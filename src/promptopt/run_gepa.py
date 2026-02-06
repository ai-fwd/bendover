import os
import tempfile
from datetime import datetime
from pathlib import Path
from typing import Iterable

import typer
import dspy
from dspy.clients import configure_cache
from dspy.teleprompt import GEPA
from dspy.utils import DummyLM
from dotenv import load_dotenv

from promptopt.bundle_store import (
    read_active_bundle_id,
    load_bundle,
    build_bundle_from_seed,
    write_bundle,
    update_active_json,
    hash_bundle,
)
from promptopt.cache import EvaluationCache
from promptopt.evaluator_client import evaluate_bundle
from promptopt.gepa_driver import load_split
from promptopt.models import EvaluationResult
from promptopt.run_store import load_run_artifact

# Load env automatically (finds .env in root)
load_dotenv()

app = typer.Typer()


class PracticeSignature(dspy.Signature):
    """
    A lightweight signature to include each practice predictor in traces.
    """

    run_ids: str = dspy.InputField(desc="Batch of run ids")
    response: str = dspy.OutputField(desc="No-op response")


class TestReflectionLM:
    """
    Deterministic reflection LM for tests.

    It looks for ADD_LINE directives in feedback and appends them to the
    current instruction extracted from the first ``` block.
    """

    def __call__(self, prompt: str, **kwargs):
        current_instruction = _extract_first_code_block(prompt)
        add_lines = []
        for line in prompt.splitlines():
            if "ADD_LINE:" in line:
                _, payload = line.split("ADD_LINE:", 1)
                payload = payload.strip()
                if payload:
                    add_lines.append(payload)

        updated = current_instruction
        for payload in add_lines:
            if payload not in updated:
                updated = updated.rstrip() + "\n" + payload

        return [f"```\n{updated.strip()}\n```"]


class BundleProgram(dspy.Module):
    def __init__(
        self,
        seed_bundle,
        runs_by_id: dict[str, object],
        bundle_root: Path,
        log_dir: Path,
        cache: EvaluationCache,
        cli_command: str,
        timeout: int,
    ):
        super().__init__()
        self.seed_bundle = seed_bundle
        self.runs_by_id = runs_by_id
        self.bundle_root = Path(bundle_root)
        self.log_dir = Path(log_dir)
        self.cache = cache
        self.cli_command = cli_command
        self.timeout = timeout

        self.practice_by_pred: dict[str, object] = {}

        for idx, practice in enumerate(seed_bundle.practices.values()):
            pred_name = f"practice_{idx}"
            predictor = dspy.Predict(PracticeSignature)
            predictor.signature.instructions = practice.body
            setattr(self, pred_name, predictor)
            self.practice_by_pred[pred_name] = practice

    def _normalize_run_ids(self, run_ids: Iterable[str] | str) -> list[str]:
        if isinstance(run_ids, str):
            return [r for r in run_ids.split(",") if r]
        return list(run_ids)

    def _current_practice_updates(self) -> dict[str, str]:
        updates = {}
        for pred_name, practice in self.practice_by_pred.items():
            predictor = getattr(self, pred_name)
            updates[practice.file_name] = predictor.signature.instructions
        return updates

    def get_practice_updates(self) -> dict[str, str]:
        return self._current_practice_updates()

    def forward(self, run_ids: list[str]):
        batch_ids = self._normalize_run_ids(run_ids)
        if not batch_ids:
            return dspy.Prediction(score=0.0, feedback="No runs provided", feedback_by_pred={})

        # Touch predictors to register trace (no-op output)
        batch_token = ",".join(batch_ids)
        for pred_name in self.practice_by_pred.keys():
            predictor = getattr(self, pred_name)
            predictor(run_ids=batch_token)

        updates = self._current_practice_updates()
        candidate_bundle = build_bundle_from_seed(self.seed_bundle, updates)
        bundle_hash = hash_bundle(candidate_bundle.practices)

        written_bundle = write_bundle(
            bundle_root=self.bundle_root,
            bundle=candidate_bundle,
            parent_id=self.seed_bundle.bundle_id,
            generation="gepa",
            metadata={},
            exist_ok=True,
        )

        evaluations = []
        for run_id in batch_ids:
            cached = self.cache.get(run_id, bundle_hash)
            if cached:
                evaluations.append(cached)
                continue

            run = self.runs_by_id[run_id]
            task_path = prepare_task_dir(run)
            result = evaluate_bundle(
                bundle_path=written_bundle.path,
                task_path=task_path,
                cli_command=self.cli_command,
                log_dir=self.log_dir,
                timeout_seconds=self.timeout,
            )
            self.cache.set(run_id, bundle_hash, result)
            evaluations.append(result)

        score = sum(e.score for e in evaluations) / max(len(evaluations), 1)

        feedback_by_pred = self._build_feedback_map(evaluations)
        overall_feedback = "\n".join(_flatten_notes(evaluations))

        return dspy.Prediction(
            score=score,
            feedback=overall_feedback,
            feedback_by_pred=feedback_by_pred,
        )

    def _build_feedback_map(self, evaluations: list[EvaluationResult]) -> dict[str, str]:
        notes_by_practice: dict[str, list[str]] = {}
        for evaluation in evaluations:
            for name, notes in evaluation.practice_attribution.notes_by_practice.items():
                notes_by_practice.setdefault(name, []).extend(notes)

        overall_notes = _flatten_notes(evaluations)
        fallback = "\n".join(overall_notes) if overall_notes else "No feedback provided."

        feedback_by_pred: dict[str, str] = {}
        for pred_name, practice in self.practice_by_pred.items():
            notes = notes_by_practice.get(practice.name)
            if notes:
                feedback_by_pred[pred_name] = "\n".join(notes)
            else:
                feedback_by_pred[pred_name] = fallback

        return feedback_by_pred


def metric_fn(gold, pred, trace=None, pred_name=None, pred_trace=None):
    score = getattr(pred, "score", 0.0)
    if pred_name:
        feedback_map = getattr(pred, "feedback_by_pred", {})
        feedback = feedback_map.get(pred_name) or getattr(pred, "feedback", None)
        if feedback is None:
            feedback = f"This trajectory got a score of {score}."
        return {"score": score, "feedback": feedback}
    return score


def _extract_first_code_block(prompt: str) -> str:
    start = prompt.find("```")
    if start == -1:
        return prompt.strip()
    start += 3
    end = prompt.find("```", start)
    if end == -1:
        return prompt[start:].strip()
    # strip optional language specifier
    block = prompt[start:end]
    if "\n" in block:
        first_line, rest = block.split("\n", 1)
        if first_line.strip() and len(first_line.split()) == 1:
            return rest.strip()
    return block.strip()


def _flatten_notes(evaluations: list[EvaluationResult]) -> list[str]:
    notes = []
    for evaluation in evaluations:
        notes.extend(evaluation.notes)
    return notes


def prepare_task_dir(run):
    temp_dir = Path(tempfile.mkdtemp(prefix=f"bendover_replay_{run.run_id}_"))
    (temp_dir / "task.md").write_text(run.goal)
    (temp_dir / "base_commit.txt").write_text(run.base_commit)
    return temp_dir


def build_batches(run_ids: list[str], batch_size: int) -> list[list[str]]:
    if not run_ids:
        return []
    if batch_size <= 0 or batch_size >= len(run_ids):
        return [run_ids]

    batches = []
    current = []
    for run_id in run_ids:
        current.append(run_id)
        if len(current) == batch_size:
            batches.append(current)
            current = []
    if current:
        batches.append(current)
    return batches


def split_devset(run_ids: list[str]) -> tuple[list[str], list[str]]:
    if len(run_ids) >= 3:
        return run_ids[:-1], run_ids[-1:]
    if len(run_ids) == 2:
        return run_ids[:1], run_ids[1:]
    return run_ids, []


def configure_base_lm(lm_mode: str, lm_model: str, cache_enabled: bool = True):
    if lm_mode not in ("dummy", "model"):
        raise ValueError(f"Unsupported lm_mode: {lm_mode}")
    if lm_mode == "dummy":
        lm = DummyLM({ "": { "response": "ok" } })
        dspy.configure(lm=lm)
        return lm

    lm = dspy.LM(lm_model, cache=cache_enabled)
    dspy.configure(lm=lm)
    return lm


def configure_reflection_lm(reflection_lm: str, cache_enabled: bool = True):
    if reflection_lm == "test":
        return TestReflectionLM()
    return dspy.LM(reflection_lm, cache=cache_enabled)


@app.command()
def main(
    cli_command: str = typer.Option(..., help="Command to invoke Bendover CLI"),
    promptopt_root: str = typer.Option(".bendover/promptopt", help="Root directory for prompt optimization data"),
    train_split: str | None = typer.Option(None, help="Path to train.txt (defaults to datasets/train.txt)"),
    timeout_seconds: int = typer.Option(900, help="Execution timeout"),
    lm_model: str = typer.Option(os.getenv("DSPY_LM_MODEL", "gpt-4o-mini"), help="LM model for dummy/model mode"),
    reflection_lm: str = typer.Option(os.getenv("DSPY_REFLECTION_MODEL", "gpt-4o-mini"), help="Reflection LM model or 'test'"),
    lm_mode: str = typer.Option("model", help="Base LM mode: model|dummy"),
    max_full_evals: int = typer.Option(10, help="GEPA max full evals"),
    batch_size: int = typer.Option(0, help="Batch size for training"),
    disable_dspy_cache: bool = typer.Option(False, help="Disable DSPy memory/disk caches"),
):
    root = Path(promptopt_root)
    bundles_root = root / "bundles"
    runs_root = root / "runs"
    datasets_root = root / "datasets"
    logs_root = root / "logs"
    cache_root = root / "cache"
    active_json = root / "active.json"

    bundles_root.mkdir(parents=True, exist_ok=True)
    logs_root.mkdir(parents=True, exist_ok=True)
    cache_root.mkdir(parents=True, exist_ok=True)

    if train_split:
        train_split_path = Path(train_split)
        if not train_split_path.is_absolute():
            train_split_path = root / train_split_path
    else:
        train_split_path = datasets_root / "train.txt"

    run_ids = load_split(str(train_split_path))
    if not run_ids:
        raise ValueError("train_split is empty")
    if len(run_ids) > 10:
        raise ValueError("train_split must contain at most 10 run_ids")

    try:
        active_bundle_id = read_active_bundle_id(active_json)
        seed_bundle_path = bundles_root / active_bundle_id
    except FileNotFoundError:
        seed_bundle_path = root

    seed_bundle = load_bundle(seed_bundle_path)

    runs = {rid: load_run_artifact(runs_root, rid) for rid in run_ids}

    train_ids, dev_ids = split_devset(run_ids)

    train_batches = build_batches(train_ids, batch_size)
    trainset = [dspy.Example(run_ids=batch).with_inputs("run_ids") for batch in train_batches]

    valset = None
    if dev_ids:
        val_batches = build_batches(dev_ids, batch_size)
        valset = [dspy.Example(run_ids=batch).with_inputs("run_ids") for batch in val_batches]

    cache_enabled = not disable_dspy_cache
    if disable_dspy_cache:
        configure_cache(enable_disk_cache=False, enable_memory_cache=False)

    configure_base_lm(lm_mode, lm_model, cache_enabled=cache_enabled)
    reflection_lm_instance = configure_reflection_lm(reflection_lm, cache_enabled=cache_enabled)

    cache = EvaluationCache(cache_root)

    program = BundleProgram(
        seed_bundle=seed_bundle,
        runs_by_id=runs,
        bundle_root=bundles_root,
        log_dir=logs_root,
        cache=cache,
        cli_command=cli_command,
        timeout=timeout_seconds,
    )

    reflection_minibatch_size = min(3, max(len(trainset), 1))

    teleprompter = GEPA(
        metric=metric_fn,
        max_full_evals=max_full_evals,
        reflection_minibatch_size=reflection_minibatch_size,
        reflection_lm=reflection_lm_instance,
        log_dir=str(logs_root),
        track_stats=True,
    )

    compiled_program = teleprompter.compile(
        program,
        trainset=trainset,
        valset=valset,
    )

    seed_updates = {name: practice.body for name, practice in seed_bundle.practices.items()}
    best_program = compiled_program

    if reflection_lm == "test" and hasattr(compiled_program, "detailed_results"):
        candidates = getattr(compiled_program.detailed_results, "candidates", [])
        for candidate in candidates:
            if not hasattr(candidate, "get_practice_updates"):
                continue
            candidate_updates = candidate.get_practice_updates()
            if any(candidate_updates.get(k) != seed_updates.get(k) for k in seed_updates.keys()):
                best_program = candidate
                break

    best_updates = best_program.get_practice_updates()

    best_bundle = build_bundle_from_seed(seed_bundle, best_updates)
    best_hash = hash_bundle(best_bundle.practices)

    written_bundle = write_bundle(
        bundle_root=bundles_root,
        bundle=best_bundle,
        parent_id=seed_bundle.bundle_id,
        generation="gepa",
        metadata={
            "trainRunIds": train_ids,
            "devRunIds": dev_ids,
        },
        exist_ok=True,
    )

    # Final score across all runs (cached if already evaluated)
    evaluations = []
    for run_id in run_ids:
        cached = cache.get(run_id, best_hash)
        if cached:
            evaluations.append(cached)
            continue
        run = runs[run_id]
        task_path = prepare_task_dir(run)
        result = evaluate_bundle(
            bundle_path=written_bundle.path,
            task_path=task_path,
            cli_command=cli_command,
            log_dir=logs_root,
            timeout_seconds=timeout_seconds,
        )
        cache.set(run_id, best_hash, result)
        evaluations.append(result)

    final_score = sum(e.score for e in evaluations) / max(len(evaluations), 1)

    update_active_json(
        active_json,
        written_bundle.bundle_id,
        {
            "updatedAt": datetime.utcnow().isoformat() + "Z",
            "parentBundleId": seed_bundle.bundle_id,
            "score": final_score,
            "trainRunIds": train_ids,
            "devRunIds": dev_ids,
        },
    )

    print("Optimization Complete.")
    print(f"Best bundle: {written_bundle.bundle_id}")
    print(f"Final score: {final_score}")


if __name__ == "__main__":
    app()

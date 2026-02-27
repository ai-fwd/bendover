import os
import json
import shutil
import tempfile
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable, Any

import typer
import dspy
from dspy.clients import configure_cache
from dspy.clients.base_lm import BaseLM
from dspy.teleprompt import GEPA
from dspy.utils import DummyLM
from dspy.utils.callback import BaseCallback
from dotenv import load_dotenv

from promptopt.bundle_store import (
    ensure_active_bundle,
    load_bundle,
    build_bundle_from_seed,
    write_bundle,
    update_active_json,
    hash_bundle,
)
from promptopt.cache import EvaluationCache
from promptopt.evaluator_client import evaluate_bundle
from promptopt.gepa_driver import load_split
from promptopt.models import Bundle, EvaluationResult, PracticeFile, RunArtifact
from promptopt.run_store import load_run_artifact

# Load env automatically (finds .env in root)
load_dotenv()

app = typer.Typer()

REFLECTION_TRANSCRIPT_ENABLED = True
EVAL_HEARTBEAT_SECONDS = 15
TRANSCRIPT_PREVIEW_LIMIT = 240


def _utc_timestamp() -> str:
    return datetime.utcnow().isoformat() + "Z"


def _status(message: str) -> None:
    print(f"[promptopt][{_utc_timestamp()}] {message}", flush=True)


def _compact_single_line(text: str | None, limit: int = TRANSCRIPT_PREVIEW_LIMIT) -> str:
    if not text:
        return "(none)"
    compact = (
        str(text)
        .replace("\r\n", "\n")
        .replace("\r", "\n")
        .replace("\n", "\\n")
        .strip()
    )
    if len(compact) <= limit:
        return compact
    return f"{compact[:limit]}...(truncated)"


def _safe_json(value: Any) -> str:
    try:
        return json.dumps(value, indent=2, sort_keys=True, default=str)
    except TypeError:
        return str(value)


def _stringify_outputs(outputs: Any) -> str:
    if outputs is None:
        return "(none)"
    if isinstance(outputs, list):
        return "\n".join(str(item) for item in outputs)
    if isinstance(outputs, dict):
        return _safe_json(outputs)
    return str(outputs)


def _append_block(lines: list[str], language: str, value: str | None) -> None:
    lines.append(f"```{language}")
    lines.append(value or "")
    lines.append("```")


def _extract_reflection_input(prompt: Any, messages: Any) -> str:
    if isinstance(prompt, str) and prompt.strip():
        return prompt
    if isinstance(messages, list):
        parts: list[str] = []
        for message in messages:
            if not isinstance(message, dict):
                continue
            role = str(message.get("role", "unknown"))
            content = str(message.get("content", ""))
            if content:
                parts.append(f"[{role}] {content}")
        if parts:
            return "\n".join(parts)
    return "(none)"


@dataclass
class ReflectionTranscriptEvent:
    index: int
    call_id: str
    started_at_utc: str
    prompt: str | None
    messages: Any
    kwargs: dict[str, Any]
    ended_at_utc: str | None = None
    duration_ms: int | None = None
    outputs: Any = None
    exception: str | None = None


class ReflectionTranscriptRecorder:
    def __init__(self, enabled: bool = True):
        self.enabled = enabled
        self._events: list[ReflectionTranscriptEvent] = []
        self._event_index_by_call_id: dict[str, int] = {}
        self._started_monotonic_by_call_id: dict[str, float] = {}

    def start(self, call_id: str, prompt: Any, messages: Any, kwargs: Any) -> None:
        if not self.enabled:
            return

        event = ReflectionTranscriptEvent(
            index=len(self._events) + 1,
            call_id=call_id,
            started_at_utc=_utc_timestamp(),
            prompt=prompt if isinstance(prompt, str) else None,
            messages=messages,
            kwargs=dict(kwargs or {}),
        )
        self._events.append(event)
        self._event_index_by_call_id[call_id] = len(self._events) - 1
        self._started_monotonic_by_call_id[call_id] = time.monotonic()

        input_text = _extract_reflection_input(prompt, messages)
        _status(
            f"[reflection][prompt] call={event.index} chars={len(input_text)} preview={_compact_single_line(input_text)}"
        )

    def end(self, call_id: str, outputs: Any, exception: Exception | None = None) -> None:
        if not self.enabled:
            return

        if call_id not in self._event_index_by_call_id:
            self.start(call_id=call_id, prompt=None, messages=None, kwargs={})

        event_index = self._event_index_by_call_id[call_id]
        event = self._events[event_index]

        now = time.monotonic()
        started = self._started_monotonic_by_call_id.pop(call_id, now)
        duration_ms = int((now - started) * 1000)

        event.ended_at_utc = _utc_timestamp()
        event.duration_ms = duration_ms
        event.outputs = outputs
        if exception is not None:
            event.exception = f"{type(exception).__name__}: {exception}"

        output_text = _stringify_outputs(outputs)
        _status(
            f"[reflection][output] call={event.index} chars={len(output_text)} duration_ms={duration_ms} "
            f"preview={_compact_single_line(output_text)}"
        )
        if event.exception:
            _status(f"[reflection][failure] call={event.index} detail={_compact_single_line(event.exception)}")

    def write_markdown(self, path: Path) -> None:
        if not self.enabled:
            return

        path.parent.mkdir(parents=True, exist_ok=True)
        lines = [
            "# Reflection LM Transcript",
            "",
            f"captured_at_utc: {_utc_timestamp()}",
            f"calls: {len(self._events)}",
            "",
        ]

        for event in self._events:
            lines.extend([
                f"## Call {event.index}",
                "",
                f"call_id: {event.call_id}",
                f"started_at_utc: {event.started_at_utc}",
                f"ended_at_utc: {event.ended_at_utc or '(none)'}",
                f"duration_ms: {event.duration_ms if event.duration_ms is not None else '(none)'}",
                "",
                "### Prompt",
                "",
            ])
            _append_block(lines, "text", event.prompt or "")
            lines.extend([
                "",
                "### Messages",
                "",
            ])
            _append_block(lines, "json", _safe_json(event.messages))
            lines.extend([
                "",
                "### Kwargs",
                "",
            ])
            _append_block(lines, "json", _safe_json(event.kwargs))
            lines.extend([
                "",
                "### Output",
                "",
            ])
            _append_block(lines, "text", _stringify_outputs(event.outputs))
            if event.exception:
                lines.extend([
                    "",
                    "### Exception",
                    "",
                ])
                _append_block(lines, "text", event.exception)
            lines.append("")

        path.write_text("\n".join(lines))
        _status(f"reflection-transcript written path={path} calls={len(self._events)}")


class ReflectionLMTranscriptCallback(BaseCallback):
    def __init__(self, recorder: ReflectionTranscriptRecorder):
        self._recorder = recorder

    def on_lm_start(
        self,
        call_id: str,
        instance: Any,
        inputs: dict[str, Any],
    ):
        del instance
        self._recorder.start(
            call_id=call_id,
            prompt=inputs.get("prompt"),
            messages=inputs.get("messages"),
            kwargs=inputs.get("kwargs"),
        )

    def on_lm_end(
        self,
        call_id: str,
        outputs: Any | None,
        exception: Exception | None = None,
    ):
        self._recorder.end(call_id=call_id, outputs=outputs, exception=exception)


class PracticeSignature(dspy.Signature):
    """
    A lightweight signature to include each practice predictor in traces.

    DSPy/GEPA needs predictors in the execution trace so it can reflect on them.
    This signature is intentionally minimal and acts like a "no-op" predictor
    that still produces a trace entry.
    """

    run_context: str = dspy.InputField(desc="Structured replay context for a batch of runs")
    response: str = dspy.OutputField(desc="No-op response")


class TestReflectionLM:
    """
    Deterministic reflection LM for tests.

    It looks for ADD_LINE directives in feedback and appends them to the
    current instruction extracted from the first ``` block.
    """

    def __init__(self, transcript_recorder: ReflectionTranscriptRecorder | None = None):
        self._transcript_recorder = transcript_recorder
        self._call_count = 0

    def __call__(self, prompt: str, **kwargs) -> list[str]:
        self._call_count += 1
        call_id = f"test_reflection_{self._call_count}"
        if self._transcript_recorder is not None:
            self._transcript_recorder.start(
                call_id=call_id,
                prompt=prompt,
                messages=kwargs.get("messages"),
                kwargs=kwargs,
            )

        try:
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

            result = [f"```\n{updated.strip()}\n```"]
            if self._transcript_recorder is not None:
                self._transcript_recorder.end(call_id=call_id, outputs=result, exception=None)
            return result
        except Exception as exc:
            if self._transcript_recorder is not None:
                self._transcript_recorder.end(call_id=call_id, outputs=None, exception=exc)
            raise


class BundleProgram(dspy.Module):
    _TEXT_LIMIT = 1200
    _OUTPUT_LIMIT = 800

    def __init__(
        self,
        seed_bundle: Bundle,
        runs_by_id: dict[str, RunArtifact],
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

        # Map predictor name -> PracticeFile for later feedback attribution.
        self.practice_by_pred: dict[str, PracticeFile] = {}
        self.file_by_alias: dict[str, str] = {}
        self._fixed_updates: dict[str, str] = {
            file_name: practice.body for file_name, practice in seed_bundle.practices.items()
        }
        self._mutable_files: set[str] = set(seed_bundle.practices.keys())

        for idx, practice in enumerate(seed_bundle.practices.values()):
            pred_name = f"practice_{idx}"
            predictor = dspy.Predict(PracticeSignature.with_instructions(practice.body))
            setattr(self, pred_name, predictor)
            self.practice_by_pred[pred_name] = practice
            self._register_alias(practice.name, practice.file_name)
            self._register_alias(practice.file_name, practice.file_name)
            self._register_alias(Path(practice.file_name).stem, practice.file_name)

    def _normalize_run_ids(self, run_ids: Iterable[str] | str) -> list[str]:
        if isinstance(run_ids, str):
            return [r for r in run_ids.split(",") if r]
        return list(run_ids)

    def _register_alias(self, alias: str, file_name: str) -> None:
        key = alias.strip().lower()
        if key and key not in self.file_by_alias:
            self.file_by_alias[key] = file_name

    def _resolve_practice_file(self, practice_name: str) -> str | None:
        return self.file_by_alias.get(practice_name.strip().lower())

    def _extract_targeted_files(self, run_id: str, evaluation: EvaluationResult) -> set[str]:
        attribution = evaluation.practice_attribution
        attribution_names = list(attribution.notes_by_practice.keys())

        if not attribution_names:
            print(f"[GEPA] No practice notes for run {run_id}; skipping mutations for this run.")
            return set()

        targeted: set[str] = set()
        for name in attribution_names:
            resolved = self._resolve_practice_file(name)
            if resolved:
                targeted.add(resolved)
            else:
                print(f"[GEPA] Unrecognized practice attribution '{name}' for run {run_id}; ignoring.")
        return targeted

    def _collect_targeted_files(self, evaluations_by_run: list[tuple[str, EvaluationResult]]) -> set[str]:
        targeted: set[str] = set()
        for run_id, evaluation in evaluations_by_run:
            targeted.update(self._extract_targeted_files(run_id, evaluation))
        return targeted

    def _current_practice_updates(self) -> dict[str, str]:
        updates = {}
        for pred_name, practice in self.practice_by_pred.items():
            if practice.file_name in self._mutable_files:
                predictor = getattr(self, pred_name)
                updates[practice.file_name] = predictor.signature.instructions
            else:
                updates[practice.file_name] = self._fixed_updates.get(practice.file_name, practice.body)
        return updates

    def get_practice_updates(self) -> dict[str, str]:
        return self._current_practice_updates()

    def snapshot_mutation_state(self) -> tuple[dict[str, str], set[str]]:
        """Capture mutable GEPA state so preflight evaluation can be rolled back."""
        return dict(self._fixed_updates), set(self._mutable_files)

    def restore_mutation_state(self, fixed_updates: dict[str, str], mutable_files: set[str]) -> None:
        """Restore mutable GEPA state after preflight evaluation."""
        self._fixed_updates = dict(fixed_updates)
        self._mutable_files = set(mutable_files)

    def _truncate(self, value: str | None, limit: int) -> str:
        if not value:
            return "(none)"
        text = value.strip()
        if len(text) <= limit:
            return text
        return f"{text[:limit]}...(truncated)"

    def _build_outputs_summary(self, run: RunArtifact) -> str:
        if not run.outputs:
            return "(none)"
        lines = []
        for phase in sorted(run.outputs.keys()):
            lines.append(f"[{phase}]")
            lines.append(self._truncate(run.outputs[phase], self._OUTPUT_LIMIT))
        return "\n".join(lines)

    def _build_run_context(self, batch_ids: list[str]) -> str:
        sections = []
        for run_id in batch_ids:
            run = self.runs_by_id[run_id]
            test_signal = run.dotnet_test if run.dotnet_test is not None else run.dotnet_test_error
            build_signal = run.dotnet_build if run.dotnet_build is not None else run.dotnet_build_error
            section = [
                f"run_id: {run.run_id}",
                f"goal:\n{self._truncate(run.goal, self._TEXT_LIMIT)}",
                f"base_commit: {run.base_commit}",
                f"git_diff:\n{self._truncate(run.git_diff, self._TEXT_LIMIT)}",
                f"dotnet_test:\n{self._truncate(test_signal, self._TEXT_LIMIT)}",
                f"dotnet_build:\n{self._truncate(build_signal, self._TEXT_LIMIT)}",
                f"outputs:\n{self._build_outputs_summary(run)}",
            ]
            sections.append("\n".join(section))
        return "\n\n---\n\n".join(sections)

    def forward(self, run_ids: list[str]) -> dspy.Prediction:
        """
        GEPA calls this method during search.

        It builds a candidate bundle from the current predictor instructions,
        replays the run(s) via the external CLI, and returns a score + feedback.
        """
        batch_ids = self._normalize_run_ids(run_ids)
        if not batch_ids:
            return dspy.Prediction(score=0.0, feedback="No runs provided", feedback_by_pred={})

        # Build a candidate bundle from the latest predictor instructions.
        updates = self._current_practice_updates()
        candidate_bundle = build_bundle_from_seed(self.seed_bundle, updates)
        bundle_hash = hash_bundle(candidate_bundle.practices, candidate_bundle.passthrough_files)

        # Persist candidate bundle to disk so the CLI can read it.
        written_bundle = write_bundle(
            bundle_root=self.bundle_root,
            bundle=candidate_bundle,
            parent_id=self.seed_bundle.bundle_id,
            generation="gepa",
            metadata={},
            exist_ok=True,
        )

        evaluations_by_run: list[tuple[str, EvaluationResult]] = []
        for run_id in batch_ids:
            cached = self.cache.get(run_id, bundle_hash)
            if cached:
                _status(f"replay-eval cache-hit run={run_id} score={cached.score}")
                evaluations_by_run.append((run_id, cached))
                continue

            # Replay the run using the external evaluator (PromptOpt.CLI).
            _status(f"replay-eval start run={run_id} bundle={written_bundle.bundle_id}")
            run = self.runs_by_id[run_id]
            task_path = prepare_task_dir(run)
            result = evaluate_bundle(
                bundle_path=written_bundle.path,
                task_path=task_path,
                cli_command=self.cli_command,
                log_dir=self.log_dir,
                timeout_seconds=self.timeout,
                run_label=run_id,
            )
            _status(f"replay-eval done run={run_id} pass={result.passed} score={result.score}")
            self.cache.set(run_id, bundle_hash, result)
            evaluations_by_run.append((run_id, result))

        evaluations = [evaluation for _, evaluation in evaluations_by_run]

        # Aggregate per-run scores into a single GEPA fitness score.
        score = sum(e.score for e in evaluations) / max(len(evaluations), 1)
        targeted_files = self._collect_targeted_files(evaluations_by_run)

        # Route per-practice feedback only for targeted predictors.
        feedback_by_pred = self._build_feedback_map(evaluations_by_run, targeted_files)
        attribution_by_run = self._build_attribution_diagnostics(evaluations_by_run)
        overall_feedback = "\n".join(_flatten_notes(evaluations))

        run_context = self._build_run_context(batch_ids)
        for pred_name, practice in self.practice_by_pred.items():
            if practice.file_name not in targeted_files:
                continue
            predictor = getattr(self, pred_name)
            predictor(run_context=run_context)

        for file_name in targeted_files:
            if file_name in updates:
                self._fixed_updates[file_name] = updates[file_name]
        self._mutable_files = targeted_files

        return dspy.Prediction(
            score=score,
            feedback=overall_feedback,
            feedback_by_pred=feedback_by_pred,
            attribution_by_run=attribution_by_run,
        )

    def _build_feedback_map(
        self,
        evaluations_by_run: list[tuple[str, EvaluationResult]],
        targeted_files: set[str],
    ) -> dict[str, str]:
        notes_by_file: dict[str, list[str]] = {}
        summary_lines: list[str] = []
        for run_id, evaluation in evaluations_by_run:
            flags = ", ".join(evaluation.flags) if evaluation.flags else "none"
            notes = " | ".join(evaluation.notes) if evaluation.notes else "none"
            summary_lines.append(
                f"[run={run_id}] pass={evaluation.passed} score={evaluation.score} flags={flags} notes={notes}"
            )
            for name, notes_for_practice in evaluation.practice_attribution.notes_by_practice.items():
                resolved = self._resolve_practice_file(name)
                if resolved:
                    prefixed = [f"[run={run_id}] {note}" for note in notes_for_practice]
                    notes_by_file.setdefault(resolved, []).extend(prefixed)

        feedback_by_pred: dict[str, str] = {}
        for pred_name, practice in self.practice_by_pred.items():
            if practice.file_name not in targeted_files:
                continue

            practice_notes = notes_by_file.get(practice.file_name, [])
            blocks = [
                "evaluation_summary:",
                "\n".join(summary_lines) if summary_lines else "no evaluations",
                "practice_notes:",
                "\n".join(practice_notes) if practice_notes else "none",
            ]
            feedback_by_pred[pred_name] = "\n".join(blocks)

        return feedback_by_pred

    def _build_attribution_diagnostics(
        self,
        evaluations_by_run: list[tuple[str, EvaluationResult]],
    ) -> dict[str, dict[str, object]]:
        diagnostics: dict[str, dict[str, object]] = {}
        for run_id, evaluation in evaluations_by_run:
            attribution = evaluation.practice_attribution
            diagnostics[run_id] = {
                "selected_practices": list(attribution.selected_practices),
                "offending_practices": list(attribution.offending_practices),
                "notes_by_practice_keys": sorted(attribution.notes_by_practice.keys()),
                "flags": list(evaluation.flags),
                "notes": list(evaluation.notes),
                "score": evaluation.score,
                "passed": evaluation.passed,
            }
        return diagnostics


def metric_fn(gold, pred, trace=None, pred_name=None, pred_trace=None):
    """
    GEPA metric function.

    When GEPA asks for feedback on a *specific predictor*, we return the
    predictor-targeted feedback. Otherwise, we return the overall score.
    """
    score = getattr(pred, "score", 0.0)
    if pred_name:
        feedback_map = getattr(pred, "feedback_by_pred", {})
        feedback = feedback_map.get(pred_name)
        if feedback is None:
            feedback = "No targeted feedback for this predictor."
        return {"score": score, "feedback": feedback}
    return score


def select_feedback_targeted_components(
    state: Any,
    trajectories: list[dict[str, Any]],
    subsample_scores: list[float],
    candidate_idx: int,
    candidate: dict[str, str],
) -> list[str]:
    """
    Select only predictors that received targeted feedback in this minibatch.

    This keeps GEPA aligned with notes_by_practice-driven attribution and avoids
    attempting reflection on predictors with no reflective dataset entries.
    """
    del subsample_scores

    targeted: set[str] = set()
    for trajectory in trajectories or []:
        prediction = trajectory.get("prediction")
        feedback_map = getattr(prediction, "feedback_by_pred", None)
        if not isinstance(feedback_map, dict):
            continue
        for pred_name in feedback_map.keys():
            if pred_name in candidate:
                targeted.add(pred_name)

    if targeted:
        return [name for name in state.list_of_named_predictors if name in targeted and name in candidate]

    # Fallback to round-robin behavior when no targeted feedback is available.
    pid = state.named_predictor_id_to_update_next_for_program_candidate[candidate_idx]
    state.named_predictor_id_to_update_next_for_program_candidate[candidate_idx] = (pid + 1) % len(
        state.list_of_named_predictors
    )
    return [state.list_of_named_predictors[pid]]


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


def prepare_task_dir(run: RunArtifact) -> Path:
    """
    Build a minimal "task" directory for the CLI replay.

    The CLI expects a task.md and base_commit.txt file.
    """
    temp_dir = Path(tempfile.mkdtemp(prefix=f"bendover_replay_{run.run_id}_"))
    (temp_dir / "task.md").write_text(run.goal)
    (temp_dir / "base_commit.txt").write_text(run.base_commit)
    previous_run_result_path = run.run_dir / "run_result.json"
    if previous_run_result_path.exists():
        (temp_dir / "previous_run_results.json").write_bytes(previous_run_result_path.read_bytes())
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


def _normalize_batch_run_ids(example: dspy.Example) -> list[str]:
    run_ids = getattr(example, "run_ids", None)
    if run_ids is None:
        try:
            run_ids = example["run_ids"]
        except (KeyError, TypeError):
            run_ids = []

    if isinstance(run_ids, str):
        return [run_id for run_id in run_ids.split(",") if run_id]
    return [str(run_id) for run_id in run_ids]


def _format_preflight_diagnostic(
    batch_ids: list[str],
    attribution_by_run: dict[str, dict[str, object]],
) -> str:
    def _csv(value: object | None) -> str:
        if not isinstance(value, list) or not value:
            return "none"
        return ", ".join(str(item) for item in value)

    lines = [
        "GEPA attribution preflight failed: no practice-targeted feedback was produced for the first training batch.",
        "GEPA requires evaluator practice_attribution.notes_by_practice entries before reflection can begin.",
        f"batch_run_ids: {', '.join(batch_ids) if batch_ids else 'none'}",
        "",
        "run_diagnostics:",
    ]

    for run_id in batch_ids:
        details = attribution_by_run.get(run_id, {})
        passed = details.get("passed", False)
        score = details.get("score", 0.0)
        lines.append(f"- run_id={run_id} pass={passed} score={score}")
        lines.append(f"  selected_practices: {_csv(details.get('selected_practices'))}")
        lines.append(f"  offending_practices: {_csv(details.get('offending_practices'))}")
        lines.append(f"  notes_by_practice_keys: {_csv(details.get('notes_by_practice_keys'))}")
        lines.append(f"  flags: {_csv(details.get('flags'))}")
        lines.append(f"  notes: {_csv(details.get('notes'))}")

    lines.extend(
        [
            "",
            "likely_causes:",
            "- Practice-specific rules did not run or did not fail for the selected practices.",
            "- Rule-to-practice naming does not match the convention (practice_name <-> PracticeNameRule).",
            "- Evaluator emitted only global notes/flags and no notes_by_practice attribution.",
        ]
    )
    return "\n".join(lines)


def run_attribution_preflight(program: BundleProgram, trainset: list[dspy.Example]) -> None:
    """
    Validate that the first batch produces practice-targeted feedback.

    Without notes_by_practice attribution, GEPA cannot construct reflective datasets.
    """
    if not trainset:
        raise ValueError("trainset is empty")

    batch_ids = _normalize_batch_run_ids(trainset[0])
    fixed_updates, mutable_files = program.snapshot_mutation_state()
    try:
        preflight_prediction = program(run_ids=batch_ids)
    finally:
        program.restore_mutation_state(fixed_updates, mutable_files)

    feedback_by_pred = getattr(preflight_prediction, "feedback_by_pred", {}) or {}
    if feedback_by_pred:
        return

    attribution_by_run = getattr(preflight_prediction, "attribution_by_run", {}) or {}
    raise RuntimeError(_format_preflight_diagnostic(batch_ids, attribution_by_run))


def configure_base_lm() -> BaseLM:
    """
    Base LM is used only to generate DSPy traces for predictors.
    We intentionally keep this as DummyLM because evaluation happens via PromptOpt.CLI.
    """
    lm = DummyLM({ "": { "response": "ok" } })
    dspy.configure(lm=lm)
    return lm


def configure_reflection_lm(
    reflection_lm: str,
    cache_enabled: bool = True,
    transcript_recorder: ReflectionTranscriptRecorder | None = None,
) -> BaseLM | TestReflectionLM:
    """
    Reflection LM is used by GEPA to propose new instructions.
    """
    if reflection_lm == "test":
        return TestReflectionLM(transcript_recorder=transcript_recorder)

    callbacks = [ReflectionLMTranscriptCallback(transcript_recorder)] if transcript_recorder is not None else None
    return dspy.LM(reflection_lm, cache=cache_enabled, callbacks=callbacks)


def _run_optimization(
    cli_command: str | None = typer.Option(
        None,
        help="Command to invoke Bendover CLI (defaults to PROMPTOPT_CLI_COMMAND env var)",
    ),
    promptopt_root: str = typer.Option(".bendover/promptopt", help="Root directory for prompt optimization data"),
    train_split: str | None = typer.Option(None, help="Path to train.txt (defaults to datasets/train.txt)"),
    timeout_seconds: int = typer.Option(900, help="Execution timeout"),
    reflection_lm: str = typer.Option(os.getenv("DSPY_REFLECTION_MODEL", "gpt-4o-mini"), help="Reflection LM model or 'test'"),
    max_full_evals: int = typer.Option(10, help="GEPA max full evals"),
    batch_size: int = typer.Option(0, help="Batch size for training"),
    disable_dspy_cache: bool = typer.Option(False, help="Disable DSPy memory/disk caches"),
):
    """
    Entry point for GEPA prompt optimization.

    High-level flow:
    1) Load run IDs from datasets/train.txt.
    2) Resolve the active bundle (bootstrap seed from root .bendover content if needed).
    3) Build a DSPy program with one predictor per practice file.
    4) Let GEPA reflect on traces + scores to evolve instructions.
    5) Write the best bundle and update active.json.
    """
    resolved_cli_command = (cli_command or os.getenv("PROMPTOPT_CLI_COMMAND", "")).strip()
    if not resolved_cli_command:
        raise typer.BadParameter(
            "Missing CLI command. Set PROMPTOPT_CLI_COMMAND in your environment or pass --cli-command."
        )

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

    # Resolve dataset split path. Default: <promptopt_root>/datasets/train.txt
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

    # Resolve the seed bundle. Missing active.json triggers seed bootstrap from root .bendover content.
    active_bundle_id = ensure_active_bundle(root)
    seed_bundle_path = bundles_root / active_bundle_id

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
    _status(
        f"optimization-start root={root} run_count={len(run_ids)} "
        f"reflection_transcript_enabled={REFLECTION_TRANSCRIPT_ENABLED} heartbeat_seconds={EVAL_HEARTBEAT_SECONDS}"
    )

    # DSPy cache can be disabled for deterministic integration tests.
    if disable_dspy_cache:
        configure_cache(enable_disk_cache=False, enable_memory_cache=False)

    reflection_transcript_recorder = ReflectionTranscriptRecorder(enabled=REFLECTION_TRANSCRIPT_ENABLED)
    reflection_transcript_path = logs_root / "reflection_transcript.md"

    try:
        configure_base_lm()
        reflection_lm_instance = configure_reflection_lm(
            reflection_lm,
            cache_enabled=cache_enabled,
            transcript_recorder=reflection_transcript_recorder,
        )

        cache = EvaluationCache(cache_root)

        program = BundleProgram(
            seed_bundle=seed_bundle,
            runs_by_id=runs,
            bundle_root=bundles_root,
            log_dir=logs_root,
            cache=cache,
            cli_command=resolved_cli_command,
            timeout=timeout_seconds,
        )

        _status("attribution-preflight start")
        try:
            run_attribution_preflight(program, trainset)
        except RuntimeError as exc:
            typer.echo(str(exc), err=True)
            raise typer.Exit(code=1)
        _status("attribution-preflight done")

        reflection_minibatch_size = min(3, max(len(trainset), 1))

        # GEPA uses the reflection LM + traces from the base LM to propose new instructions.
        teleprompter = GEPA(
            metric=metric_fn,
            max_full_evals=max_full_evals,
            reflection_minibatch_size=reflection_minibatch_size,
            reflection_lm=reflection_lm_instance,
            component_selector=select_feedback_targeted_components,
            log_dir=str(logs_root),
            track_stats=True,
        )

        _status(
            f"gepa-compile start train_batches={len(trainset)} "
            f"dev_batches={0 if valset is None else len(valset)} max_full_evals={max_full_evals}"
        )
        compiled_program = teleprompter.compile(
            program,
            trainset=trainset,
            valset=valset,
        )
        _status("gepa-compile done")

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
        best_hash = hash_bundle(best_bundle.practices, best_bundle.passthrough_files)

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
                _status(f"final-score cache-hit run={run_id} score={cached.score}")
                evaluations.append(cached)
                continue
            run = runs[run_id]
            task_path = prepare_task_dir(run)
            _status(f"final-score start run={run_id}")
            result = evaluate_bundle(
                bundle_path=written_bundle.path,
                task_path=task_path,
                cli_command=resolved_cli_command,
                log_dir=logs_root,
                timeout_seconds=timeout_seconds,
                run_label=run_id,
            )
            _status(f"final-score done run={run_id} pass={result.passed} score={result.score}")
            cache.set(run_id, best_hash, result)
            evaluations.append(result)

        final_score = sum(e.score for e in evaluations) / max(len(evaluations), 1)
        _status(f"optimization-final-score score={final_score}")

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
    finally:
        try:
            reflection_transcript_recorder.write_markdown(reflection_transcript_path)
        except Exception as exc:
            _status(f"reflection-transcript write failed path={reflection_transcript_path} error={exc}")


def _safe_remove_path(path: Path) -> None:
    if path.is_symlink():
        path.unlink(missing_ok=True)
        return
    if not path.exists():
        return
    if path.is_dir():
        shutil.rmtree(path)
        return
    path.unlink(missing_ok=True)


def _run_clean(promptopt_root: str) -> None:
    root = Path(promptopt_root)
    active_json = root / "active.json"
    logs_root = root / "logs"
    bundles_root = root / "bundles"
    cache_evals_root = root / "cache" / "evals"

    _safe_remove_path(active_json)

    if logs_root.exists() and logs_root.is_dir():
        for child in list(logs_root.iterdir()):
            _safe_remove_path(child)

    if bundles_root.exists() and bundles_root.is_dir():
        for child in list(bundles_root.glob("gen*")):
            _safe_remove_path(child)

    if cache_evals_root.exists() and cache_evals_root.is_dir():
        for child in list(cache_evals_root.iterdir()):
            _safe_remove_path(child)


@app.callback(invoke_without_command=True)
def main(
    ctx: typer.Context,
    cli_command: str | None = typer.Option(
        None,
        help="Command to invoke Bendover CLI (defaults to PROMPTOPT_CLI_COMMAND env var)",
    ),
    promptopt_root: str = typer.Option(".bendover/promptopt", help="Root directory for prompt optimization data"),
    train_split: str | None = typer.Option(None, help="Path to train.txt (defaults to datasets/train.txt)"),
    timeout_seconds: int = typer.Option(900, help="Execution timeout"),
    reflection_lm: str = typer.Option(
        os.getenv("DSPY_REFLECTION_MODEL", "gpt-4o-mini"), help="Reflection LM model or 'test'"
    ),
    max_full_evals: int = typer.Option(10, help="GEPA max full evals"),
    batch_size: int = typer.Option(0, help="Batch size for training"),
    disable_dspy_cache: bool = typer.Option(False, help="Disable DSPy memory/disk caches"),
):
    if ctx.invoked_subcommand is not None:
        return

    _run_optimization(
        cli_command=cli_command,
        promptopt_root=promptopt_root,
        train_split=train_split,
        timeout_seconds=timeout_seconds,
        reflection_lm=reflection_lm,
        max_full_evals=max_full_evals,
        batch_size=batch_size,
        disable_dspy_cache=disable_dspy_cache,
    )


@app.command("clean")
def clean(
    promptopt_root: str = typer.Option(".bendover/promptopt", help="Root directory for prompt optimization data"),
):
    _run_clean(promptopt_root)


def cli() -> None:
    app()


if __name__ == "__main__":
    cli()

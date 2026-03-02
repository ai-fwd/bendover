from __future__ import annotations

import logging
import random
import re
import threading
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path
from types import MethodType
from typing import Any

import dspy
from dspy.primitives import Example, Module, Prediction
from dspy.teleprompt.bootstrap_trace import FailedPrediction
from dspy.teleprompt.gepa.gepa import AUTO_RUN_SETTINGS, DspyGEPAResult
from dspy.teleprompt.gepa.gepa_utils import DspyAdapter
from dspy.utils.exceptions import AdapterParseError
from dspy.utils.parallelizer import ParallelExecutor
from gepa import EvaluationBatch, GEPAResult, optimize
from gepa.logging.logger import LoggerProtocol
from gepa.strategies.instruction_proposal import InstructionProposalSignature

from promptopt.status import emit_status_event


TRANSCRIPT_PREVIEW_LIMIT = 240

_SELECTED_PROGRAM_RE = re.compile(r"Iteration (\d+): Selected program (\d+) score: ([0-9.]+)")
_BETTER_PROGRAM_RE = re.compile(r"Iteration (\d+): Found a better program on the valset with score ([0-9.]+)")
_BEST_SCORE_RE = re.compile(r"Iteration (\d+): Best score on valset: ([0-9.]+)")


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


@dataclass
class GepaRuntimeContext:
    current_iteration: int | None = None
    total_metric_calls: int | None = None


class RawLogWriter:
    def __init__(self, path: Path):
        self._path = Path(path)
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._handle = self._path.open("a", encoding="utf-8")
        self._lock = threading.Lock()

    @property
    def path(self) -> str:
        return str(self._path)

    def write(self, source: str, message: str) -> None:
        line = f"[{source}] {message.rstrip()}"
        with self._lock:
            self._handle.write(line + "\n")
            self._handle.flush()

    def close(self) -> None:
        with self._lock:
            self._handle.close()


class PromptOptGepaLogger(LoggerProtocol):
    def __init__(self, runtime: GepaRuntimeContext, raw_log: RawLogWriter):
        self._runtime = runtime
        self._raw_log = raw_log

    def log(self, message: str) -> None:
        self._raw_log.write("gepa", message)
        self._parse(message)

    def _parse(self, message: str) -> None:
        stripped = message.strip()

        selected_match = _SELECTED_PROGRAM_RE.search(stripped)
        if selected_match:
            iteration = int(selected_match.group(1))
            selected_program_idx = int(selected_match.group(2))
            selected_program_score = float(selected_match.group(3))
            self._runtime.current_iteration = iteration
            emit_status_event(
                "gepa_candidate_selected",
                summary=(
                    f"iteration {iteration} selected program {selected_program_idx} "
                    f"score={selected_program_score}"
                ),
                iteration=iteration,
                selected_program_idx=selected_program_idx,
                selected_program_score=selected_program_score,
            )
            return

        better_match = _BETTER_PROGRAM_RE.search(stripped)
        if better_match:
            iteration = int(better_match.group(1))
            score = float(better_match.group(2).rstrip("."))
            emit_status_event(
                "gepa_better_candidate",
                summary=f"iteration {iteration} found better score={score}",
                iteration=iteration,
                score=score,
            )
            return

        best_match = _BEST_SCORE_RE.search(stripped)
        if best_match:
            iteration = int(best_match.group(1))
            score = float(best_match.group(2).rstrip("."))
            emit_status_event(
                "gepa_best_score_updated",
                summary=f"best score={score}",
                iteration=iteration,
                score=score,
            )


class PromptOptPythonLogHandler(logging.Handler):
    def __init__(self, raw_log: RawLogWriter):
        super().__init__(level=logging.INFO)
        self._raw_log = raw_log

    def emit(self, record: logging.LogRecord) -> None:
        message = self.format(record)
        self._raw_log.write(record.name, message)

        if record.levelno >= logging.WARNING:
            normalized = message.strip()
            if "The score returned by the metric with pred_name is different" in normalized:
                normalized = "GEPA metric returned predictor-level score mismatch; using module-level score."
            emit_status_event(
                "warning",
                summary=f"warning: {normalized}",
                message=normalized,
                logger_name=record.name,
            )


@contextmanager
def _capture_dspy_logs(raw_log: RawLogWriter):
    logger_names = [
        "dspy.teleprompt.gepa.gepa_utils",
        "dspy.evaluate.evaluate",
        "dspy.utils.parallelizer",
    ]
    handler = PromptOptPythonLogHandler(raw_log)
    handler.setFormatter(logging.Formatter("%(levelname)s %(name)s: %(message)s"))

    original_configs: list[tuple[logging.Logger, list[logging.Handler], bool, int]] = []
    try:
        for logger_name in logger_names:
            logger = logging.getLogger(logger_name)
            original_configs.append((logger, list(logger.handlers), logger.propagate, logger.level))
            logger.handlers = [handler]
            logger.propagate = False
            logger.setLevel(logging.INFO)
        yield
    finally:
        for logger, handlers, propagate, level in original_configs:
            logger.handlers = handlers
            logger.propagate = propagate
            logger.setLevel(level)


def _evaluate_examples_no_stragglers(
    program: Module,
    dataset: list[Example],
    metric: Any,
    *,
    num_threads: int | None,
    failure_score: float,
    provide_traceback: bool | None,
    max_errors: int,
) -> list[tuple[Example, Any, Any]]:
    executor = ParallelExecutor(
        num_threads=num_threads,
        disable_progress_bar=True,
        max_errors=max_errors,
        provide_traceback=provide_traceback,
        compare_results=True,
        timeout=0,
        straggler_limit=0,
    )

    def process_item(example: Example):
        prediction = program(**example.inputs())
        score = metric(example, prediction)
        return prediction, score

    results = executor.execute(process_item, dataset)
    assert len(dataset) == len(results)

    normalized = [((dspy.Prediction(), failure_score) if result is None else result) for result in results]
    return [
        (example, prediction, score)
        for example, (prediction, score) in zip(dataset, normalized, strict=False)
    ]


def _bootstrap_trace_data_no_stragglers(
    program: Module,
    dataset: list[Example],
    metric: Any,
    *,
    num_threads: int | None,
    raise_on_error: bool,
    capture_failed_parses: bool,
    failure_score: float,
    format_failure_score: float,
) -> list[dict[str, Any]]:
    del capture_failed_parses

    def wrapped_metric(example, prediction, trace=None):
        prediction, _ = prediction
        if isinstance(prediction, FailedPrediction):
            return prediction.format_reward or format_failure_score
        return metric(example, prediction, trace) if metric else True

    original_forward = object.__getattribute__(program, "forward")

    def patched_forward(program_to_use: Module, **kwargs):
        with dspy.context(trace=[]):
            try:
                return original_forward(**kwargs), dspy.settings.trace.copy()
            except AdapterParseError as exc:
                completion_str = exc.lm_response
                parsed_result = exc.parsed_result
                failed_signature = exc.signature
                failed_inputs = kwargs

                present = list(parsed_result.keys()) if parsed_result else None
                expected = list(failed_signature.output_fields.keys())

                found_pred = None
                for pred in program_to_use.predictors():
                    if pred.signature == failed_signature:
                        found_pred = pred
                        break
                if found_pred is None:
                    raise ValueError(f"Failed to find the predictor for the failed signature: {failed_signature}")

                trace = dspy.settings.trace.copy()
                if present:
                    completion_ratio = len(present) / max(len(expected), 1)
                    failed_pred = FailedPrediction(
                        completion_text=completion_str,
                        format_reward=format_failure_score
                        + (failure_score - format_failure_score) * completion_ratio,
                    )
                else:
                    failed_pred = FailedPrediction(completion_text=completion_str, format_reward=format_failure_score)

                trace.append((found_pred, failed_inputs, failed_pred))
                return failed_pred, trace

    program.forward = MethodType(patched_forward, program)
    try:
        results = _evaluate_examples_no_stragglers(
            program,
            dataset,
            wrapped_metric,
            num_threads=num_threads,
            failure_score=failure_score,
            provide_traceback=False,
            max_errors=len(dataset) * 10,
        )
    finally:
        program.forward = original_forward

    data: list[dict[str, Any]] = []
    for example_ind, (example, prediction, score) in enumerate(results):
        try:
            prediction_value, trace = prediction
        except ValueError as exc:
            if raise_on_error:
                raise exc
            continue
        data.append(
            {
                "example": example,
                "prediction": prediction_value,
                "trace": trace,
                "example_ind": example_ind,
                "score": score,
            }
        )
    return data


class PromptOptDspyAdapter(DspyAdapter):
    def __init__(self, *args: Any, runtime: GepaRuntimeContext, **kwargs: Any):
        super().__init__(*args, **kwargs)
        self._runtime = runtime

    def propose_new_texts(
        self,
        candidate: dict[str, str],
        reflective_dataset: dict[str, list[dict[str, Any]]],
        components_to_update: list[str],
    ) -> dict[str, str]:
        iteration = self._runtime.current_iteration
        emit_status_event(
            "gepa_reflection_started",
            phase="reflection",
            summary=(
                f"iteration {iteration} reflecting on {', '.join(components_to_update) or '(none)'}"
            ),
            iteration=iteration,
            targets=list(components_to_update),
        )

        reflection_lm = self.reflection_lm or dspy.settings.lm
        results: dict[str, str] = {}

        with dspy.context(lm=reflection_lm):
            for name in components_to_update:
                base_instruction = candidate[name]
                dataset_with_feedback = reflective_dataset[name]
                results[name] = InstructionProposalSignature.run(
                    lm=(lambda x: self.stripped_lm_call(x)[0]),
                    input_dict={
                        "current_instruction_doc": base_instruction,
                        "dataset_with_feedback": dataset_with_feedback,
                    },
                )["new_instruction"]
                emit_status_event(
                    "gepa_proposal",
                    summary=f"iteration {iteration} proposed {name}",
                    iteration=iteration,
                    target=name,
                    preview=_compact_single_line(results[name]),
                )

        emit_status_event(
            "gepa_reflection_completed",
            phase="gepa_compile",
            iteration=iteration,
            targets=list(components_to_update),
        )
        return results

    def evaluate(self, batch, candidate, capture_traces=False):
        program = self.build_program(candidate)

        if capture_traces:
            trajectories = _bootstrap_trace_data_no_stragglers(
                program=program,
                dataset=batch,
                metric=self.metric_fn,
                num_threads=self.num_threads,
                raise_on_error=False,
                capture_failed_parses=True,
                failure_score=self.failure_score,
                format_failure_score=self.failure_score,
            )
            scores = []
            outputs = []
            for item in trajectories:
                outputs.append(item["prediction"])
                score = item["score"]
                if hasattr(score, "score"):
                    score = score["score"]
                scores.append(score if score is not None else self.failure_score)

            return EvaluationBatch(outputs=outputs, scores=scores, trajectories=trajectories)

        results = _evaluate_examples_no_stragglers(
            program,
            batch,
            self.metric_fn,
            num_threads=self.num_threads,
            failure_score=self.failure_score,
            provide_traceback=True,
            max_errors=len(batch) * 100,
        )
        outputs = [result[1] for result in results]
        scores = [result[2] for result in results]
        scores = [score["score"] if hasattr(score, "score") else score for score in scores]
        return EvaluationBatch(outputs=outputs, scores=scores, trajectories=None)


def compile_gepa_with_status(
    teleprompter: Any,
    student: Module,
    *,
    trainset: list[Example],
    valset: list[Example] | None,
    raw_log_path: Path,
) -> Module:
    assert trainset, "Trainset must be provided and non-empty"

    runtime = GepaRuntimeContext()
    raw_log = RawLogWriter(raw_log_path)
    emit_status_event(
        "artifact_ready",
        artifact_type="raw_log",
        path=str(raw_log_path),
    )

    try:
        if teleprompter.auto is not None:
            max_metric_calls = teleprompter.auto_budget(
                num_preds=len(student.predictors()),
                num_candidates=AUTO_RUN_SETTINGS[teleprompter.auto]["n"],
                valset_size=len(valset) if valset is not None else len(trainset),
            )
        elif teleprompter.max_full_evals is not None:
            max_metric_calls = teleprompter.max_full_evals * (
                len(trainset) + (len(valset) if valset is not None else len(trainset))
            )
        else:
            max_metric_calls = teleprompter.max_metric_calls

        teleprompter.max_metric_calls = max_metric_calls
        runtime.total_metric_calls = max_metric_calls

        if valset is None:
            emit_status_event(
                "warning",
                summary="warning: no valset provided; using trainset as valset",
                message="No valset provided; using trainset as valset.",
            )

        active_valset = valset or trainset
        emit_status_event(
            "gepa_budget_initialized",
            summary=(
                f"budget metric_calls={max_metric_calls} "
                f"train={len(trainset)} val={len(active_valset)}"
            ),
            total_metric_calls=max_metric_calls,
            train_batches=len(trainset),
            dev_batches=0 if valset is None else len(valset),
            max_full_evals=teleprompter.max_full_evals,
        )

        rng = random.Random(teleprompter.seed)

        def feedback_fn_creator(pred_name: str, predictor) -> Any:
            def feedback_fn(
                predictor_output: dict[str, Any],
                predictor_inputs: dict[str, Any],
                module_inputs: Example,
                module_outputs: Prediction,
                captured_trace,
            ):
                trace_for_pred = [(predictor, predictor_inputs, predictor_output)]
                outcome = teleprompter.metric_fn(
                    module_inputs,
                    module_outputs,
                    captured_trace,
                    pred_name,
                    trace_for_pred,
                )
                if hasattr(outcome, "feedback"):
                    if outcome["feedback"] is None:
                        outcome["feedback"] = f"This trajectory got a score of {outcome['score']}."
                    return outcome
                return dict(score=outcome, feedback=f"This trajectory got a score of {outcome}.")

            return feedback_fn

        feedback_map = {name: feedback_fn_creator(name, predictor) for name, predictor in student.named_predictors()}
        adapter = PromptOptDspyAdapter(
            student_module=student,
            metric_fn=teleprompter.metric_fn,
            feedback_map=feedback_map,
            failure_score=teleprompter.failure_score,
            num_threads=teleprompter.num_threads,
            add_format_failure_as_feedback=teleprompter.add_format_failure_as_feedback,
            rng=rng,
            reflection_lm=teleprompter.reflection_lm,
            custom_instruction_proposer=teleprompter.custom_instruction_proposer,
            warn_on_score_mismatch=teleprompter.warn_on_score_mismatch,
            reflection_minibatch_size=teleprompter.reflection_minibatch_size,
            runtime=runtime,
        )

        seed_candidate = {
            name: predictor.signature.instructions
            for name, predictor in student.named_predictors()
        }

        with _capture_dspy_logs(raw_log):
            gepa_result: GEPAResult = optimize(
                seed_candidate=seed_candidate,
                trainset=trainset,
                valset=active_valset,
                adapter=adapter,
                reflection_lm=(lambda x: adapter.stripped_lm_call(x)[0]) if teleprompter.reflection_lm is not None else None,
                candidate_selection_strategy=teleprompter.candidate_selection_strategy,
                skip_perfect_score=teleprompter.skip_perfect_score,
                reflection_minibatch_size=teleprompter.reflection_minibatch_size,
                module_selector=teleprompter.component_selector,
                perfect_score=teleprompter.perfect_score,
                use_merge=teleprompter.use_merge,
                max_merge_invocations=teleprompter.max_merge_invocations,
                max_metric_calls=teleprompter.max_metric_calls,
                logger=PromptOptGepaLogger(runtime, raw_log),
                run_dir=teleprompter.log_dir,
                use_wandb=teleprompter.use_wandb,
                wandb_api_key=teleprompter.wandb_api_key,
                wandb_init_kwargs=teleprompter.wandb_init_kwargs,
                use_mlflow=teleprompter.use_mlflow,
                track_best_outputs=teleprompter.track_best_outputs,
                display_progress_bar=False,
                raise_on_exception=True,
                seed=teleprompter.seed,
                **teleprompter.gepa_kwargs,
            )

        new_prog = adapter.build_program(gepa_result.best_candidate)
        if teleprompter.track_stats:
            dspy_gepa_result = DspyGEPAResult.from_gepa_result(gepa_result, adapter)
            new_prog.detailed_results = dspy_gepa_result
        return new_prog
    finally:
        raw_log.close()

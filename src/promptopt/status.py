from __future__ import annotations

import json
import sys
import threading
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from enum import Enum
from pathlib import Path
from typing import Any, Protocol


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


class PromptOptUiMode(str, Enum):
    LIVE = "live"
    PLAIN = "plain"


@dataclass
class PromptOptStatusEvent:
    kind: str
    timestamp_utc: str
    summary: str | None = None
    phase: str | None = None
    details: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def create(
        cls,
        kind: str,
        *,
        summary: str | None = None,
        phase: str | None = None,
        **details: Any,
    ) -> "PromptOptStatusEvent":
        return cls(
            kind=kind,
            timestamp_utc=utc_timestamp(),
            summary=summary,
            phase=phase,
            details=details,
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "kind": self.kind,
            "timestamp_utc": self.timestamp_utc,
            "summary": self.summary,
            "phase": self.phase,
            "details": self.details,
        }


@dataclass
class ActiveEvaluation:
    key: str
    run_label: str
    candidate_id: str
    task_id: str
    attempt: int
    out_dir: str
    status: str
    started_at_utc: str
    elapsed_seconds: int = 0


@dataclass
class GepaProgressSnapshot:
    total_metric_calls: int | None = None
    rollouts_started: int = 0
    rollouts_completed: int = 0
    current_iteration: int | None = None
    selected_program_idx: int | None = None
    selected_program_score: float | None = None
    last_reflection_targets: tuple[str, ...] = ()
    reflection_running: bool = False
    last_proposal_target: str | None = None
    last_proposal_preview: str | None = None
    latest_warning: str | None = None
    train_batches: int | None = None
    dev_batches: int | None = None
    max_full_evals: int | None = None
    best_val_score: float | None = None


@dataclass
class PromptOptRunState:
    phase: str = "startup"
    root: str = "."
    run_count: int = 0
    started_at_utc: str | None = None
    reflection_transcript_enabled: bool = False
    heartbeat_seconds: int = 0
    active_bundle: str = "(pending)"
    best_bundle: str = "(pending)"
    best_score: float | None = None
    final_score: float | None = None
    events_path: str | None = None
    raw_log_path: str | None = None
    reflection_transcript_path: str | None = None
    latest_warning: str | None = None
    latest_error: str | None = None
    latest_reflection_preview: str | None = None
    completion_requested: bool = False
    active_evaluations: dict[str, ActiveEvaluation] = field(default_factory=dict)
    recent_activity: list[str] = field(default_factory=list)
    gepa: GepaProgressSnapshot = field(default_factory=GepaProgressSnapshot)

    @property
    def active_evaluation_count(self) -> int:
        return len(self.active_evaluations)


class PromptOptStatusSink(Protocol):
    def emit(self, event: PromptOptStatusEvent) -> None:
        ...

    def close(self) -> None:
        ...


class NoOpPromptOptStatusSink:
    def emit(self, event: PromptOptStatusEvent) -> None:
        del event

    def close(self) -> None:
        return None


class CompositePromptOptStatusSink:
    def __init__(self, *sinks: PromptOptStatusSink):
        self._sinks = sinks

    def emit(self, event: PromptOptStatusEvent) -> None:
        for sink in self._sinks:
            sink.emit(event)

    def close(self) -> None:
        for sink in self._sinks:
            sink.close()


class JsonlPromptOptStatusSink:
    def __init__(self, path: Path):
        self._path = Path(path)
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._handle = self._path.open("a", encoding="utf-8")
        self._lock = threading.Lock()

    def emit(self, event: PromptOptStatusEvent) -> None:
        line = json.dumps(event.to_dict(), sort_keys=True, default=str)
        with self._lock:
            self._handle.write(line + "\n")
            self._handle.flush()

    def close(self) -> None:
        with self._lock:
            self._handle.close()


class PlainPromptOptStatusSink:
    def __init__(self, stream: Any | None = None):
        self._stream = stream or sys.stdout
        self._state = PromptOptRunState()
        self._lock = threading.Lock()

    @property
    def state(self) -> PromptOptRunState:
        return self._state

    def emit(self, event: PromptOptStatusEvent) -> None:
        with self._lock:
            apply_event(self._state, event)
            line = summarize_event(event)
            if line:
                self._stream.write(line + "\n")
                self._stream.flush()

    def close(self) -> None:
        return None


_CURRENT_SINK_LOCK = threading.Lock()
_CURRENT_SINK: PromptOptStatusSink = NoOpPromptOptStatusSink()


def get_current_status_sink() -> PromptOptStatusSink:
    with _CURRENT_SINK_LOCK:
        return _CURRENT_SINK


def set_current_status_sink(sink: PromptOptStatusSink) -> PromptOptStatusSink:
    global _CURRENT_SINK
    with _CURRENT_SINK_LOCK:
        previous = _CURRENT_SINK
        _CURRENT_SINK = sink
        return previous


def emit_status_event(
    kind: str,
    *,
    summary: str | None = None,
    phase: str | None = None,
    **details: Any,
) -> PromptOptStatusEvent:
    event = PromptOptStatusEvent.create(kind, summary=summary, phase=phase, **details)
    get_current_status_sink().emit(event)
    return event


def _append_recent_activity(state: PromptOptRunState, summary: str | None, *, include: bool = True) -> None:
    if not include or not summary:
        return
    state.recent_activity.append(summary)
    if len(state.recent_activity) > 12:
        del state.recent_activity[:-12]


def _to_float(value: Any) -> float | None:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def summarize_event(event: PromptOptStatusEvent) -> str | None:
    if event.summary:
        return event.summary

    details = event.details
    if event.kind == "startup":
        return f"startup root={details.get('root', '.')} runs={details.get('run_count', 0)}"
    if event.kind == "preflight_done":
        return "preflight ok"
    if event.kind == "gepa_budget_initialized":
        return (
            f"budget metric_calls={details.get('total_metric_calls')} "
            f"train={details.get('train_batches')} val={details.get('dev_batches')}"
        )
    if event.kind == "gepa_rollout_started":
        total = details.get("total_metric_calls")
        current = details.get("rollout_index")
        if total:
            return f"rollout {current}/{total} started"
        return f"rollout {current} started"
    if event.kind == "gepa_rollout_completed":
        total = details.get("total_metric_calls")
        current = details.get("rollout_index")
        if total:
            return f"rollout {current}/{total} completed"
        return f"rollout {current} completed"
    if event.kind == "eval_started":
        return f"eval run={details.get('run_label')} candidate={details.get('candidate_id')} started"
    if event.kind == "eval_heartbeat":
        return f"eval run={details.get('run_label')} elapsed={details.get('elapsed_seconds')}s"
    if event.kind == "eval_finished":
        return (
            f"eval run={details.get('run_label')} pass={details.get('passed')} "
            f"score={details.get('score')}"
        )
    if event.kind == "eval_cache_hit":
        return f"eval run={details.get('run_label')} cache-hit score={details.get('score')}"
    if event.kind == "gepa_candidate_selected":
        return (
            f"iteration {details.get('iteration')} selected program "
            f"{details.get('selected_program_idx')} score={details.get('selected_program_score')}"
        )
    if event.kind == "gepa_reflection_started":
        targets = ", ".join(details.get("targets") or [])
        return f"iteration {details.get('iteration')} reflecting on {targets or '(none)'}"
    if event.kind == "gepa_proposal":
        return f"iteration {details.get('iteration')} proposed {details.get('target')}"
    if event.kind == "gepa_better_candidate":
        return f"iteration {details.get('iteration')} found better score={details.get('score')}"
    if event.kind == "gepa_best_score_updated":
        return f"best score={details.get('score')}"
    if event.kind == "final_bundle_written":
        return f"final bundle={details.get('bundle_id')}"
    if event.kind == "optimization_completed":
        return (
            f"optimization complete bundle={details.get('best_bundle')} "
            f"score={details.get('final_score')}"
        )
    if event.kind == "warning":
        return f"warning: {details.get('message')}"
    if event.kind == "error":
        return f"error: {details.get('message')}"
    return None


def apply_event(state: PromptOptRunState, event: PromptOptStatusEvent) -> PromptOptRunState:
    details = event.details
    if event.phase:
        state.phase = event.phase

    if event.kind == "startup":
        state.phase = event.phase or "startup"
        state.root = str(details.get("root", state.root))
        state.run_count = int(details.get("run_count", state.run_count))
        state.started_at_utc = event.timestamp_utc
        state.reflection_transcript_enabled = bool(
            details.get("reflection_transcript_enabled", state.reflection_transcript_enabled)
        )
        state.heartbeat_seconds = int(details.get("heartbeat_seconds", state.heartbeat_seconds))
        state.events_path = details.get("events_path") or state.events_path
        state.raw_log_path = details.get("raw_log_path") or state.raw_log_path
        _append_recent_activity(state, summarize_event(event))
        return state

    if event.kind == "phase_changed":
        state.phase = details.get("phase", state.phase)
    elif event.kind == "preflight_started":
        state.phase = "preflight"
    elif event.kind == "preflight_done":
        state.phase = "preflight"
    elif event.kind == "gepa_compile_started":
        state.phase = "gepa_compile"
        state.gepa.train_batches = details.get("train_batches")
        state.gepa.dev_batches = details.get("dev_batches")
        state.gepa.max_full_evals = details.get("max_full_evals")
    elif event.kind == "gepa_budget_initialized":
        state.gepa.total_metric_calls = details.get("total_metric_calls")
        state.gepa.train_batches = details.get("train_batches")
        state.gepa.dev_batches = details.get("dev_batches")
        state.gepa.max_full_evals = details.get("max_full_evals")
    elif event.kind == "gepa_rollout_started":
        state.phase = "evaluation"
        state.active_bundle = str(details.get("bundle_id", state.active_bundle))
        state.gepa.rollouts_started = int(details.get("rollout_index", state.gepa.rollouts_started))
    elif event.kind == "gepa_rollout_completed":
        state.phase = "evaluation"
        state.gepa.rollouts_completed = int(details.get("rollout_index", state.gepa.rollouts_completed))
    elif event.kind == "eval_started":
        key = str(details["key"])
        state.active_evaluations[key] = ActiveEvaluation(
            key=key,
            run_label=str(details.get("run_label", "(unknown)")),
            candidate_id=str(details.get("candidate_id", "(unknown)")),
            task_id=str(details.get("task_id", "(unknown)")),
            attempt=int(details.get("attempt", 1)),
            out_dir=str(details.get("out_dir", "(pending)")),
            status=str(details.get("status", "running")),
            started_at_utc=str(details.get("started_at_utc", event.timestamp_utc)),
            elapsed_seconds=int(details.get("elapsed_seconds", 0)),
        )
    elif event.kind == "eval_heartbeat":
        key = str(details["key"])
        active = state.active_evaluations.get(key)
        if active is not None:
            active.elapsed_seconds = int(details.get("elapsed_seconds", active.elapsed_seconds))
            active.status = str(details.get("status", active.status))
    elif event.kind in {"eval_finished", "eval_timeout"}:
        key = str(details["key"])
        state.active_evaluations.pop(key, None)
        if state.completion_requested and not state.active_evaluations:
            state.phase = "completed"
    elif event.kind == "eval_cache_hit":
        state.active_bundle = str(details.get("candidate_id", state.active_bundle))
    elif event.kind == "gepa_candidate_selected":
        state.phase = "evaluation"
        state.gepa.current_iteration = details.get("iteration")
        state.gepa.selected_program_idx = details.get("selected_program_idx")
        state.gepa.selected_program_score = _to_float(details.get("selected_program_score"))
    elif event.kind == "gepa_reflection_started":
        state.phase = "reflection"
        state.gepa.reflection_running = True
        state.gepa.last_reflection_targets = tuple(str(item) for item in details.get("targets") or [])
    elif event.kind == "gepa_reflection_completed":
        state.phase = "gepa_compile"
        state.gepa.reflection_running = False
    elif event.kind == "gepa_proposal":
        state.gepa.current_iteration = details.get("iteration", state.gepa.current_iteration)
        state.gepa.last_proposal_target = details.get("target")
        state.gepa.last_proposal_preview = details.get("preview")
    elif event.kind == "gepa_better_candidate":
        score = _to_float(details.get("score"))
        state.best_score = score if score is not None else state.best_score
        state.gepa.best_val_score = score if score is not None else state.gepa.best_val_score
    elif event.kind == "gepa_best_score_updated":
        score = _to_float(details.get("score"))
        state.best_score = score if score is not None else state.best_score
        state.gepa.best_val_score = score if score is not None else state.gepa.best_val_score
    elif event.kind == "warning":
        message = str(details.get("message", "")).strip() or event.summary
        state.latest_warning = message
        state.gepa.latest_warning = message
    elif event.kind == "reflection_prompt":
        state.latest_reflection_preview = details.get("preview")
    elif event.kind == "reflection_output":
        state.latest_reflection_preview = details.get("preview")
    elif event.kind == "final_scoring_started":
        state.phase = "final_scoring"
    elif event.kind == "final_bundle_written":
        state.best_bundle = str(details.get("bundle_id", state.best_bundle))
    elif event.kind == "final_score":
        score = _to_float(details.get("score"))
        state.final_score = score if score is not None else state.final_score
        state.best_score = score if score is not None else state.best_score
    elif event.kind == "artifact_ready":
        artifact_type = details.get("artifact_type")
        artifact_path = details.get("path")
        if artifact_type == "reflection_transcript":
            state.reflection_transcript_path = artifact_path
        elif artifact_type == "events":
            state.events_path = artifact_path
        elif artifact_type == "raw_log":
            state.raw_log_path = artifact_path
    elif event.kind == "optimization_completed":
        state.completion_requested = True
        state.best_bundle = str(details.get("best_bundle", state.best_bundle))
        score = _to_float(details.get("final_score"))
        state.final_score = score if score is not None else state.final_score
        state.best_score = score if score is not None else state.best_score
        state.events_path = details.get("events_path") or state.events_path
        state.raw_log_path = details.get("raw_log_path") or state.raw_log_path
        state.reflection_transcript_path = (
            details.get("reflection_transcript_path") or state.reflection_transcript_path
        )
        state.phase = "draining" if state.active_evaluations else "completed"
    elif event.kind == "error":
        state.phase = "failed"
        state.latest_error = str(details.get("message", "Unknown error"))

    include_recent = event.kind not in {"eval_heartbeat"}
    _append_recent_activity(state, summarize_event(event), include=include_recent)
    return state


def serialize_state(state: PromptOptRunState) -> dict[str, Any]:
    return asdict(state)

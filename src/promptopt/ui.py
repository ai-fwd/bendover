from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Any

from rich.console import Console, Group, RenderableType
from rich.live import Live
from rich.panel import Panel
from rich.table import Table
from rich.text import Text

from promptopt.status import PromptOptRunState, PromptOptStatusEvent, apply_event


def _parse_utc(value: str | None) -> datetime | None:
    if not value:
        return None
    normalized = value[:-1] + "+00:00" if value.endswith("Z") else value
    try:
        return datetime.fromisoformat(normalized)
    except ValueError:
        return None


def _elapsed_text(started_at_utc: str | None) -> str:
    started = _parse_utc(started_at_utc)
    if started is None:
        return "(pending)"
    now = datetime.now(timezone.utc)
    delta = max(int((now - started).total_seconds()), 0)
    minutes, seconds = divmod(delta, 60)
    hours, minutes = divmod(minutes, 60)
    if hours > 0:
        return f"{hours}h {minutes:02d}m {seconds:02d}s"
    if minutes > 0:
        return f"{minutes}m {seconds:02d}s"
    return f"{seconds}s"


def _phase_style(phase: str) -> str:
    return {
        "startup": "yellow",
        "preflight": "cyan",
        "gepa_compile": "cyan",
        "reflection": "magenta",
        "evaluation": "blue",
        "final_scoring": "green",
        "draining": "yellow",
        "completed": "green",
        "failed": "red",
    }.get(phase, "white")


def _kv_table() -> Table:
    table = Table.grid(padding=(0, 1))
    table.add_column(style="bold", width=18)
    table.add_column(ratio=1)
    return table


def _add_kv_row(table: Table, label: str, value: Any) -> None:
    table.add_row(f"{label}:", str(value))


class PromptOptLiveRenderer:
    def render(self, state: PromptOptRunState) -> RenderableType:
        return Group(
            self._build_header(state),
            self._build_gepa_panel(state),
            self._build_active_evaluations_panel(state),
            self._build_recent_activity_panel(state),
        )

    def _build_header(self, state: PromptOptRunState) -> Panel:
        table = Table.grid(expand=True)
        table.add_column(ratio=1)
        table.add_column(ratio=1)
        table.add_column(ratio=1)

        progress_total = state.gepa.total_metric_calls or 0
        progress_text = (
            f"{state.gepa.rollouts_completed}/{progress_total}"
            if progress_total > 0
            else str(state.gepa.rollouts_completed)
        )

        left = _kv_table()
        _add_kv_row(left, "Phase", Text(state.phase, style=_phase_style(state.phase)))
        _add_kv_row(left, "Elapsed", _elapsed_text(state.started_at_utc))
        _add_kv_row(left, "Run Dir", state.root)

        middle = _kv_table()
        _add_kv_row(middle, "Active Bundle", state.active_bundle)
        _add_kv_row(middle, "Best Bundle", state.best_bundle)
        _add_kv_row(
            middle,
            "Score",
            state.final_score if state.final_score is not None else state.best_score if state.best_score is not None else "(pending)",
        )

        right = _kv_table()
        _add_kv_row(right, "Metric Calls", progress_text)
        _add_kv_row(right, "Active Evals", state.active_evaluation_count)
        _add_kv_row(right, "Heartbeat", f"{state.heartbeat_seconds}s" if state.heartbeat_seconds else "(pending)")

        table.add_row(left, middle, right)
        return Panel(table, title="PromptOpt", border_style=_phase_style(state.phase))

    def _build_gepa_panel(self, state: PromptOptRunState) -> Panel:
        table = _kv_table()

        targets = ", ".join(state.gepa.last_reflection_targets) if state.gepa.last_reflection_targets else "(none)"
        iteration = state.gepa.current_iteration if state.gepa.current_iteration is not None else "(pending)"
        current_score = (
            state.gepa.selected_program_score
            if state.gepa.selected_program_score is not None
            else "(pending)"
        )
        proposal = (
            f"{state.gepa.last_proposal_target}: {state.gepa.last_proposal_preview}"
            if state.gepa.last_proposal_target
            else "(none)"
        )
        split_text = (
            f"train={state.gepa.train_batches or 0} dev={state.gepa.dev_batches or 0} "
            f"max_full_evals={state.gepa.max_full_evals or 0}"
        )

        _add_kv_row(table, "Iteration", iteration)
        _add_kv_row(table, "Current Score", current_score)
        _add_kv_row(table, "Reflection", "running" if state.gepa.reflection_running else "idle")
        _add_kv_row(table, "Targets", targets)
        _add_kv_row(table, "Last Proposal", proposal)
        _add_kv_row(table, "Train / Dev", split_text)
        _add_kv_row(
            table,
            "Best Val Score",
            state.gepa.best_val_score if state.gepa.best_val_score is not None else "(pending)",
        )
        _add_kv_row(table, "Warning", state.latest_warning or "(none)")

        return Panel(table, title="GEPA", border_style="cyan")

    def _build_active_evaluations_panel(self, state: PromptOptRunState) -> Panel:
        table = Table(expand=True)
        table.add_column("Run")
        table.add_column("Candidate")
        table.add_column("Attempt", justify="right")
        table.add_column("Elapsed", justify="right")
        table.add_column("Status")
        table.add_column("Output")

        if not state.active_evaluations:
            table.add_row("(none)", "(none)", "-", "-", "idle", "(none)")
        else:
            for active in sorted(
                state.active_evaluations.values(),
                key=lambda item: (item.elapsed_seconds, item.run_label),
                reverse=True,
            ):
                table.add_row(
                    active.run_label,
                    active.candidate_id,
                    str(active.attempt),
                    f"{active.elapsed_seconds}s",
                    active.status,
                    active.out_dir,
                )

        return Panel(table, title="Active Evaluations", border_style="blue")

    def _build_recent_activity_panel(self, state: PromptOptRunState) -> Panel:
        lines = list(state.recent_activity[-12:])
        if state.latest_reflection_preview:
            lines.append(f"reflection preview: {state.latest_reflection_preview}")
        if not lines:
            lines = ["(waiting for updates)"]

        text = Text("\n".join(lines))
        return Panel(text, title="Recent Activity", border_style="magenta")


class LivePromptOptStatusSink:
    def __init__(self, console: Console | None = None):
        self._console = console or Console()
        self._state = PromptOptRunState()
        self._renderer = PromptOptLiveRenderer()
        self._lock = threading.Lock()
        self._live = Live(
            self._renderer.render(self._state),
            console=self._console,
            auto_refresh=False,
            transient=False,
        )
        self._live.start()

    @property
    def state(self) -> PromptOptRunState:
        return self._state

    def emit(self, event: PromptOptStatusEvent) -> None:
        with self._lock:
            apply_event(self._state, event)
            self._live.update(self._renderer.render(self._state), refresh=True)

    def close(self) -> None:
        with self._lock:
            self._live.update(self._renderer.render(self._state), refresh=True)
            self._live.stop()


def build_render_text(state: PromptOptRunState, console: Console | None = None) -> str:
    renderer = PromptOptLiveRenderer()
    renderable = renderer.render(state)
    active_console = console or Console(record=True, width=120)
    active_console.print(renderable)
    return active_console.export_text()

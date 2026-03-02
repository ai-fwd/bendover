from __future__ import annotations

import json
import shlex
import subprocess
import time
from pathlib import Path

from promptopt.models import EvaluationResult, PracticeAttribution
from promptopt.status import emit_status_event, utc_timestamp

HEARTBEAT_SECONDS = 15
POLL_INTERVAL_SECONDS = 0.25


def _ensure_plain_ui(cli_command: str) -> list[str]:
    """Append `--ui plain` unless the command already specifies UI mode."""
    tokens = shlex.split(cli_command)
    for index, token in enumerate(tokens):
        if token in ("--ui", "-u"):
            if index + 1 < len(tokens):
                return tokens
        if token.startswith("--ui="):
            return tokens
    return tokens + ["--ui", "plain"]


def _get_ci(data: dict, key: str, default=None):
    """Case-insensitive lookup helper for evaluator.json fields."""
    for candidate in (key, key.lower(), key.upper()):
        if candidate in data:
            return data[candidate]
    for k, v in data.items():
        if k.lower() == key.lower():
            return v
    return default


def _parse_practice_attribution(data: dict) -> PracticeAttribution:
    """Normalize practice attribution block from evaluator.json."""
    practice = _get_ci(data, "practice_attribution", {}) or {}
    selected = _get_ci(practice, "selected_practices", []) or []
    offending = _get_ci(practice, "offending_practices", []) or []
    notes_by = _get_ci(practice, "notes_by_practice", {}) or {}
    normalized_notes = {}
    for key, value in dict(notes_by).items():
        if isinstance(value, list):
            normalized_notes[key] = [str(v) for v in value]
        else:
            normalized_notes[key] = [str(value)]
    return PracticeAttribution(
        selected_practices=list(selected),
        offending_practices=list(offending),
        notes_by_practice=normalized_notes,
    )


def parse_evaluator_json(path: Path) -> EvaluationResult:
    """Parse evaluator.json into a typed EvaluationResult."""
    data = json.loads(path.read_text())
    passed = bool(_get_ci(data, "pass", False))
    score = float(_get_ci(data, "score", 0.0))
    flags = list(_get_ci(data, "flags", []) or [])
    notes = list(_get_ci(data, "notes", []) or [])
    practice_attribution = _parse_practice_attribution(data)

    return EvaluationResult(
        passed=passed,
        score=score,
        flags=flags,
        notes=notes,
        practice_attribution=practice_attribution,
        raw=data,
    )


def evaluate_bundle(
    bundle_path: Path,
    task_path: Path,
    cli_command: str,
    log_dir: Path,
    timeout_seconds: int,
    run_label: str | None = None,
) -> EvaluationResult:
    """
    Execute the PromptOpt CLI against a bundle + task and parse evaluator.json.

    This function is the bridge between GEPA and the real agentic evaluation loop.
    """
    bundle_path = Path(bundle_path)
    task_path = Path(task_path)
    log_dir = Path(log_dir)

    candidate_id = bundle_path.name
    task_id = task_path.name
    out_dir = log_dir / candidate_id / task_id
    out_dir.mkdir(parents=True, exist_ok=True)

    cmd = _ensure_plain_ui(cli_command) + [
        "--bundle",
        str(bundle_path),
        "--task",
        str(task_path),
        "--out",
        str(out_dir),
    ]

    label = (run_label or f"{candidate_id}/{task_id}").strip() or f"{candidate_id}/{task_id}"
    
    def _event_key(attempt: int) -> str:
        return f"{candidate_id}:{task_id}:attempt{attempt}"

    def run_eval_once(attempt: int) -> int:
        key = _event_key(attempt)
        started_at_utc = utc_timestamp()
        emit_status_event(
            "eval_started",
            summary=f"eval run={label} candidate={candidate_id} started",
            run_label=label,
            candidate_id=candidate_id,
            task_id=task_id,
            attempt=attempt,
            out_dir=str(out_dir),
            key=key,
            status="running",
            started_at_utc=started_at_utc,
            elapsed_seconds=0,
        )
        process = subprocess.Popen(cmd, text=True)
        started = time.monotonic()
        next_heartbeat = started + HEARTBEAT_SECONDS

        while True:
            returncode = process.poll()
            if returncode is not None:
                return returncode

            now = time.monotonic()
            elapsed = now - started
            if elapsed >= timeout_seconds:
                process.kill()
                process.wait()
                emit_status_event(
                    "eval_timeout",
                    summary=f"eval run={label} timeout after {timeout_seconds}s",
                    run_label=label,
                    candidate_id=candidate_id,
                    task_id=task_id,
                    attempt=attempt,
                    out_dir=str(out_dir),
                    key=key,
                    timeout_seconds=timeout_seconds,
                    retry=attempt == 1,
                )
                raise subprocess.TimeoutExpired(cmd=cmd, timeout=timeout_seconds)

            if now >= next_heartbeat:
                emit_status_event(
                    "eval_heartbeat",
                    summary=f"eval run={label} elapsed={int(elapsed)}s",
                    run_label=label,
                    candidate_id=candidate_id,
                    task_id=task_id,
                    attempt=attempt,
                    out_dir=str(out_dir),
                    key=key,
                    elapsed_seconds=int(elapsed),
                    status="running",
                )
                next_heartbeat = now + HEARTBEAT_SECONDS

            time.sleep(POLL_INTERVAL_SECONDS)

    try:
        returncode = run_eval_once(attempt=1)
        attempt_used = 1
    except subprocess.TimeoutExpired:
        try:
            returncode = run_eval_once(attempt=2)
            attempt_used = 2
        except subprocess.TimeoutExpired:
            emit_status_event(
                "eval_finished",
                summary=f"eval run={label} pass=False score=0.0",
                run_label=label,
                candidate_id=candidate_id,
                task_id=task_id,
                attempt=2,
                out_dir=str(out_dir),
                key=_event_key(2),
                passed=False,
                score=0.0,
                returncode=None,
            )
            return EvaluationResult(passed=False, score=0.0)

    evaluator_json = out_dir / "evaluator.json"

    if returncode != 0 and not evaluator_json.exists():
        emit_status_event(
            "eval_finished",
            summary=f"eval run={label} pass=False score=0.0",
            run_label=label,
            candidate_id=candidate_id,
            task_id=task_id,
            attempt=attempt_used,
            out_dir=str(out_dir),
            key=_event_key(attempt_used),
            passed=False,
            score=0.0,
            returncode=returncode,
        )
        try:
            emit_status_event(
                "warning",
                summary=f"warning: eval run={label} exited {returncode}; retrying because evaluator.json is missing",
                message=f"eval run={label} exited {returncode}; retrying because evaluator.json is missing",
            )
            returncode = run_eval_once(attempt=attempt_used + 1)
            attempt_used += 1
        except subprocess.TimeoutExpired:
            emit_status_event(
                "eval_finished",
                summary=f"eval run={label} pass=False score=0.0",
                run_label=label,
                candidate_id=candidate_id,
                task_id=task_id,
                attempt=attempt_used,
                out_dir=str(out_dir),
                key=_event_key(attempt_used),
                passed=False,
                score=0.0,
                returncode=None,
            )
            return EvaluationResult(passed=False, score=0.0)

    result = EvaluationResult(passed=False, score=0.0)
    if evaluator_json.exists():
        try:
            result = parse_evaluator_json(evaluator_json)
        except (json.JSONDecodeError, ValueError):
            result = EvaluationResult(passed=False, score=0.0)

    emit_status_event(
        "eval_finished",
        summary=f"eval run={label} pass={result.passed} score={result.score}",
        run_label=label,
        candidate_id=candidate_id,
        task_id=task_id,
        attempt=attempt_used,
        out_dir=str(out_dir),
        key=_event_key(attempt_used),
        passed=result.passed,
        score=result.score,
        returncode=returncode,
    )
    return result

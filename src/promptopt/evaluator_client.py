from __future__ import annotations

import json
import shlex
import subprocess
from pathlib import Path

from promptopt.models import EvaluationResult, PracticeAttribution


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

    cmd = shlex.split(cli_command) + [
        "--bundle",
        str(bundle_path),
        "--task",
        str(task_path),
        "--out",
        str(out_dir),
    ]

    def run_eval():
        return subprocess.run(cmd, timeout=timeout_seconds, capture_output=True, text=True)

    try:
        result = run_eval()
    except subprocess.TimeoutExpired:
        try:
            result = run_eval()
        except subprocess.TimeoutExpired:
            return EvaluationResult(passed=False, score=0.0)

    if result.returncode != 0:
        evaluator_json = out_dir / "evaluator.json"
        if not evaluator_json.exists():
            try:
                result = run_eval()
            except subprocess.TimeoutExpired:
                pass

    evaluator_json = out_dir / "evaluator.json"
    if evaluator_json.exists():
        try:
            return parse_evaluator_json(evaluator_json)
        except (json.JSONDecodeError, ValueError):
            return EvaluationResult(passed=False, score=0.0)

    return EvaluationResult(passed=False, score=0.0)

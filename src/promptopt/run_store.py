from __future__ import annotations

import json
from pathlib import Path

from promptopt.models import RunArtifact


def load_run_artifact(runs_root: Path, run_id: str) -> RunArtifact:
    run_dir = runs_root / run_id
    if not run_dir.exists():
        raise FileNotFoundError(f"Run directory not found: {run_dir}")

    goal_path = run_dir / "goal.txt"
    if not goal_path.exists():
        raise FileNotFoundError(f"goal.txt not found: {goal_path}")

    goal = goal_path.read_text().strip()

    meta: dict = {}
    meta_path = run_dir / "run_meta.json"
    if meta_path.exists():
        try:
            meta = json.loads(meta_path.read_text())
        except json.JSONDecodeError:
            meta = {}

    base_commit_path = run_dir / "base_commit.txt"
    if base_commit_path.exists():
        base_commit = base_commit_path.read_text().strip()
    else:
        base_commit = str(meta.get("base_commit", ""))

    if not base_commit:
        raise FileNotFoundError(
            f"base_commit.txt missing and run_meta.json lacks base_commit: {run_dir}"
        )

    bundle_id_path = run_dir / "bundle_id.txt"
    bundle_id = None
    if bundle_id_path.exists():
        bundle_id = bundle_id_path.read_text().strip() or None
    elif meta.get("bundle_id"):
        bundle_id = str(meta.get("bundle_id"))

    git_diff_path = run_dir / "git_diff.patch"
    git_diff = git_diff_path.read_text() if git_diff_path.exists() else None

    evaluator_path = run_dir / "evaluator.json"
    evaluator = None
    if evaluator_path.exists():
        try:
            evaluator = json.loads(evaluator_path.read_text())
        except json.JSONDecodeError:
            evaluator = None

    return RunArtifact(
        run_id=run_id,
        run_dir=run_dir,
        goal=goal,
        base_commit=base_commit,
        bundle_id=bundle_id,
        meta=meta,
        git_diff=git_diff,
        evaluator=evaluator,
    )

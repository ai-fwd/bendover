import hashlib
import json
import time
from pathlib import Path

from promptopt.bundle_store import build_bundle_from_seed, load_bundle, write_bundle
from promptopt.evaluator_client import evaluate_bundle as _evaluate_bundle
from promptopt.run_store import load_run_artifact


def load_split(split_file_path):
    """
    Returns ordered list of task directories from train.txt/val.txt.
    """
    path = Path(split_file_path)
    if not path.exists():
        raise FileNotFoundError(f"Split file not found: {split_file_path}")

    tasks = []
    with open(path, "r") as f:
        for line in f:
            line = line.strip()
            if line:
                tasks.append(line)
    return tasks


def create_candidate_bundle(
    seed_bundle_path=None,
    bundle_root=None,
    generation=None,
    practices_content=None,
    exist_ok=False,
    seed_bundle_id=None,
):
    """
    Creates a new bundle by copying the seed bundle and applying edits to specific files.

    seed_bundle_path: Path to the seed bundle directory.
    bundle_root: Directory where new bundles are created.
    practices_content: Dictionary mapping filename -> NEW BODY CONTENT (str).
                       Frontmatter from the seed file will be preserved.
    """
    if seed_bundle_path is None and seed_bundle_id is not None:
        seed_bundle_path = seed_bundle_id
    if seed_bundle_path is None:
        raise ValueError("seed_bundle_path is required")

    seed_path = Path(seed_bundle_path)
    if seed_path.exists():
        seed_bundle = load_bundle(seed_path)
        updated_bundle = build_bundle_from_seed(seed_bundle, practices_content)
        bundle = write_bundle(
            bundle_root=Path(bundle_root),
            bundle=updated_bundle,
            parent_id=seed_bundle.bundle_id,
            generation=str(generation),
            exist_ok=exist_ok,
        )
        return bundle.bundle_id, bundle.path, bundle.meta

    # Fallback: behave like legacy mode when no seed bundle exists.
    bundle_root = Path(bundle_root)
    content_hash = bundle_hash_for_practices(practices_content)
    bundle_id = f"gen{generation}_{content_hash[:8]}"
    bundle_path = bundle_root / bundle_id
    if bundle_path.exists() and not exist_ok:
        raise FileExistsError(f"Bundle directory already exists: {bundle_path}")

    practices_dir = bundle_path / "practices"
    practices_dir.mkdir(parents=True, exist_ok=True)
    for filename, content in practices_content.items():
        (practices_dir / filename).write_text(content)

    meta = {
        "id": bundle_id,
        "parent": seed_path.name,
        "generation": generation,
    }
    (bundle_path / "meta.json").write_text(json.dumps(meta, indent=2))
    return bundle_id, bundle_path, meta


def prepare_replay_task(run_id, runs_root):
    """
    Creates a temporary task directory from a recorded run.
    """
    run = load_run_artifact(Path(runs_root), run_id)

    temp_dir = Path(f"/tmp/bendover_replay_{run_id}_{int(time.time())}")
    temp_dir.mkdir(parents=True, exist_ok=True)

    (temp_dir / "task.md").write_text(run.goal)
    (temp_dir / "base_commit.txt").write_text(run.base_commit)

    return temp_dir


def _merge_frontmatter_body(original_text, new_body):
    """
    Helper to preserve YAML frontmatter from original_text and append new_body.
    """
    if original_text.startswith("---\n"):
        parts = original_text.split("---\n", 2)
        if len(parts) >= 3:
            frontmatter = parts[1]
            return f"---\n{frontmatter}---\n\n{new_body}"
    return new_body


def evaluate_bundle(bundle_path, task_path, cli_command, log_dir, timeout_seconds):
    """
    Evaluates a bundle against a task using the CLI.
    Returns (pass: bool, score: float).
    """
    result = _evaluate_bundle(
        bundle_path=Path(bundle_path),
        task_path=Path(task_path),
        cli_command=cli_command,
        log_dir=Path(log_dir),
        timeout_seconds=timeout_seconds,
    )
    return result.passed, result.score


def bundle_hash_for_practices(practices_content):
    sorted_content = sorted(practices_content.items())
    content_str = "".join([content for _, content in sorted_content])
    return hashlib.sha256(content_str.encode("utf-8")).hexdigest()

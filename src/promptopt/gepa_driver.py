import hashlib
import json
import os
import subprocess
import time
from pathlib import Path
from datetime import datetime

def load_split(split_file_path):
    """
    Returns ordered list of task directories from train.txt/val.txt.
    """
    path = Path(split_file_path)
    if not path.exists():
        raise FileNotFoundError(f"Split file not found: {split_file_path}")
        
    tasks = []
    with open(path, 'r') as f:
        for line in f:
            line = line.strip()
            if line:
                tasks.append(line)
    return tasks

def create_candidate_bundle(seed_bundle_path, bundle_root, generation, practices_content, exist_ok=False):
    """
    Creates a new bundle by copying the seed bundle and applying edits to specific files.
    
    seed_bundle_path: Path to the seed bundle directory.
    bundle_root: Directory where new bundles are created.
    practices_content: Dictionary mapping filename -> NEW BODY CONTENT (str). 
                       Frontmatter from the seed file will be preserved.
    """
    seed_bundle_path = Path(seed_bundle_path)
    bundle_root = Path(bundle_root)
    
    # calculate hash based on new content (body only for mutated files)
    sorted_content = sorted(practices_content.items())
    content_str = "".join([content for _, content in sorted_content])
    content_hash = hashlib.sha256(content_str.encode('utf-8')).hexdigest()
    
    new_id = f"gen{generation}_{content_hash[:8]}"
    new_path = bundle_root / new_id
    
    if new_path.exists():
        if exist_ok:
            meta_path = new_path / "meta.json"
            if meta_path.exists():
                meta = json.loads(meta_path.read_text())
                return new_id, new_path, meta
            return new_id, new_path, {}
        raise FileExistsError(f"Bundle directory already exists: {new_path}")
    
    # Create directory structure
    practices_dir = new_path / "practices"
    practices_dir.mkdir(parents=True)
    
    # Copy all files from seed bundle practices
    seed_practices_dir = seed_bundle_path / "practices"
    if seed_practices_dir.exists():
        for file in seed_practices_dir.glob("*"):
            if file.is_file():
                if file.name in practices_content:
                    # Apply mutation preserving frontmatter
                    original_text = file.read_text()
                    new_body = practices_content[file.name]
                    merged_content = _merge_frontmatter_body(original_text, new_body)
                    (practices_dir / file.name).write_text(merged_content)
                else:
                    # Direct copy
                    (practices_dir / file.name).write_text(file.read_text())
    
    # Write any new files that didn't exist in seed (if any in practices_content)
    # This logic assumes we mostly mutate existing, but good generic support.
    for filename, content in practices_content.items():
        if not (practices_dir / filename).exists():
             # Assume content passed is full content if new? 
             # Or just body? Let's assume body and empty frontmatter if new.
             # But likely we only edit existing files as per plan.
             (practices_dir / filename).write_text(content)

    # Create and write metadata
    meta = {
        "id": new_id,
        "parent": seed_bundle_path.name,
        "generation": generation,
        "created_at": datetime.now().isoformat(),
        "hash": content_hash
    }
    
    (new_path / "meta.json").write_text(json.dumps(meta, indent=2))
    
    return new_id, new_path, meta

def prepare_replay_task(run_id, runs_root):
    """
    Creates a temporary task directory from a recorded run.
    """
    run_dir = Path(runs_root) / run_id
    if not run_dir.exists():
        raise FileNotFoundError(f"Run directory not found: {run_dir}")
        
    temp_dir = Path(f"/tmp/bendover_replay_{run_id}_{int(time.time())}")
    temp_dir.mkdir(parents=True, exist_ok=True)
    
    # Copy goal.txt -> task.md
    goal_src = run_dir / "goal.txt"
    if goal_src.exists():
        (temp_dir / "task.md").write_text(goal_src.read_text())
    else:
        # Fallback or error?
        (temp_dir / "task.md").write_text("No goal recorded.")

    # Copy base_commit.txt
    commit_src = run_dir / "base_commit.txt"
    if commit_src.exists():
        (temp_dir / "base_commit.txt").write_text(commit_src.read_text())
        
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
    bundle_path = Path(bundle_path)
    task_path = Path(task_path)
    log_dir = Path(log_dir)
    
    # Create unique output directory: log_dir / candidate_id / task_id
    # bundle_path name is the candidate_id
    candidate_id = bundle_path.name
    task_id = task_path.name
    out_dir = log_dir / candidate_id / task_id
    
    # Construct command
    # Assuming CLI supports: <command> --bundle <path> --task <path> --out <path>
    # Splitting cli_command into parts in case it's "dotnet run --project ..."
    cmd_parts = cli_command.split()
    cmd = shlex.split(cli_command) + ["--bundle", str(bundle_path), "--task", str(task_path), "--out", str(out_dir)]
    print(f"[DEBUG] Invoking CLI: {cmd}")
    
    def run_eval(attempt=1):
        try:
            result = subprocess.run(cmd, timeout=timeout_seconds, capture_output=True, text=True)
            return result
        except subprocess.TimeoutExpired as e:
            # Re-raise to be handled by retry logic
            raise e

    # First Attempt
    try:
        result = run_eval(attempt=1)
    except subprocess.TimeoutExpired:
        # Retry ONCE on timeout
        try:
            result = run_eval(attempt=2)
        except subprocess.TimeoutExpired:
             # If it fails twice, we return failure (or maybe -1 score? Plan implies boolean/float return)
             # "Returns (pass: bool, score: float)"
             # If timeout persists, it's a fail.
             return False, 0.0

    # Retry ONCE if non-zero exit AND evaluator.json missing
    if result.returncode != 0:
        print(f"[ERROR] CLI Failed (Return Code {result.returncode})")
        print(f"STDOUT:\n{result.stdout}")
        print(f"STDERR:\n{result.stderr}")
        evaluator_json = out_dir / "evaluator.json"
        if not evaluator_json.exists():
            # Retry
            try:
                result = run_eval(attempt=2)
            except subprocess.TimeoutExpired:
                pass # Fall through to result check

    # Read Result
    evaluator_json = out_dir / "evaluator.json"
    if evaluator_json.exists():
        try:
            data = json.loads(evaluator_json.read_text())
            return data.get("pass", False), float(data.get("score", 0.0))
        except (json.JSONDecodeError, ValueError):
            return False, 0.0
    
    return False, 0.0

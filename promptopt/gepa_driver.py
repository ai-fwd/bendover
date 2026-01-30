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

def create_candidate_bundle(seed_bundle_id, bundle_root, generation, practices_content, exist_ok=False):
    """
    Pure function returning (new_id, new_path, metadata_content).
    Writes the bundle content to disk including meta.json.
    
    ID format: gen{generation}_{content_hash[:8]}
    """
    bundle_root = Path(bundle_root)
    
    # Calculate hash based on concatenated practice content
    # Sort keys to ensure determinism
    sorted_practices = sorted(practices_content.items())
    content_str = "".join([content for _, content in sorted_practices])
    content_hash = hashlib.sha256(content_str.encode('utf-8')).hexdigest()
    
    new_id = f"gen{generation}_{content_hash[:8]}"
    new_path = bundle_root / new_id
    
    if new_path.exists():
        if exist_ok:
            # Read metadata if available, else just return
            meta_path = new_path / "meta.json"
            if meta_path.exists():
                meta = json.loads(meta_path.read_text())
                return new_id, new_path, meta
            return new_id, new_path, {}
        raise FileExistsError(f"Bundle directory already exists: {new_path}")
    
    # Create directory structure
    practices_dir = new_path / "practices"
    practices_dir.mkdir(parents=True)
    
    # Write practice files
    for filename, content in practices_content.items():
        (practices_dir / filename).write_text(content)
        
    # Create and write metadata
    meta = {
        "id": new_id,
        "parent": seed_bundle_id,
        "generation": generation,
        "created_at": datetime.now().isoformat(),
        "hash": content_hash
    }
    
    (new_path / "meta.json").write_text(json.dumps(meta, indent=2))
    
    return new_id, new_path, meta

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
    cmd = cmd_parts + [
        "--bundle", str(bundle_path),
        "--task", str(task_path),
        "--out", str(out_dir)
    ]
    
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

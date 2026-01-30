import pytest
import shutil
import json
import subprocess
from pathlib import Path
from unittest.mock import MagicMock, patch
from promptopt.gepa_driver import load_split, create_candidate_bundle, evaluate_bundle

@pytest.fixture
def temp_workspace(tmp_path):
    # Setup mock workspace
    tasks_dir = tmp_path / "bench" / "tasks"
    tasks_dir.mkdir(parents=True)
    
    (tasks_dir / "task1").mkdir()
    (tasks_dir / "task2").mkdir()
    (tasks_dir / "task3").mkdir()
    
    train_txt = tmp_path / "train.txt"
    train_txt.write_text("task1\ntask3\n")
    
    bundles_dir = tmp_path / ".bendover" / "promptopt" / "bundles"
    bundles_dir.mkdir(parents=True)
    
    return {
        "root": tmp_path,
        "train_txt": train_txt,
        "bundles_dir": bundles_dir
    }

def test_load_split(temp_workspace):
    tasks = load_split(str(temp_workspace["train_txt"]))
    assert tasks == ["task1", "task3"]

def test_load_split_missing_file():
    with pytest.raises(FileNotFoundError):
        load_split("non_existent_file.txt")

def test_create_candidate_bundle_pure(temp_workspace):
    bundle_root = temp_workspace["bundles_dir"]
    practices = {"practice_1.md": "content1", "practice_2.md": "content2"}
    
    new_id, new_path, meta = create_candidate_bundle(
        seed_bundle_id="seed123",
        bundle_root=bundle_root,
        generation=1,
        practices_content=practices
    )
    
    # Check ID format: gen1_{hash}
    assert new_id.startswith("gen1_")
    assert len(new_id) > 5
    
    # Check path
    assert new_path == bundle_root / new_id
    assert new_path.exists()
    
    # Check practices written
    assert (new_path / "practices" / "practice_1.md").read_text() == "content1"
    assert (new_path / "practices" / "practice_2.md").read_text() == "content2"
    
    # Check meta.json
    assert (new_path / "meta.json").exists()
    meta_loaded = json.loads((new_path / "meta.json").read_text())
    assert meta_loaded["parent"] == "seed123"
    assert meta_loaded["generation"] == 1
    assert meta_loaded == meta

def test_create_candidate_bundle_id_determinism(temp_workspace):
    bundle_root = temp_workspace["bundles_dir"]
    practices = {"p1.md": "abc"}
    
    id1, _, _ = create_candidate_bundle("seed", bundle_root, 1, practices)
    
    # Same content should produce same ID (if we were pure, but strict creation might fail if exists)
    # The function signature suggests it creates the directory.
    # If we call it again with same params, it should probably return same ID but maybe handle existence?
    # For now, let's just check the ID calculation based on another call (we'll need to clean up or expect failure if it tries to mkdir)
    
    # Let's clean up first to test ID determinism independently of FS side effects
    shutil.rmtree(bundle_root / id1)
    
    id2, _, _ = create_candidate_bundle("seed", bundle_root, 1, practices)
    assert id1 == id2

def test_create_candidate_bundle_exists(temp_workspace):
    bundle_root = temp_workspace["bundles_dir"]
    practices = {"p1.md": "abc"}
    
    # First creation
    create_candidate_bundle("seed", bundle_root, 1, practices)
    
    # Second creation with same content should raise FileExistsError or similar if we enforce uniqueness strictly
    # or handle it gracefully. The plan says "Verifies failure if target directory already exists"
    with pytest.raises(FileExistsError):
        create_candidate_bundle("seed", bundle_root, 1, practices)


@patch("subprocess.run")
def test_evaluate_bundle_contract(mock_run, temp_workspace):
    log_dir = temp_workspace["root"] / "logs"
    log_dir.mkdir()
    bundle_path = temp_workspace["bundles_dir"] / "gen1_12345678"
    task_path = temp_workspace["root"] / "bench" / "tasks" / "task1"
    
    # Mock successful run with evaluator.json creation
    def side_effect_success(*args, **kwargs):
        # args[0] is the command list
        cmd = args[0]
        # extract --out dir from command
        try:
            out_idx = cmd.index("--out")
            out_dir = Path(cmd[out_idx + 1])
            # emulate tool writing evaluator.json
            out_dir.mkdir(parents=True, exist_ok=True)
            (out_dir / "evaluator.json").write_text('{"pass": true, "score": 100.0}')
        except ValueError:
            pass
        return MagicMock(returncode=0)

    mock_run.side_effect = side_effect_success
    
    success, score = evaluate_bundle(
        bundle_path=bundle_path,
        task_path=task_path,
        cli_command="bendover-cli",
        log_dir=log_dir,
        timeout_seconds=10
    )
    
    assert success is True
    assert score == 100.0
    
    # Verify Unique Output Directory
    # Inspect the call args to ensure unique path structure: log_dir / bundle_id / task_id
    args, _ = mock_run.call_args
    cmd = args[0]
    out_idx = cmd.index("--out")
    out_dir_arg = Path(cmd[out_idx + 1])
    
    expected_out = log_dir / "gen1_12345678" / "task1"
    assert out_dir_arg == expected_out
    assert mock_run.call_count == 1


@patch("subprocess.run")
def test_evaluate_bundle_timeout_retry(mock_run, temp_workspace):
    log_dir = temp_workspace["root"] / "logs_retry"
    log_dir.mkdir()
    bundle_path = temp_workspace["bundles_dir"] / "gen1_retry"
    task_path = temp_workspace["root"] / "bench" / "tasks" / "task1"
    
    # First call times out, Second call succeeds
    def side_effect_timeout_then_success(*args, **kwargs):
        # We need to maintain state to switch behavior
        if not hasattr(side_effect_timeout_then_success, "called"):
            side_effect_timeout_then_success.called = True
            raise subprocess.TimeoutExpired(cmd=args[0], timeout=10)
        else:
             # Success on retry
            cmd = args[0]
            out_idx = cmd.index("--out")
            out_dir = Path(cmd[out_idx + 1])
            out_dir.mkdir(parents=True, exist_ok=True)
            (out_dir / "evaluator.json").write_text('{"pass": false, "score": 0.0}')
            return MagicMock(returncode=0)

    mock_run.side_effect = side_effect_timeout_then_success
    
    success, score = evaluate_bundle(
        bundle_path=bundle_path,
        task_path=task_path,
        cli_command="bendover-cli",
        log_dir=log_dir,
        timeout_seconds=10
    )
    
    # It should succeed (return valid score) after retry, even if pass is false
    assert success is False
    assert score == 0.0
    assert mock_run.call_count == 2

@patch("subprocess.run")
def test_evaluate_bundle_no_retry_if_evaluator_exists(mock_run, temp_workspace):
    # Scenario: Process crashes (non-zero exit) BUT wrote evaluator.json. Should NOT retry.
    log_dir = temp_workspace["root"] / "logs_crash"
    log_dir.mkdir()
    
    def side_effect_crash_with_json(*args, **kwargs):
        cmd = args[0]
        out_idx = cmd.index("--out")
        out_dir = Path(cmd[out_idx + 1])
        out_dir.mkdir(parents=True, exist_ok=True)
        (out_dir / "evaluator.json").write_text('{"pass": true, "score": 50.0}') # Wrote partial result?
        return MagicMock(returncode=1) # Error code

    mock_run.side_effect = side_effect_crash_with_json
    
    success, score = evaluate_bundle(
        bundle_path=Path("b"),
        task_path=Path("t"),
        cli_command="cli",
        log_dir=log_dir,
        timeout_seconds=10
    )
    
    assert success is True
    assert score == 50.0
    assert mock_run.call_count == 1

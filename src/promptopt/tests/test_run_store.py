import json

from promptopt.run_store import load_run_artifact


def test_load_run_artifact_reads_optional_artifacts(tmp_path):
    runs_root = tmp_path / "runs"
    run_id = "run1"
    run_dir = runs_root / run_id
    run_dir.mkdir(parents=True)

    (run_dir / "goal.txt").write_text("Ship feature")
    (run_dir / "base_commit.txt").write_text("abc123")
    (run_dir / "bundle_id.txt").write_text("bundle-x")
    (run_dir / "git_diff.patch").write_text("diff --git a/x b/x\n+line")
    (run_dir / "dotnet_test.txt").write_text("tests passed")
    (run_dir / "dotnet_build.txt").write_text("build passed")
    (run_dir / "outputs.json").write_text(json.dumps({"engineer": "impl", "reviewer": "review"}))

    artifact = load_run_artifact(runs_root, run_id)

    assert artifact.run_id == run_id
    assert artifact.goal == "Ship feature"
    assert artifact.base_commit == "abc123"
    assert artifact.bundle_id == "bundle-x"
    assert artifact.git_diff and "diff --git" in artifact.git_diff
    assert artifact.dotnet_test == "tests passed"
    assert artifact.dotnet_build == "build passed"
    assert artifact.outputs == {"engineer": "impl", "reviewer": "review"}


def test_load_run_artifact_falls_back_to_none_for_missing_optional_files(tmp_path):
    runs_root = tmp_path / "runs"
    run_id = "run2"
    run_dir = runs_root / run_id
    run_dir.mkdir(parents=True)

    (run_dir / "goal.txt").write_text("Ship feature")
    (run_dir / "base_commit.txt").write_text("abc123")

    artifact = load_run_artifact(runs_root, run_id)

    assert artifact.outputs is None
    assert artifact.git_diff is None
    assert artifact.dotnet_test is None
    assert artifact.dotnet_test_error is None
    assert artifact.dotnet_build is None
    assert artifact.dotnet_build_error is None

import shutil
import tempfile
from pathlib import Path
from promptopt.gepa_driver import create_candidate_bundle, prepare_replay_task, _merge_frontmatter_body

def verify_helpers():
    with tempfile.TemporaryDirectory() as temp_root:
        root = Path(temp_root)
        
        # Setup Seed Bundle
        seed_path = root / "bundles" / "seed"
        (seed_path / "practices").mkdir(parents=True)
        (seed_path / "practices" / "coding_standards.md").write_text(
            "---\nid: coding_standards\n---\n\nAvoid duplicate code."
        )
        (seed_path / "practices" / "static.md").write_text("Should not change.")
        
        # Setup Run
        run_path = root / "runs" / "run_001"
        run_path.mkdir(parents=True)
        (run_path / "goal.txt").write_text("Build a spaceship")
        (run_path / "base_commit.txt").write_text("abc1234")
        
        # Test 1: prepare_replay_task
        print("Testing prepare_replay_task...")
        task_dir = prepare_replay_task("run_001", root / "runs")
        assert (task_dir / "task.md").read_text() == "Build a spaceship"
        assert (task_dir / "base_commit.txt").read_text() == "abc1234"
        print("PASS")
        
        # Test 2: create_candidate_bundle (Frontmatter Preservation)
        print("Testing create_candidate_bundle...")
        bundle_root = root / "bundles"
        new_body = "Avoid duplicate code.\nAlways write tests."
        
        new_id, new_path, meta = create_candidate_bundle(
            seed_bundle_path=seed_path,
            bundle_root=bundle_root,
            generation="test",
            practices_content={"coding_standards.md": new_body},
            exist_ok=False
        )
        
        # Check files
        new_practice = (new_path / "practices" / "coding_standards.md").read_text()
        assert "id: coding_standards" in new_practice # Frontmatter
        assert "Always write tests" in new_practice # New Body
        
        static_practice = (new_path / "practices" / "static.md").read_text()
        assert static_practice == "Should not change."
        
        print("PASS")

if __name__ == "__main__":
    verify_helpers()

from pathlib import Path

from promptopt.bundle_store import load_bundle, build_bundle_from_seed, write_bundle


def test_load_bundle_captures_agents_as_passthrough(tmp_path: Path):
    bundle_path = tmp_path / "bundles" / "seed"
    practices_dir = bundle_path / "practices"
    practices_dir.mkdir(parents=True)
    agents_dir = bundle_path / "agents"
    agents_dir.mkdir(parents=True)

    (practices_dir / "tdd_spirit.md").write_text(
        "---\nName: tdd_spirit\nTargetRole: Architect\nAreaOfConcern: Quality\n---\n\nPractice body"
    )
    (agents_dir / "lead.md").write_text("Lead template")
    (agents_dir / "engineer.md").write_text("Engineer template")

    bundle = load_bundle(bundle_path)

    assert "tdd_spirit.md" in bundle.practices
    assert "agents/lead.md" not in bundle.practices
    assert bundle.passthrough_files["agents/lead.md"] == "Lead template"
    assert bundle.passthrough_files["agents/engineer.md"] == "Engineer template"


def test_write_bundle_preserves_agents_passthrough_files(tmp_path: Path):
    seed_path = tmp_path / "bundles" / "seed"
    practices_dir = seed_path / "practices"
    practices_dir.mkdir(parents=True)
    agents_dir = seed_path / "agents"
    agents_dir.mkdir(parents=True)

    (practices_dir / "clean_interfaces.md").write_text(
        "---\nName: clean_interfaces\nTargetRole: Engineer\nAreaOfConcern: Style\n---\n\nOriginal"
    )
    (agents_dir / "lead.md").write_text("Lead template")
    (agents_dir / "engineer.md").write_text("Engineer template")

    seed_bundle = load_bundle(seed_path)
    updated_bundle = build_bundle_from_seed(seed_bundle, {"clean_interfaces.md": "Updated"})
    written = write_bundle(
        bundle_root=tmp_path / "bundles",
        bundle=updated_bundle,
        parent_id="seed",
        generation="1",
    )

    written_practices = written.path / "practices"
    assert (written_practices / "clean_interfaces.md").exists()
    assert (written.path / "agents" / "lead.md").read_text() == "Lead template"
    assert (written.path / "agents" / "engineer.md").read_text() == "Engineer template"

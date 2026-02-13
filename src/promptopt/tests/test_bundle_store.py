import json
from pathlib import Path

import pytest

from promptopt.bundle_store import (
    ensure_active_bundle,
    load_bundle,
    build_bundle_from_seed,
    write_bundle,
)


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
    (agents_dir / "tools.md").write_text("# SDK Tool Usage Contract (Auto-generated)\n- sdk contract")

    bundle = load_bundle(bundle_path)

    assert "tdd_spirit.md" in bundle.practices
    assert "agents/lead.md" not in bundle.practices
    assert bundle.passthrough_files["agents/lead.md"] == "Lead template"
    assert bundle.passthrough_files["agents/engineer.md"] == "Engineer template"
    assert bundle.passthrough_files["agents/tools.md"] == "# SDK Tool Usage Contract (Auto-generated)\n- sdk contract"


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
    (agents_dir / "tools.md").write_text("# SDK Tool Usage Contract (Auto-generated)\n- sdk contract")

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
    assert (written.path / "agents" / "tools.md").read_text() == "# SDK Tool Usage Contract (Auto-generated)\n- sdk contract"


def test_ensure_active_bundle_bootstraps_seed_from_root_sources(tmp_path: Path):
    bendover_root = tmp_path / ".bendover"
    promptopt_root = bendover_root / "promptopt"
    practices_source = bendover_root / "practices"
    agents_source = bendover_root / "agents"
    practices_source.mkdir(parents=True)
    agents_source.mkdir(parents=True)

    (practices_source / "tdd_spirit.md").write_text(
        "---\nName: tdd_spirit\nTargetRole: Architect\nAreaOfConcern: Quality\n---\n\nPractice body"
    )
    (agents_source / "lead.md").write_text("Lead root")
    (agents_source / "engineer.md").write_text("Engineer root")
    (agents_source / "tools.md").write_text("# SDK Tool Usage Contract (Auto-generated)\n- sdk contract")

    active_bundle_id = ensure_active_bundle(promptopt_root)

    assert active_bundle_id == "seed"
    active_data = json.loads((promptopt_root / "active.json").read_text())
    assert active_data["bundleId"] == "seed"
    assert (promptopt_root / "bundles" / "seed" / "practices" / "tdd_spirit.md").read_text().endswith("Practice body")
    assert (promptopt_root / "bundles" / "seed" / "agents" / "lead.md").read_text() == "Lead root"


def test_ensure_active_bundle_rebuilds_seed_when_active_is_deleted(tmp_path: Path):
    bendover_root = tmp_path / ".bendover"
    promptopt_root = bendover_root / "promptopt"
    practices_source = bendover_root / "practices"
    agents_source = bendover_root / "agents"
    practices_source.mkdir(parents=True)
    agents_source.mkdir(parents=True)

    (practices_source / "old.md").write_text("---\nName: old\n---\n\nold")
    (agents_source / "lead.md").write_text("Lead v1")
    (agents_source / "engineer.md").write_text("Engineer v1")
    (agents_source / "tools.md").write_text("# SDK Tool Usage Contract (Auto-generated)\n- sdk contract")

    ensure_active_bundle(promptopt_root)

    (promptopt_root / "active.json").unlink()
    (practices_source / "old.md").unlink()
    (practices_source / "new.md").write_text("---\nName: new\n---\n\nnew")
    (agents_source / "lead.md").write_text("Lead v2")

    ensure_active_bundle(promptopt_root)

    seed_root = promptopt_root / "bundles" / "seed"
    assert not (seed_root / "practices" / "old.md").exists()
    assert (seed_root / "practices" / "new.md").exists()
    assert (seed_root / "agents" / "lead.md").read_text() == "Lead v2"


def test_ensure_active_bundle_returns_existing_active_bundle_without_bootstrap(tmp_path: Path):
    promptopt_root = tmp_path / ".bendover" / "promptopt"
    promptopt_root.mkdir(parents=True)
    (promptopt_root / "active.json").write_text('{"bundleId":"gen_existing"}')

    active_bundle_id = ensure_active_bundle(promptopt_root)

    assert active_bundle_id == "gen_existing"
    assert not (promptopt_root / "bundles" / "seed").exists()


def test_ensure_active_bundle_fails_when_root_practices_missing(tmp_path: Path):
    bendover_root = tmp_path / ".bendover"
    promptopt_root = bendover_root / "promptopt"
    agents_source = bendover_root / "agents"
    agents_source.mkdir(parents=True)
    (agents_source / "lead.md").write_text("Lead")
    (agents_source / "engineer.md").write_text("Engineer")
    (agents_source / "tools.md").write_text("# SDK Tool Usage Contract (Auto-generated)\n- sdk contract")

    with pytest.raises(FileNotFoundError, match="Root practices directory not found"):
        ensure_active_bundle(promptopt_root)


def test_ensure_active_bundle_fails_when_root_practices_empty(tmp_path: Path):
    bendover_root = tmp_path / ".bendover"
    promptopt_root = bendover_root / "promptopt"
    practices_source = bendover_root / "practices"
    agents_source = bendover_root / "agents"
    practices_source.mkdir(parents=True)
    agents_source.mkdir(parents=True)
    (agents_source / "lead.md").write_text("Lead")
    (agents_source / "engineer.md").write_text("Engineer")
    (agents_source / "tools.md").write_text("# SDK Tool Usage Contract (Auto-generated)\n- sdk contract")

    with pytest.raises(ValueError, match="must contain at least one .md"):
        ensure_active_bundle(promptopt_root)


def test_ensure_active_bundle_fails_when_root_agents_missing(tmp_path: Path):
    bendover_root = tmp_path / ".bendover"
    promptopt_root = bendover_root / "promptopt"
    practices_source = bendover_root / "practices"
    practices_source.mkdir(parents=True)
    (practices_source / "tdd_spirit.md").write_text("---\nName: tdd_spirit\n---\n\ncontent")

    with pytest.raises(FileNotFoundError, match="Root agents directory not found"):
        ensure_active_bundle(promptopt_root)


@pytest.mark.parametrize("missing_file", ["lead.md", "engineer.md", "tools.md"])
def test_ensure_active_bundle_fails_when_required_agent_file_missing(tmp_path: Path, missing_file: str):
    bendover_root = tmp_path / ".bendover"
    promptopt_root = bendover_root / "promptopt"
    practices_source = bendover_root / "practices"
    agents_source = bendover_root / "agents"
    practices_source.mkdir(parents=True)
    agents_source.mkdir(parents=True)
    (practices_source / "tdd_spirit.md").write_text("---\nName: tdd_spirit\n---\n\ncontent")

    required_files = ["lead.md", "engineer.md", "tools.md"]
    for file_name in required_files:
        if file_name == missing_file:
            continue
        content = "template"
        if file_name == "tools.md":
            content = "# SDK Tool Usage Contract (Auto-generated)\n- sdk contract"
        (agents_source / file_name).write_text(content)

    with pytest.raises(FileNotFoundError, match=missing_file):
        ensure_active_bundle(promptopt_root)

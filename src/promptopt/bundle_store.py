from __future__ import annotations

import hashlib
import json
import shutil
from datetime import datetime
from pathlib import Path
from typing import Any

from promptopt.models import Bundle, PracticeFile


def read_active_bundle_id(active_json_path: Path) -> str:
    """
    Read the active bundle id from active.json.

    This mirrors the CLI resolver: if active.json is missing we let the caller decide
    how to fall back (e.g., root/practices).
    """
    if not active_json_path.exists():
        raise FileNotFoundError(f"active.json not found: {active_json_path}")

    try:
        data = json.loads(active_json_path.read_text())
    except json.JSONDecodeError as exc:
        raise ValueError(f"Invalid JSON in {active_json_path}") from exc

    bundle_id = data.get("bundleId") or data.get("bundle_id")
    if not bundle_id:
        raise ValueError(f"active.json at {active_json_path} missing bundleId")

    return str(bundle_id)


def update_active_json(active_json_path: Path, bundle_id: str, metadata: dict[str, Any]) -> None:
    """Persist the active bundle id and metadata after optimization."""
    active_json_path.parent.mkdir(parents=True, exist_ok=True)
    payload = {"bundleId": bundle_id, **metadata}
    active_json_path.write_text(json.dumps(payload, indent=2))


def ensure_active_bundle(promptopt_root: Path, seed_bundle_id: str = "seed") -> str:
    """
    Ensure prompt optimization has an active bundle id.

    Behavior:
    - If active.json exists, return its bundle id as-is.
    - If active.json is missing, rebuild bundles/<seed_bundle_id> from root
      .bendover/{practices,agents} and write active.json -> seed.
    """
    promptopt_root = Path(promptopt_root)
    active_json_path = promptopt_root / "active.json"

    if active_json_path.exists():
        return read_active_bundle_id(active_json_path)

    _rebuild_seed_bundle(promptopt_root, seed_bundle_id)
    update_active_json(active_json_path, seed_bundle_id, metadata={})
    return seed_bundle_id


def _rebuild_seed_bundle(promptopt_root: Path, seed_bundle_id: str) -> None:
    practices_source, agents_source = _resolve_root_sources(promptopt_root)
    _validate_root_sources(practices_source, agents_source)

    seed_bundle_path = promptopt_root / "bundles" / seed_bundle_id
    if seed_bundle_path.exists():
        shutil.rmtree(seed_bundle_path)

    (promptopt_root / "bundles").mkdir(parents=True, exist_ok=True)
    shutil.copytree(practices_source, seed_bundle_path / "practices")
    shutil.copytree(agents_source, seed_bundle_path / "agents")


def _resolve_root_sources(promptopt_root: Path) -> tuple[Path, Path]:
    bendover_root = promptopt_root.parent
    return bendover_root / "practices", bendover_root / "agents"


def _validate_root_sources(practices_source: Path, agents_source: Path) -> None:
    if not practices_source.is_dir():
        raise FileNotFoundError(f"Root practices directory not found: {practices_source}")
    if not any(practices_source.glob("*.md")):
        raise ValueError(
            f"Root practices directory must contain at least one .md file: {practices_source}")

    if not agents_source.is_dir():
        raise FileNotFoundError(f"Root agents directory not found: {agents_source}")

    required_agents = ("lead.md", "engineer.md", "tools.md")
    for file_name in required_agents:
        path = agents_source / file_name
        if not path.is_file():
            raise FileNotFoundError(f"Required root agent prompt file missing: {path}")


def _parse_frontmatter(text: str) -> tuple[str, str]:
    """Split YAML frontmatter from the practice body, if present."""
    if not text.startswith("---\n"):
        return "", text

    parts = text.split("---\n", 2)
    if len(parts) < 3:
        return "", text

    frontmatter = parts[1].strip()
    body = parts[2].lstrip("\n")
    return frontmatter, body


def _extract_name(frontmatter: str, fallback: str) -> str:
    """Use Name: from frontmatter if available; otherwise fall back to filename."""
    for line in frontmatter.splitlines():
        if ":" not in line:
            continue
        key, value = line.split(":", 1)
        if key.strip().lower() == "name":
            name = value.strip()
            return name or fallback
    return fallback


def load_bundle(bundle_path: Path) -> Bundle:
    """
    Load a bundle from disk: practices/*.md + optional meta.json.

    Each practice file is parsed into frontmatter + body, and a stable name is
    derived from the frontmatter or filename.
    """
    if not bundle_path.exists():
        raise FileNotFoundError(f"Bundle not found: {bundle_path}")

    practices_dir = bundle_path / "practices"
    if not practices_dir.exists():
        raise FileNotFoundError(f"Practices directory not found: {practices_dir}")

    practices: dict[str, PracticeFile] = {}
    for practice_path in sorted(practices_dir.glob("*.md")):
        text = practice_path.read_text()
        frontmatter, body = _parse_frontmatter(text)
        fallback_name = practice_path.stem
        name = _extract_name(frontmatter, fallback_name)
        practices[practice_path.name] = PracticeFile(
            file_name=practice_path.name,
            name=name,
            frontmatter=frontmatter,
            body=body.strip(),
            path=practice_path,
        )

    passthrough_files: dict[str, str] = {}
    agents_dir = bundle_path / "agents"
    if agents_dir.exists():
        for prompt_path in sorted(agents_dir.rglob("*.md")):
            relative = prompt_path.relative_to(bundle_path).as_posix()
            passthrough_files[relative] = prompt_path.read_text()

    meta_path = bundle_path / "meta.json"
    meta: dict[str, Any] = {}
    if meta_path.exists():
        try:
            meta = json.loads(meta_path.read_text())
        except json.JSONDecodeError:
            meta = {}

    return Bundle(
        bundle_id=bundle_path.name,
        path=bundle_path,
        practices=practices,
        passthrough_files=passthrough_files,
        meta=meta,
    )


def build_bundle_from_seed(seed: Bundle, updates: dict[str, str]) -> Bundle:
    """
    Create a new bundle by applying body updates to the seed practices.
    """
    practices: dict[str, PracticeFile] = {}
    for file_name, practice in seed.practices.items():
        new_body = updates.get(file_name, practice.body)
        practices[file_name] = PracticeFile(
            file_name=file_name,
            name=practice.name,
            frontmatter=practice.frontmatter,
            body=new_body.strip(),
        )

    for file_name, new_body in updates.items():
        if file_name not in practices:
            practices[file_name] = PracticeFile(
                file_name=file_name,
                name=Path(file_name).stem,
                frontmatter="",
                body=new_body.strip(),
            )

    return Bundle(
        bundle_id=seed.bundle_id,
        path=seed.path,
        practices=practices,
        passthrough_files=dict(seed.passthrough_files),
        meta=seed.meta,
    )


def hash_bundle(practices: dict[str, PracticeFile], passthrough_files: dict[str, str] | None = None) -> str:
    """Hash the practice bodies to produce a deterministic bundle id."""
    content = "".join([practices[name].body for name in sorted(practices.keys())])
    if passthrough_files:
        content += "".join(
            [f"{name}\n{passthrough_files[name]}" for name in sorted(passthrough_files.keys())]
        )
    return hashlib.sha256(content.encode("utf-8")).hexdigest()


def write_bundle(
    bundle_root: Path,
    bundle: Bundle,
    parent_id: str,
    generation: str,
    metadata: dict[str, Any] | None = None,
    exist_ok: bool = True,
) -> Bundle:
    """
    Persist a bundle to disk under bundles/<bundle_id>/practices.
    """
    bundle_root.mkdir(parents=True, exist_ok=True)

    content_hash = hash_bundle(bundle.practices, bundle.passthrough_files)
    bundle_id = f"gen{generation}_{content_hash[:8]}"
    bundle_path = bundle_root / bundle_id

    if bundle_path.exists() and not exist_ok:
        raise FileExistsError(f"Bundle directory already exists: {bundle_path}")

    if not bundle_path.exists():
        (bundle_path / "practices").mkdir(parents=True, exist_ok=True)
        for practice in bundle.practices.values():
            target = bundle_path / "practices" / practice.file_name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(practice.render())
        for relative_path, content in bundle.passthrough_files.items():
            target = bundle_path / relative_path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content)

    meta = {
        "id": bundle_id,
        "parent": parent_id,
        "generation": generation,
        "created_at": datetime.utcnow().isoformat() + "Z",
        "hash": content_hash,
    }
    if metadata:
        meta.update(metadata)

    (bundle_path / "meta.json").write_text(json.dumps(meta, indent=2))

    return Bundle(
        bundle_id=bundle_id,
        path=bundle_path,
        practices=bundle.practices,
        passthrough_files=bundle.passthrough_files,
        meta=meta,
    )

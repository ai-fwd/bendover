from __future__ import annotations

import hashlib
import json
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

    meta_path = bundle_path / "meta.json"
    meta: dict[str, Any] = {}
    if meta_path.exists():
        try:
            meta = json.loads(meta_path.read_text())
        except json.JSONDecodeError:
            meta = {}

    return Bundle(bundle_id=bundle_path.name, path=bundle_path, practices=practices, meta=meta)


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

    return Bundle(bundle_id=seed.bundle_id, path=seed.path, practices=practices, meta=seed.meta)


def hash_bundle(practices: dict[str, PracticeFile]) -> str:
    """Hash the practice bodies to produce a deterministic bundle id."""
    content = "".join([practices[name].body for name in sorted(practices.keys())])
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

    content_hash = hash_bundle(bundle.practices)
    bundle_id = f"gen{generation}_{content_hash[:8]}"
    bundle_path = bundle_root / bundle_id

    if bundle_path.exists() and not exist_ok:
        raise FileExistsError(f"Bundle directory already exists: {bundle_path}")

    if not bundle_path.exists():
        (bundle_path / "practices").mkdir(parents=True, exist_ok=True)
        for practice in bundle.practices.values():
            target = bundle_path / "practices" / practice.file_name
            target.write_text(practice.render())

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

    return Bundle(bundle_id=bundle_id, path=bundle_path, practices=bundle.practices, meta=meta)

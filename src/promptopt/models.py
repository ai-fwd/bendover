from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class PracticeFile:
    """In-memory representation of a single practice markdown file."""
    file_name: str
    name: str
    frontmatter: str
    body: str
    path: Path | None = None

    def render(self) -> str:
        frontmatter = self.frontmatter.strip()
        if frontmatter:
            return f"---\n{frontmatter}\n---\n\n{self.body}"
        return self.body


@dataclass
class Bundle:
    """A bundle is a collection of practices + metadata on disk."""
    bundle_id: str
    path: Path
    practices: dict[str, PracticeFile]
    passthrough_files: dict[str, str] = field(default_factory=dict)
    meta: dict[str, Any] = field(default_factory=dict)


@dataclass
class RunArtifact:
    """Recorded run used as training data for GEPA."""
    run_id: str
    run_dir: Path
    goal: str
    base_commit: str
    bundle_id: str | None = None
    meta: dict[str, Any] = field(default_factory=dict)
    outputs: dict[str, str] | None = None
    git_diff: str | None = None
    dotnet_test: str | None = None
    dotnet_test_error: str | None = None
    dotnet_build: str | None = None
    dotnet_build_error: str | None = None
    evaluator: dict[str, Any] | None = None


@dataclass
class PracticeAttribution:
    """Evaluator notes scoped to specific practices."""
    selected_practices: list[str] = field(default_factory=list)
    offending_practices: list[str] = field(default_factory=list)
    notes_by_practice: dict[str, list[str]] = field(default_factory=dict)


@dataclass
class EvaluationResult:
    """Result of evaluating a bundle on a run."""
    passed: bool
    score: float
    flags: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    practice_attribution: PracticeAttribution = field(default_factory=PracticeAttribution)
    raw: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "pass": self.passed,
            "score": self.score,
            "flags": self.flags,
            "notes": self.notes,
            "practice_attribution": {
                "selected_practices": self.practice_attribution.selected_practices,
                "offending_practices": self.practice_attribution.offending_practices,
                "notes_by_practice": self.practice_attribution.notes_by_practice,
            },
        }

    @staticmethod
    def from_dict(data: dict[str, Any]) -> "EvaluationResult":
        practice = data.get("practice_attribution") or {}
        return EvaluationResult(
            passed=bool(data.get("pass", False)),
            score=float(data.get("score", 0.0)),
            flags=list(data.get("flags") or []),
            notes=list(data.get("notes") or []),
            practice_attribution=PracticeAttribution(
                selected_practices=list(practice.get("selected_practices") or []),
                offending_practices=list(practice.get("offending_practices") or []),
                notes_by_practice=dict(practice.get("notes_by_practice") or {}),
            ),
            raw=data,
        )

from __future__ import annotations

import json
from datetime import datetime
from pathlib import Path

from promptopt.models import EvaluationResult


class EvaluationCache:
    def __init__(self, root: Path):
        self.root = Path(root)

    def _path(self, run_id: str, bundle_hash: str) -> Path:
        return self.root / "evals" / bundle_hash / f"{run_id}.json"

    def get(self, run_id: str, bundle_hash: str) -> EvaluationResult | None:
        """
        Load a cached evaluation if available.

        Cache key is (run_id + bundle_hash) so the same run can be re-scored
        across different candidate bundles.
        """
        path = self._path(run_id, bundle_hash)
        if not path.exists():
            return None
        data = json.loads(path.read_text())
        evaluation = data.get("evaluation", data)
        return EvaluationResult.from_dict(evaluation)

    def set(self, run_id: str, bundle_hash: str, evaluation: EvaluationResult) -> None:
        """Persist an evaluation to disk for reuse in later GEPA iterations."""
        path = self._path(run_id, bundle_hash)
        path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "run_id": run_id,
            "bundle_hash": bundle_hash,
            "cached_at": datetime.utcnow().isoformat() + "Z",
            "evaluation": evaluation.to_dict(),
        }
        path.write_text(json.dumps(payload, indent=2))

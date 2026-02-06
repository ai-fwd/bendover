import pytest
from typer.testing import CliRunner
from unittest.mock import MagicMock, patch
import dspy
from dspy.utils import DummyLM

from promptopt.run_gepa import app, BundleProgram
from promptopt.models import Bundle, PracticeFile, RunArtifact, EvaluationResult, PracticeAttribution

runner = CliRunner()


@pytest.fixture
def mock_dependencies(tmp_path):
    with patch("promptopt.run_gepa.load_split") as mock_load, \
         patch("promptopt.run_gepa.read_active_bundle_id") as mock_active, \
         patch("promptopt.run_gepa.load_bundle") as mock_load_bundle, \
         patch("promptopt.run_gepa.load_run_artifact") as mock_load_run, \
         patch("promptopt.run_gepa.GEPA") as mock_gepa, \
         patch("promptopt.run_gepa.EvaluationCache") as mock_cache, \
         patch("promptopt.run_gepa.BundleProgram") as mock_program_cls, \
         patch("promptopt.run_gepa.evaluate_bundle") as mock_eval:

        mock_load.return_value = ["run1", "run2"]
        mock_active.return_value = "seed"

        practice = PracticeFile(
            file_name="simple.md",
            name="simple",
            frontmatter="Name: simple",
            body="Hello",
        )
        mock_load_bundle.return_value = Bundle(
            bundle_id="seed",
            path=tmp_path / "seed",
            practices={"simple.md": practice},
            meta={},
        )

        mock_load_run.side_effect = lambda _, rid: RunArtifact(
            run_id=rid,
            run_dir=tmp_path / rid,
            goal="Do it",
            base_commit="abc",
        )

        # Mock GEPA instance
        mock_teleprompter = MagicMock()
        mock_gepa.return_value = mock_teleprompter

        # Mock Program instance
        mock_program = MagicMock()
        mock_program_cls.return_value = mock_program

        # Mock compiled program
        mock_compiled = MagicMock()
        mock_compiled.get_practice_updates.return_value = {"simple.md": "Updated"}
        mock_teleprompter.compile.return_value = mock_compiled

        # Mock cache
        mock_cache.return_value.get.return_value = None
        mock_cache.return_value.set.return_value = None

        mock_eval.return_value = EvaluationResult(
            passed=True,
            score=0.5,
            notes=["ok"],
            practice_attribution=PracticeAttribution(notes_by_practice={}),
        )

        yield {
            "load_split": mock_load,
            "GEPA": mock_gepa,
            "teleprompter": mock_teleprompter,
            "program_cls": mock_program_cls,
        }


def test_cli_invocation_format(mock_dependencies, tmp_path):
    deps = mock_dependencies
    promptopt_root = tmp_path / "promptopt"

    result = runner.invoke(app, [
        "--promptopt-root", str(promptopt_root),
        "--cli-command", "bendover-cli",
        "--lm-mode", "dummy",
        "--reflection-lm", "test",
        "--max-full-evals", "1",
    ])

    assert result.exit_code == 0
    deps["load_split"].assert_called_with(str(promptopt_root / "datasets" / "train.txt"))
    deps["GEPA"].assert_called_once()
    deps["teleprompter"].compile.assert_called_once()


def test_bundle_program_forward_uses_feedback(tmp_path):
    dspy.configure(lm=DummyLM({ "": { "response": "ok" } }))

    practice = PracticeFile(
        file_name="simple.md",
        name="simple",
        frontmatter="Name: simple",
        body="Hello",
    )

    seed_bundle = Bundle(
        bundle_id="seed",
        path=tmp_path / "seed",
        practices={"simple.md": practice},
        meta={},
    )

    run = RunArtifact(
        run_id="run1",
        run_dir=tmp_path / "run1",
        goal="Do it",
        base_commit="abc",
    )

    cache = MagicMock()
    cache.get.return_value = None

    program = BundleProgram(
        seed_bundle=seed_bundle,
        runs_by_id={"run1": run},
        bundle_root=tmp_path / "bundles",
        log_dir=tmp_path / "logs",
        cache=cache,
        cli_command="cli",
        timeout=5,
    )

    eval_result = EvaluationResult(
        passed=True,
        score=0.5,
        notes=["global note"],
        practice_attribution=PracticeAttribution(
            notes_by_practice={"simple": ["ADD_LINE: hi"]}
        ),
    )

    with patch("promptopt.run_gepa.evaluate_bundle", return_value=eval_result):
        pred = program.forward(["run1"])

    assert pred.score == 0.5
    assert pred.feedback_by_pred

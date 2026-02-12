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
        mock_program.snapshot_mutation_state.return_value = ({}, set())
        mock_program.restore_mutation_state.return_value = None
        mock_program.return_value = dspy.Prediction(
            score=0.5,
            feedback_by_pred={"practice_0": "ok"},
            attribution_by_run={
                "run1": {
                    "selected_practices": ["simple"],
                    "offending_practices": ["simple"],
                    "notes_by_practice_keys": ["simple"],
                    "flags": [],
                    "notes": ["ok"],
                    "score": 0.5,
                    "passed": True,
                }
            },
        )

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
            "program": mock_program,
        }


def test_cli_invocation_format(mock_dependencies, tmp_path):
    deps = mock_dependencies
    promptopt_root = tmp_path / "promptopt"

    result = runner.invoke(app, [
        "--promptopt-root", str(promptopt_root),
        "--cli-command", "bendover-cli",
        "--reflection-lm", "test",
        "--max-full-evals", "1",
    ])

    assert result.exit_code == 0
    deps["load_split"].assert_called_with(str(promptopt_root / "datasets" / "train.txt"))
    deps["GEPA"].assert_called_once()
    deps["program"].assert_called_once_with(run_ids=["run1"])
    deps["teleprompter"].compile.assert_called_once()


def test_cli_preflight_fails_without_practice_attribution(mock_dependencies, tmp_path):
    deps = mock_dependencies
    promptopt_root = tmp_path / "promptopt"
    deps["program"].return_value = dspy.Prediction(
        score=0.0,
        feedback_by_pred={},
        attribution_by_run={
            "run1": {
                "selected_practices": ["simple"],
                "offending_practices": [],
                "notes_by_practice_keys": [],
                "flags": ["TDDSpiritRule"],
                "notes": ["No test output found."],
                "score": 0.0,
                "passed": False,
            }
        },
    )

    result = runner.invoke(app, [
        "--promptopt-root", str(promptopt_root),
        "--cli-command", "bendover-cli",
        "--reflection-lm", "test",
        "--max-full-evals", "1",
    ])

    assert result.exit_code != 0
    error_text = result.output
    assert "GEPA attribution preflight failed" in error_text
    assert "run_id=run1 pass=False score=0.0" in error_text
    assert "notes_by_practice_keys: none" in error_text
    deps["teleprompter"].compile.assert_not_called()


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


def test_bundle_program_targets_only_implicated_practices(tmp_path):
    dspy.configure(lm=DummyLM({ "": { "response": "ok" } }))

    simple = PracticeFile(
        file_name="simple.md",
        name="simple",
        frontmatter="Name: simple",
        body="Hello",
    )
    static = PracticeFile(
        file_name="static.md",
        name="static",
        frontmatter="Name: static",
        body="Static",
    )

    seed_bundle = Bundle(
        bundle_id="seed",
        path=tmp_path / "seed",
        practices={"simple.md": simple, "static.md": static},
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

    pred_by_file = {practice.file_name: pred_name for pred_name, practice in program.practice_by_pred.items()}
    getattr(program, pred_by_file["simple.md"]).signature.instructions = "Hello\nADD_LINE: target_only"
    getattr(program, pred_by_file["static.md"]).signature.instructions = "Static\nADD_LINE: should_not_persist"

    eval_result = EvaluationResult(
        passed=True,
        score=0.8,
        notes=["global note"],
        practice_attribution=PracticeAttribution(
            offending_practices=["simple"],
            notes_by_practice={"simple": ["ADD_LINE: target_only"]},
        ),
    )

    with patch("promptopt.run_gepa.evaluate_bundle", return_value=eval_result):
        pred = program.forward(["run1"])

    assert program._mutable_files == {"simple.md"}
    assert program._fixed_updates["simple.md"] == "Hello\nADD_LINE: target_only"
    assert program._fixed_updates["static.md"] == "Static"
    assert pred_by_file["static.md"] not in pred.feedback_by_pred
    assert "ADD_LINE: target_only" in pred.feedback_by_pred[pred_by_file["simple.md"]]

    getattr(program, pred_by_file["static.md"]).signature.instructions = "Mutated static content"
    updates = program.get_practice_updates()
    assert updates["simple.md"] == "Hello\nADD_LINE: target_only"
    assert updates["static.md"] == "Static"


def test_bundle_program_no_attribution_warns_and_freezes(tmp_path, capsys):
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

    pred_name = next(iter(program.practice_by_pred.keys()))
    getattr(program, pred_name).signature.instructions = "Hello\nADD_LINE: should_not_apply"

    eval_result = EvaluationResult(
        passed=False,
        score=0.2,
        notes=["global only"],
        practice_attribution=PracticeAttribution(),
    )

    with patch("promptopt.run_gepa.evaluate_bundle", return_value=eval_result):
        pred = program.forward(["run1"])

    captured = capsys.readouterr()
    assert "[GEPA] No practice notes for run run1; skipping mutations for this run." in captured.out
    assert program._mutable_files == set()
    assert pred.feedback_by_pred == {}

    getattr(program, pred_name).signature.instructions = "changed_again"
    updates = program.get_practice_updates()
    assert updates["simple.md"] == "Hello"


def test_bundle_program_excludes_agents_templates_from_predictors(tmp_path):
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
        passthrough_files={
            "agents/lead.md": "Lead template",
            "agents/engineer.md": "Engineer template",
        },
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

    predictor_files = {practice.file_name for practice in program.practice_by_pred.values()}
    assert predictor_files == {"simple.md"}


def test_bundle_program_offending_without_notes_does_not_mutate(tmp_path, capsys):
    dspy.configure(lm=DummyLM({ "": { "response": "ok" } }))

    simple = PracticeFile(
        file_name="simple.md",
        name="simple",
        frontmatter="Name: simple",
        body="Hello",
    )

    seed_bundle = Bundle(
        bundle_id="seed",
        path=tmp_path / "seed",
        practices={"simple.md": simple},
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

    pred_name = next(iter(program.practice_by_pred.keys()))
    getattr(program, pred_name).signature.instructions = "Hello\nADD_LINE: should_not_apply"

    eval_result = EvaluationResult(
        passed=False,
        score=0.2,
        notes=["global only"],
        practice_attribution=PracticeAttribution(
            offending_practices=["simple"],
            notes_by_practice={},
        ),
    )

    with patch("promptopt.run_gepa.evaluate_bundle", return_value=eval_result):
        pred = program.forward(["run1"])

    captured = capsys.readouterr()
    assert "[GEPA] No practice notes for run run1; skipping mutations for this run." in captured.out
    assert program._mutable_files == set()
    assert pred.feedback_by_pred == {}

    getattr(program, pred_name).signature.instructions = "changed_again"
    updates = program.get_practice_updates()
    assert updates["simple.md"] == "Hello"


def test_bundle_program_run_context_contains_artifacts(tmp_path):
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
        goal="Build login flow",
        base_commit="abc123",
        git_diff="diff --git a/foo b/foo\n+line",
        dotnet_test="test output",
        dotnet_build="build output",
        outputs={"architect": "plan details", "engineer": "impl details"},
    )
    cache = MagicMock()
    program = BundleProgram(
        seed_bundle=seed_bundle,
        runs_by_id={"run1": run},
        bundle_root=tmp_path / "bundles",
        log_dir=tmp_path / "logs",
        cache=cache,
        cli_command="cli",
        timeout=5,
    )

    context = program._build_run_context(["run1"])
    assert "run_id: run1" in context
    assert "Build login flow" in context
    assert "abc123" in context
    assert "diff --git a/foo b/foo" in context
    assert "test output" in context
    assert "build output" in context
    assert "[architect]" in context
    assert "[engineer]" in context
    assert "prompts.json" not in context

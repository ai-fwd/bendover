import shutil
from unittest.mock import patch
import pytest
from promptopt.models import EvaluationResult, RunArtifact
from promptopt.run_gepa import (
    evaluate_run_replay,
    metric_fn,
    prepare_task_dir,
    select_feedback_targeted_components,
    configure_reflection_lm,
    ReflectionLMTranscriptCallback,
    ReflectionTranscriptRecorder,
)
from dspy import Prediction

def test_metric_fn_signature():
    """
    Verifies that metric_fn accepts the 5 arguments required by DSPy GEPA.
    (gold, pred, trace, pred_name, pred_trace)
    """
    gold = None
    pred = Prediction(score=0.85)
    trace = None
    pred_name = None
    pred_trace = None
    
    # This should not raise TypeError
    score = metric_fn(gold, pred, trace, pred_name, pred_trace)
    
    assert score == 0.85

def test_gepa_initialization():
    from dspy.teleprompt import GEPA
    from dspy import LM
    
    lm = LM("openai/gpt-4o-mini")
    
    # checking if it raises error without required params
    # This reflects the current bug where we didn't pass reflection_lm
    # But checking what we WANT: it should work IF we pass everything.
    
    # We want to verify that run_gepa's usage is correct.
    # So we should simulate what run_gepa does.
    
    # Successful init
    gepa = GEPA(metric=metric_fn, max_full_evals=10, reflection_lm=lm)
    assert gepa is not None


def test_select_feedback_targeted_components_prefers_feedback_by_pred():
    class _State:
        list_of_named_predictors = ["practice_0", "practice_1", "practice_2"]
        named_predictor_id_to_update_next_for_program_candidate = [0]

    state = _State()
    trajectories = [
        {
            "prediction": Prediction(
                feedback_by_pred={
                    "practice_2": "targeted",
                }
            )
        }
    ]
    selected = select_feedback_targeted_components(
        state=state,
        trajectories=trajectories,
        subsample_scores=[0.7],
        candidate_idx=0,
        candidate={
            "practice_0": "a",
            "practice_1": "b",
            "practice_2": "c",
        },
    )
    assert selected == ["practice_2"]


def test_prepare_task_dir_copies_previous_run_results(tmp_path):
    run_dir = tmp_path / "runs" / "run-1"
    run_dir.mkdir(parents=True)
    run_result_content = '{"has_code_changes": true, "completion_signaled": true}'
    (run_dir / "run_result.json").write_text(run_result_content)

    run = RunArtifact(
        run_id="run-1",
        run_dir=run_dir,
        goal="Ship feature",
        base_commit="abc123",
    )

    task_dir = prepare_task_dir(run)
    try:
        copied_path = task_dir / "previous_run_results.json"
        assert copied_path.exists()
        assert copied_path.read_text() == run_result_content
    finally:
        shutil.rmtree(task_dir, ignore_errors=True)


def test_evaluate_run_replay_cleans_up_temp_task_dir_on_success(tmp_path, monkeypatch):
    run_dir = tmp_path / "runs" / "run-1"
    run_dir.mkdir(parents=True)
    run = RunArtifact(
        run_id="run-1",
        run_dir=run_dir,
        goal="Ship feature",
        base_commit="abc123",
    )
    bundle_path = tmp_path / "bundle"
    bundle_path.mkdir()
    log_dir = tmp_path / "logs"

    seen_task_path = None

    def fake_evaluate_bundle(bundle_path, task_path, cli_command, log_dir, timeout_seconds, run_label):
        nonlocal seen_task_path
        seen_task_path = task_path
        assert task_path.exists()
        return EvaluationResult(passed=True, score=0.75)

    monkeypatch.setattr("promptopt.run_gepa.evaluate_bundle", fake_evaluate_bundle)

    result = evaluate_run_replay(
        bundle_path=bundle_path,
        run=run,
        cli_command="promptopt-cli",
        log_dir=log_dir,
        timeout_seconds=30,
        run_label=run.run_id,
    )

    assert result.score == 0.75
    assert seen_task_path is not None
    assert not seen_task_path.exists()


def test_evaluate_run_replay_cleans_up_temp_task_dir_on_failure(tmp_path, monkeypatch):
    run_dir = tmp_path / "runs" / "run-1"
    run_dir.mkdir(parents=True)
    run = RunArtifact(
        run_id="run-1",
        run_dir=run_dir,
        goal="Ship feature",
        base_commit="abc123",
    )
    bundle_path = tmp_path / "bundle"
    bundle_path.mkdir()
    log_dir = tmp_path / "logs"

    seen_task_path = None

    def fake_evaluate_bundle(bundle_path, task_path, cli_command, log_dir, timeout_seconds, run_label):
        nonlocal seen_task_path
        seen_task_path = task_path
        assert task_path.exists()
        raise RuntimeError("boom")

    monkeypatch.setattr("promptopt.run_gepa.evaluate_bundle", fake_evaluate_bundle)

    with pytest.raises(RuntimeError, match="boom"):
        evaluate_run_replay(
            bundle_path=bundle_path,
            run=run,
            cli_command="promptopt-cli",
            log_dir=log_dir,
            timeout_seconds=30,
            run_label=run.run_id,
        )

    assert seen_task_path is not None
    assert not seen_task_path.exists()


def test_configure_reflection_lm_attaches_transcript_callback():
    recorder = ReflectionTranscriptRecorder(enabled=True)

    with patch("promptopt.run_gepa.dspy.LM") as mock_lm:
        configure_reflection_lm("openai/gpt-4o-mini", cache_enabled=False, transcript_recorder=recorder)

    callbacks = mock_lm.call_args.kwargs.get("callbacks")
    assert isinstance(callbacks, list)
    assert len(callbacks) == 1
    assert isinstance(callbacks[0], ReflectionLMTranscriptCallback)


def test_test_reflection_lm_records_transcript_and_writes_markdown(tmp_path):
    recorder = ReflectionTranscriptRecorder(enabled=True)
    reflection_lm = configure_reflection_lm("test", transcript_recorder=recorder)

    outputs = reflection_lm(
        "Instruction block\nADD_LINE: include unit tests",
        messages=[{"role": "user", "content": "prompt body"}],
    )

    transcript_path = tmp_path / "reflection_transcript.md"
    recorder.write_markdown(transcript_path)
    transcript = transcript_path.read_text()

    assert outputs
    assert "## Call 1" in transcript
    assert "Instruction block" in transcript
    assert "include unit tests" in transcript
    assert "### Output" in transcript


def test_reflection_transcript_markdown_preserves_call_order(tmp_path):
    recorder = ReflectionTranscriptRecorder(enabled=True)
    callback = ReflectionLMTranscriptCallback(recorder)

    callback.on_lm_start(
        call_id="call-1",
        instance=None,
        inputs={"prompt": "first prompt", "messages": [{"role": "user", "content": "first"}], "kwargs": {}},
    )
    callback.on_lm_end(call_id="call-1", outputs=["first output"], exception=None)
    callback.on_lm_start(
        call_id="call-2",
        instance=None,
        inputs={"prompt": "second prompt", "messages": [{"role": "user", "content": "second"}], "kwargs": {}},
    )
    callback.on_lm_end(call_id="call-2", outputs=["second output"], exception=None)

    transcript_path = tmp_path / "reflection_transcript.md"
    recorder.write_markdown(transcript_path)
    transcript = transcript_path.read_text()

    call_1_index = transcript.index("## Call 1")
    call_2_index = transcript.index("## Call 2")
    assert call_1_index < call_2_index
    assert "first prompt" in transcript
    assert "second output" in transcript

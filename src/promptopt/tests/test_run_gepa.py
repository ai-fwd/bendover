import pytest
import shutil
from promptopt.models import RunArtifact
from promptopt.run_gepa import metric_fn, prepare_task_dir, select_feedback_targeted_components
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

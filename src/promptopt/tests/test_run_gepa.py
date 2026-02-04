import pytest
from promptopt.run_gepa import metric_fn
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


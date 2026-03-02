import dspy

from promptopt.gepa_runtime import GepaRuntimeContext, PromptOptGepaLogger, RawLogWriter, _evaluate_examples_no_stragglers
from promptopt.status import PromptOptStatusEvent, set_current_status_sink


class RecordingStatusSink:
    def __init__(self):
        self.events: list[PromptOptStatusEvent] = []

    def emit(self, event: PromptOptStatusEvent) -> None:
        self.events.append(event)

    def close(self) -> None:
        return None


def test_gepa_logger_parses_selected_program_and_best_scores(tmp_path):
    sink = RecordingStatusSink()
    previous_sink = set_current_status_sink(sink)
    raw_log = RawLogWriter(tmp_path / "gepa_raw.log")
    try:
        logger = PromptOptGepaLogger(GepaRuntimeContext(), raw_log)
        logger.log("Iteration 1: Selected program 0 score: 0.7")
        logger.log("Iteration 1: Found a better program on the valset with score 0.8.")
        logger.log("Iteration 1: Best score on valset: 0.8")
    finally:
        raw_log.close()
        set_current_status_sink(previous_sink)

    kinds = [event.kind for event in sink.events]
    assert "gepa_candidate_selected" in kinds
    assert "gepa_better_candidate" in kinds
    assert "gepa_best_score_updated" in kinds


def test_no_straggler_evaluation_uses_zero_timeout(monkeypatch):
    created_kwargs = {}

    class FakeExecutor:
        def __init__(self, **kwargs):
            created_kwargs.update(kwargs)

        def execute(self, function, data):
            return [function(item) for item in data]

    monkeypatch.setattr("promptopt.gepa_runtime.ParallelExecutor", FakeExecutor)

    class Program:
        def __call__(self, **kwargs):
            del kwargs
            return dspy.Prediction(score=1.0)

    example = dspy.Example(value="x").with_inputs("value")
    results = _evaluate_examples_no_stragglers(
        Program(),
        [example],
        lambda gold, pred: pred.score,
        num_threads=3,
        failure_score=0.0,
        provide_traceback=False,
        max_errors=1,
    )

    assert results[0][2] == 1.0
    assert created_kwargs["timeout"] == 0
    assert created_kwargs["straggler_limit"] == 0

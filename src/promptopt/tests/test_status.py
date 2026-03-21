from promptopt.status import PromptOptRunState, PromptOptStatusEvent, apply_event


def _event(kind: str, **details):
    return PromptOptStatusEvent.create(kind, **details)


def test_state_enters_draining_until_active_evaluations_finish():
    state = PromptOptRunState()

    apply_event(
        state,
        _event(
            "startup",
            phase="startup",
            root=".mystro/promptopt",
            run_count=1,
            reflection_transcript_enabled=True,
            heartbeat_seconds=15,
        ),
    )
    apply_event(
        state,
        _event(
            "eval_started",
            key="bundle:task:attempt1",
            run_label="run-1",
            candidate_id="bundle",
            task_id="task",
            attempt=1,
            out_dir="/tmp/out",
            status="running",
            started_at_utc="2026-03-01T00:00:00Z",
            elapsed_seconds=0,
        ),
    )
    apply_event(
        state,
        _event(
            "optimization_completed",
            best_bundle="bundle",
            final_score=0.8,
            events_path="/tmp/events.jsonl",
            raw_log_path="/tmp/gepa_raw.log",
            reflection_transcript_path="/tmp/reflection_transcript.md",
        ),
    )

    assert state.phase == "draining"
    assert state.active_evaluation_count == 1
    assert state.best_bundle == "bundle"
    assert state.final_score == 0.8

    apply_event(
        state,
        _event(
            "eval_finished",
            key="bundle:task:attempt1",
            run_label="run-1",
            candidate_id="bundle",
            task_id="task",
            attempt=1,
            out_dir="/tmp/out",
            passed=True,
            score=0.8,
        ),
    )

    assert state.phase == "completed"
    assert state.active_evaluation_count == 0


def test_state_tracks_eval_lifecycle_without_creating_cache_hit_entries():
    state = PromptOptRunState()

    apply_event(
        state,
        _event(
            "eval_cache_hit",
            run_label="run-1",
            candidate_id="bundle-a",
            task_id="run-1",
            score=0.7,
        ),
    )
    assert state.active_evaluation_count == 0
    assert state.active_bundle == "bundle-a"

    apply_event(
        state,
        _event(
            "eval_started",
            key="bundle-a:run-1:attempt1",
            run_label="run-1",
            candidate_id="bundle-a",
            task_id="run-1",
            attempt=1,
            out_dir="/tmp/out",
            status="running",
            started_at_utc="2026-03-01T00:00:00Z",
            elapsed_seconds=0,
        ),
    )
    apply_event(
        state,
        _event(
            "eval_heartbeat",
            key="bundle-a:run-1:attempt1",
            run_label="run-1",
            candidate_id="bundle-a",
            task_id="run-1",
            attempt=1,
            elapsed_seconds=45,
            status="running",
        ),
    )

    assert state.active_evaluation_count == 1
    assert state.active_evaluations["bundle-a:run-1:attempt1"].elapsed_seconds == 45

    apply_event(
        state,
        _event(
            "eval_finished",
            key="bundle-a:run-1:attempt1",
            run_label="run-1",
            candidate_id="bundle-a",
            task_id="run-1",
            attempt=1,
            out_dir="/tmp/out",
            passed=True,
            score=0.7,
        ),
    )

    assert state.active_evaluation_count == 0

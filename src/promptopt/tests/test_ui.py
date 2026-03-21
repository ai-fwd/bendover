from promptopt.status import ActiveEvaluation, GepaProgressSnapshot, PromptOptRunState
from promptopt.ui import build_render_text


def test_live_renderer_includes_dashboard_sections():
    state = PromptOptRunState(
        phase="reflection",
        root=".mystro/promptopt",
        run_count=1,
        started_at_utc="2026-03-01T00:00:00Z",
        reflection_transcript_enabled=True,
        heartbeat_seconds=15,
        active_bundle="gengepa_current",
        best_bundle="gengepa_best",
        best_score=0.8,
        final_score=0.8,
        latest_warning="No valset provided; using trainset as valset.",
        latest_reflection_preview="preview text",
        recent_activity=[
            "startup root=.mystro/promptopt runs=1",
            "iteration 1 selected program 0 score=0.7",
            "iteration 1 proposed practice_2",
        ],
        active_evaluations={
            "gengepa_current:run-1:attempt1": ActiveEvaluation(
                key="gengepa_current:run-1:attempt1",
                run_label="run-1",
                candidate_id="gengepa_current",
                task_id="run-1",
                attempt=1,
                out_dir="/tmp/out",
                status="running",
                started_at_utc="2026-03-01T00:00:00Z",
                elapsed_seconds=45,
            )
        },
        gepa=GepaProgressSnapshot(
            total_metric_calls=4,
            rollouts_started=1,
            rollouts_completed=1,
            current_iteration=1,
            selected_program_idx=0,
            selected_program_score=0.7,
            last_reflection_targets=("practice_2",),
            reflection_running=True,
            last_proposal_target="practice_2",
            last_proposal_preview="Use a set and add tests",
            train_batches=1,
            dev_batches=0,
            max_full_evals=4,
            best_val_score=0.8,
        ),
    )

    text = build_render_text(state)

    assert "PromptOpt" in text
    assert "GEPA" in text
    assert "Active Evaluations" in text
    assert "Recent Activity" in text
    assert "Run Dir:" in text
    assert "Metric Calls:" in text
    assert "Current Score:" in text
    assert "Selected Program" not in text
    assert "run-1" in text
    assert "practice_2" in text
    assert "Use a set and add tests" in text

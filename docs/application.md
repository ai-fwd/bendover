---
Name: agent_orchestration_flow
TargetRole: Architect
AreaOfConcern: Agent Workflow
---
## Intent
Keep the agent loop deterministic and debuggable by treating orchestration phases, validation gates, and run recording as explicit workflow contracts.

## Rules
- Orchestration must fail fast on invalid preconditions (missing context, invalid practice selection, missing runtime artifacts).
- Selected practices must be validated against available practice names before execution.
- Every executed phase must have consistent prompt/output recording so replay and evaluation remain reliable.
- Phase behavior may evolve, but contract changes must be reflected in orchestration docs and tests together.

## Workflow contract
- Pre-flight:
  - Validate environment and load run context.
  - Start run recording with goal, base commit, and bundle identity.
- Lead phase:
  - Select practices from available set.
  - Reject empty selections.
  - Reject unknown practice names.
  - Record lead input and output.
- Plan phase:
  - Current behavior uses goal as effective plan context.
  - Architect planning is currently disabled and must be treated as inactive unless explicitly re-enabled.
- Engineer phase:
  - Build engineer prompt from selected practices, plan context, and composed agent templates (`engineer.md` + `tools.md`).
  - Enforce body validation before execution.
  - Execute in sandbox with retry loop and failure digest feedback.
  - Record per-attempt prompts/outputs and failure digests.
- Post-execution:
  - Persist sandbox artifacts (`git diff`, build, test outputs).
  - Apply patch to source only when run context explicitly enables it.
  - Always finalize run recording.

## Phase status
- Active: Environment check, Lead selection, Engineer execution with retries, artifact persistence.
- Inactive by default: Architect planning, Reviewer critique.
- If an inactive phase is re-enabled, phase recording, prompting, and tests must be restored in the same change.

## Change checklist
- If you change phase order, update orchestration tests and run artifact expectations.
- If you change lead output format, keep selection validation and evaluator input compatibility intact.
- If you change retry behavior, keep failure digest capture and attempt phase naming stable or migrate consumers.
- If you change artifact capture or patch-apply behavior, ensure replay and scoring workflows remain consistent.

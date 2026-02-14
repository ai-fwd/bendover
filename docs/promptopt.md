---
Name: promptopt_context_and_bundles
TargetRole: Engineer
AreaOfConcern: Prompt Optimization
---
## Intent
Keep PromptOpt runs reproducible by treating run context and bundle layout as strict contracts, not incidental implementation details.

## Rules
- Every PromptOpt execution must set `PromptOptRunContext` before calling orchestration code.
- `PromptOptRunRecorder` and evaluator services read from context and should not infer state from ambient runtime globals.
- Bundle resolution must be explicit and validated before evaluation starts.
- Replay and scoring paths may differ in entrypoint behavior, but they must converge to the same evaluator contract output.

## Run context contract
- `OutDir`: canonical run artifact root for prompts, outputs, and evaluation artifacts.
- `Capture`: enables/disables run artifact persistence.
- `RunId`: stable run identity for traceability; may be supplied or generated.
- `BundleId`: identity of the bundle used for selection/evaluation.
- `ApplySandboxPatchToSource`: controls whether sandbox diff is applied back to source workspace.

## Bundle contract
- A bundle must contain `practices/` for practice files.
- Replay bundles must include `agents/lead.md`, `agents/engineer.md`, and `agents/tools.md`.
- `tools.md` is treated as required runtime prompt context for engineer execution.
- In scoring mode, bundle resolution may come from override, `bundle_id.txt`, or run metadata (`bundle_id`/`bundleId`).
- Special bundle aliases like `current`/`default` resolve to root `.bendover` bundle semantics.

## Mode behavior
- Replay mode:
  - Clones to an isolated workspace at target commit.
  - Copies bundle practices/prompts into workspace-local bundle path.
  - Sets context with `ApplySandboxPatchToSource=false` for evaluation safety.
- Score-existing-run mode:
  - Uses existing run directory artifacts.
  - Resolves bundle from override or recorded run metadata.
  - Rewrites/refreshes `evaluator.json` using current evaluator rules.

## Change checklist
- If you add context fields, update setter and reader code paths together.
- If you change bundle structure, update validation and failure messages in replay/scoring orchestrators.
- If you alter bundle resolution rules, keep replay and score mode behavior documented and test-covered.
- If you change artifact names or capture flow, ensure evaluator and downstream tooling still parse outputs correctly.

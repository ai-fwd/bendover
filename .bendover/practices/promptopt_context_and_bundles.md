---
Name: promptopt_context_and_bundles
TargetRole: Engineer
AreaOfConcern: Prompt Optimization
---
Prompt optimization runs are driven by a shared runtime context and bundle-resolved practice paths.

Key conventions:
- `PromptOptRunContext` (via `IPromptOptRunContextAccessor`) carries `OutDir`, `Capture`, `RunId`, and `BundleId` for the current run. It is set in the composition root before invoking `AgentOrchestrator`.
- `PromptOptRunRecorder` and related services read from the context; they assume it is present and throw if missing.
- Practice paths should be resolved via `PromptBundleResolver` (active bundle or explicit bundle path), not hard-coded paths.

If a feature needs a practice path or run metadata, prefer reading it from the run context or bundle resolver and keep the application logic free of environment-specific path assumptions.

# Story 07 - Sandbox Patch Apply Behavior

## Repro commands (PatchApplyProof)

1. Verify local-mode patch apply behavior (enabled/disabled guard in orchestrator):

```bash
dotnet test tests/Bendover.Tests/Bendover.Tests.csproj -c Debug --filter "FullyQualifiedName~AgentOrchestratorTests.RunAsync_ShouldApplySandboxPatchToSource_WhenEnabled|FullyQualifiedName~AgentOrchestratorTests.RunAsync_ShouldNotApplySandboxPatchToSource_WhenDisabled" -v normal
```

Expected:
- Exit code: `0`
- Passed tests:
  - `RunAsync_ShouldApplySandboxPatchToSource_WhenEnabled`
  - `RunAsync_ShouldNotApplySandboxPatchToSource_WhenDisabled`

What it validates:
- Enabled mode calls `git apply --whitespace=nowarn -` with sandbox patch content through stdin.
- Disabled mode skips patch apply completely.

2. Verify benchmark orchestration sets patch apply to disabled:

```bash
dotnet test tests/Bendover.PromptOpt.CLI.Tests/Bendover.PromptOpt.CLI.Tests.csproj -c Debug --filter FullyQualifiedName~BenchmarkRunOrchestratorTests.RunAsync_ShouldUseBundlePathAndTaskPathAndEmitArtifacts -v normal
```

Expected:
- Exit code: `0`
- Passed test:
  - `BenchmarkRunOrchestratorTests.RunAsync_ShouldUseBundlePathAndTaskPathAndEmitArtifacts`

What it validates:
- Benchmark run context sets `ApplySandboxPatchToSource=false`, preventing host patch application.

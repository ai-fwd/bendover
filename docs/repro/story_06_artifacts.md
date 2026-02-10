# Story 06 - Sandbox Artifacts Persisted To Host

## Repro command (ArtifactProof)

```bash
dotnet test tests/Bendover.Tests/Bendover.Tests.csproj -c Debug --filter FullyQualifiedName~Story6ArtifactFlowTests.RunAsync_PersistsSandboxArtifactsToHostOutDir_WithExpectedNames -v normal
```

## Expected result
- Exit code: `0`
- Test output includes:
  - `Passed Bendover.Tests.Story6ArtifactFlowTests.RunAsync_PersistsSandboxArtifactsToHostOutDir_WithExpectedNames`

## What the proof run validates
- One orchestrator run persists sandbox-generated artifacts to host output directory.
- Host output directory contains expected artifact names:
  - `git_diff.patch`
  - `dotnet_build.txt` or `dotnet_build_error.txt`
  - `dotnet_test.txt` or `dotnet_test_error.txt`
- Artifact contents come from sandbox command outputs, not host `git`/`dotnet` execution inside the recorder.

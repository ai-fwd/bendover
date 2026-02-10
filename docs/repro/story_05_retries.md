# Story 05 - Engineer Retries With Failure Digest

## Repro command (RetryProof)

```bash
dotnet test tests/Bendover.Tests/Bendover.Tests.csproj -c Debug --filter FullyQualifiedName~AgentOrchestratorTests.RunAsync_ShouldRetryEngineer_WhenExecutionFailsThenSucceed -v normal
```

## Scenario used by the proof
- Engineer response #1: `Console.WriteLine("bad"` (compile error)
- Sandbox execution result #1: non-zero exit with output `(1,27): error CS1026: ) expected`
- Engineer response #2: `Console.WriteLine("good");`
- Sandbox execution result #2: exit code `0`

## Expected outcomes
- Test exits with code `0`
- Test reports `Passed Bendover.Tests.AgentOrchestratorTests.RunAsync_ShouldRetryEngineer_WhenExecutionFailsThenSucceed`
- Retry prompt validation in the test confirms retry prompt includes failure digest entries:
  - `exit_code=1`
  - compile error marker `CS1026`

## What this proves
- Orchestrator retries after a failed engineer execution.
- Retry path injects digest information into the next engineer prompt.
- A subsequent corrected engineer response succeeds and exits retry loop.

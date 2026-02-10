# Story 04 - Sandbox Engineer Body Execution

## Repro steps

1. Build ScriptRunner artifacts used inside sandbox workspace copy:

```bash
dotnet build src/Bendover.ScriptRunner/Bendover.ScriptRunner.csproj -c Debug
```

Expected:
- Exit code: `0`
- Output contains: `Build succeeded.`

2. Start container with local repo mounted read-only at `/input/repo`:

```bash
docker run -d --name bendover_story4_proof -v "$(pwd)":/input/repo:ro mcr.microsoft.com/dotnet/sdk:10.0 sleep infinity
```

Expected:
- Exit code: `0`
- Prints container ID

3. Copy repo into mutable sandbox workspace:

```bash
docker exec bendover_story4_proof bash -lc 'rm -rf /workspace && mkdir -p /workspace && cp -a /input/repo/. /workspace'
```

Expected:
- Exit code: `0`

4. Write engineer body to fixed path in sandbox:

```bash
docker exec bendover_story4_proof bash -lc 'cat <<"CSX" > /workspace/engineer_body.csx
sdk.WriteFile("/workspace/story4_sandbox.txt", "created inside sandbox workspace");
Console.WriteLine("SandboxProof execution complete");
CSX'
```

Expected:
- Exit code: `0`

5. Execute ScriptRunner against sandbox body file:

```bash
docker exec bendover_story4_proof bash -lc 'cd /workspace && dotnet src/Bendover.ScriptRunner/bin/Debug/net10.0/Bendover.ScriptRunner.dll --body-file /workspace/engineer_body.csx'
```

Expected:
- Exit code: `0`
- Stdout contains: `SandboxProof execution complete`

6. Inspect command result artifact in sandbox:

```bash
docker exec bendover_story4_proof bash -lc 'test -f /workspace/story4_sandbox.txt && cat /workspace/story4_sandbox.txt'
```

Expected:
- Exit code: `0`
- Output contains: `created inside sandbox workspace`

7. Verify host repo was not changed by sandbox write:

```bash
test -f "$(pwd)/story4_sandbox.txt"; echo $?
```

Expected:
- Exit code from `test` command output is `1` (file absent on host)

8. Cleanup:

```bash
docker rm -f bendover_story4_proof
```

Expected:
- Exit code: `0`

## Observed proof outputs
- `start_exit_code=0`
- `prepare_exit_code=0`
- `write_exit_code=0`
- `execute_exit_code=0`
- `inspect_exit_code=0`
- `host_file_present=no`

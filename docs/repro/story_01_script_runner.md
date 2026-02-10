# Story 01 - Roslyn ScriptRunner

## Commands

1. Build solution (requested in proof workstream):

```bash
dotnet build Bendover.sln -c Debug
```

Observed in this environment:
- Exit code: `1`
- Output:
  - `Build FAILED.`
  - No project errors emitted by the default logger.
- Diagnostic log shows environment issue: `MSB4276` workload resolver SDK folders missing under `/usr/lib/dotnet/sdk/10.0.100/Sdks/...`.

2. Build ScriptRunner project directly:

```bash
dotnet build src/Bendover.ScriptRunner/Bendover.ScriptRunner.csproj -c Debug
```

Expected:
- Exit code: `0`
- Output contains: `Build succeeded.`

3. Run valid body file:

```bash
cat <<'CSX' > tmp/story1/valid_body.csx
Console.WriteLine("RunnerProof: hello from body");
CSX

dotnet src/Bendover.ScriptRunner/bin/Debug/net10.0/Bendover.ScriptRunner.dll --body-file tmp/story1/valid_body.csx
```

Expected:
- Exit code: `0`
- Stdout contains: `RunnerProof: hello from body`

4. Run invalid body file:

```bash
cat <<'CSX' > tmp/story1/invalid_body.csx
Console.WriteLine("broken"
CSX

dotnet src/Bendover.ScriptRunner/bin/Debug/net10.0/Bendover.ScriptRunner.dll --body-file tmp/story1/invalid_body.csx
```

Expected:
- Exit code: non-zero (`1` observed)
- Stderr contains compile diagnostic similar to: `(1,27): error CS1026: ) expected`

## Files created/changed by repro
- `tmp/story1/valid_body.csx`
- `tmp/story1/invalid_body.csx`

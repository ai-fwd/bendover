# Story 03 - SDK Globals Facade

## Build command

```bash
dotnet build src/Bendover.ScriptRunner/Bendover.ScriptRunner.csproj -c Debug
```

Expected:
- Exit code: `0`
- Output contains: `Build succeeded.`

## Repro steps

1. Create grouped API body:

```bash
cat <<'CSX' > tmp/story3/grouped_body.csx
sdk.File.Write("tmp/story3/grouped.txt", "written via sdk.File.Write");
Console.WriteLine("SdkFacadeProof grouped style complete");
CSX
```

2. Create shorthand API body:

```bash
cat <<'CSX' > tmp/story3/shorthand_body.csx
sdk.WriteFile("tmp/story3/shorthand.txt", "written via sdk.WriteFile");
Console.WriteLine("SdkFacadeProof shorthand style complete");
CSX
```

3. Run grouped style:

```bash
dotnet src/Bendover.ScriptRunner/bin/Debug/net10.0/Bendover.ScriptRunner.dll --body-file tmp/story3/grouped_body.csx
```

Expected:
- Exit code: `0`
- Stdout contains: `SdkFacadeProof grouped style complete`

4. Run shorthand style:

```bash
dotnet src/Bendover.ScriptRunner/bin/Debug/net10.0/Bendover.ScriptRunner.dll --body-file tmp/story3/shorthand_body.csx
```

Expected:
- Exit code: `0`
- Stdout contains: `SdkFacadeProof shorthand style complete`

5. Verify files:

```bash
test -f tmp/story3/grouped.txt && cat tmp/story3/grouped.txt
test -f tmp/story3/shorthand.txt && cat tmp/story3/shorthand.txt
```

Expected:
- `tmp/story3/grouped.txt` exists with content `written via sdk.File.Write`
- `tmp/story3/shorthand.txt` exists with content `written via sdk.WriteFile`

## Files created/changed by repro
- `tmp/story3/grouped_body.csx`
- `tmp/story3/shorthand_body.csx`
- `tmp/story3/grouped.txt`
- `tmp/story3/shorthand.txt`

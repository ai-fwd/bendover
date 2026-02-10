# Story 02 - Engineer Body Validator

## Build command

```bash
dotnet build src/Bendover.ScriptRunner/Bendover.ScriptRunner.csproj -c Debug
```

Expected:
- Exit code: `0`
- Output contains: `Build succeeded.`

## Validation command pattern

```bash
dotnet src/Bendover.ScriptRunner/bin/Debug/net10.0/Bendover.ScriptRunner.dll --body-file <path>
```

## Sample inputs and expected results

1. Valid body (`tmp/story2/valid.csx`)

```csharp
Console.WriteLine("ValidatorProof: valid body executed");
```

Expected:
- Exit code: `0`
- Stdout contains: `ValidatorProof: valid body executed`

2. Markdown fences (`tmp/story2/invalid_markdown.csx`)

```text
```csharp
Console.WriteLine("no");
```
```

Expected:
- Exit code: `1`
- Message starts with: `Engineer body rejected:`
- Message includes: `contains markdown fences`

3. `#r` directive (`tmp/story2/invalid_reference.csx`)

```csharp
#r "Some.Assembly.dll"
Console.WriteLine("no");
```

Expected:
- Exit code: `1`
- Message includes: `contains #r directives`

4. `using` directive (`tmp/story2/invalid_using.csx`)

```csharp
using System;
Console.WriteLine("no");
```

Expected:
- Exit code: `1`
- Message includes: `contains using directives`

5. Type/member declaration (`tmp/story2/invalid_declaration.csx`)

```csharp
class Greeter
{
    public void SayHi() => Console.WriteLine("hi");
}
```

Expected:
- Exit code: `1`
- Message includes: `contains namespace/type/member declarations`

6. Empty body (`tmp/story2/invalid_empty.csx`)

Expected:
- Exit code: `1`
- Message includes: `body is empty`

7. No global statements (`tmp/story2/invalid_no_global_statement.csx`)

```csharp
// comment only
```

Expected:
- Exit code: `1`
- Message includes: `must include at least one global statement`

## Files created by repro
- `tmp/story2/valid.csx`
- `tmp/story2/invalid_markdown.csx`
- `tmp/story2/invalid_reference.csx`
- `tmp/story2/invalid_using.csx`
- `tmp/story2/invalid_declaration.csx`
- `tmp/story2/invalid_empty.csx`
- `tmp/story2/invalid_no_global_statement.csx`

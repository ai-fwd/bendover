# Issues
_Note: Most of these need to be addressed via the agent and NOT manually to validate the hypothesis. Tracking for now._

## Practices
- [ ] Rename to Policies
- [ ] Need to create better policies based on issues below
- [ ] Need refactoring policies 

## Domain
- [ ] Code smell on interfaces

## Application
- [ ] AgentOrchestrator is serializing the `selectedPractices`. That's not necessary.
- [x] PromptOptRunRecorder.FinalizeRunAsync() always called but it should be based on a flag
- [ ] AgentOrchestrator doesn't iterate based on reviewer feedback
- [x] AgentOrchestrator practice selection needs to be more robust
- [ ] AgentOrchestrator doesn't pass any context so evaluation runs are meaningless
- [ ] AgentOrchestrator is currently running as 1 shot with retries vs. multi-turn
- [ ] AgentOrchestrator is focused on script run vs. goal accomplished


## Infrastructure
- [ ] PromptOptRunRecorder is saving the run based on CWD. Need to resolve explicitly.
- [ ] Need to change the naming to just RunRecorder
- [x] Based on new practice specific mutation the PromptOptRunEvaluator needs to be updated to create a correct evaluator.json containing:

    ```json
    "practice_attribution": {
    "selected_practices": [
      "simple_practice",
      "static_practice"
    ],
    "offending_practices": [
      "simple_practice",
      "static_practice"
    ],
    "notes_by_practice": {
      "simple_practice": [
        "ADD_LINE: include_reflected_line"
      ]
    }
    ```
- [ ] do we need to run the all the `StartContainerAsync` commands individually or is there a more efficient way?

## Presentation.CLI
- [ ] Support multi-line input via Shift+Enter
- [x] Needs to support Codex auth flow
- [ ] Changed to .env file but readme is showing support for different agent configs via appsettings.json & configuration.GetSection(AgentOptions.SectionName) still exists. I don't see tests.

## Presentation.Server
- [ ] Falling behind the CLI -- will require large changes in design

## PromptOpt.Cli
- [ ] Rename to PolicyOpt -- don't need the CLI part. It's just a tool
- [ ] Rule for building solution successfully `dotnet build Bendover.sln`
- [ ] Rule for running test at solution successfully `dotnet test Bendover.sln`
- [x] Using a StubAgentRunner. Needs to use the actual AgentOrchestrator 
- [x] Ensure metric_fn & design is correct for practice specific mutation
- [x] Validated a full curated run - not perfect but evaluation does work
- [x] Validation is happening in a /tmp directory but it should be happening in the sandbox where real edits are made, and git_diff.patch contains something
- [x] .bendover/agents for system prompts should get optimized separately from practices. For now we're skipping them.

## ScriptRunner
- [ ] Respect the author of the patch, Bendover should get credit.

## SDK
- [x] Need to provide this context to the agent otherwise it has no visibility
- [x] Context is generated on the ScriptRunner but it should be on the SDK project not the ScriptRunner
- [ ] Add a delete file tool


## promptopt (Python)
- [x] --target-practice-file limits to just 1 practice update. Auto choose based on evaluator flags (i.e. SkippingTDD -> tdd_spirit.md)
- [x] missing a way to configure the reflection lm to use gpt oss
- [x] Mutate only practices that are necessary
- [x] Build GEPA run context from replay artifacts (goal/diff/test/build/outputs summary)
- [x] Validate promptopt behavior on a curated run - not perfect but does work
- [ ] Needs to use ChatGPT Pro/Plus subscription like CLI

## Infrastructure (PromptOpt artifacts)
- [x] Capture `dotnet_build.txt` / `dotnet_build_error.txt` in PromptOpt run recorder

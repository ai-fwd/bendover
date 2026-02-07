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
- [ ] PromptOptRunRecorder needs to output to different directories
- [ ] Agentic loop doesn't iterate based on reviewer feedback


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

## Presentation.CLI
- [ ] Support multi-line input via Shift+Enter
- [ ] Needs to support Codex auth flow
- [ ] Changed to .env file but readme is showing support for different agent configs via appsettings.json & configuration.GetSection(AgentOptions.SectionName) still exists. I don't see tests.

## Presentation.Server
- [ ] Falling behind the CLI -- will require large changes in design

## PromptOpt.Cli
- [ ] Rename to PolicyOpt -- don't need the CLI part. It's just a tool
- [ ] Rule for building solution successfully `dotnet build Bendover.sln`
- [ ] Rule for running test at solution successfully `dotnet test Bendover.sln`
- [x] Using a StubAgentRunner. Needs to use the actual AgentOrchestrator 
- [x] Ensure metric_fn & design is correct for practice specific mutation
- [ ]

## SDK
- [ ]

## promptopt (Python)
- [x] --target-practice-file limits to just 1 practice update. Auto choose based on evaluator flags (i.e. SkippingTDD -> tdd_spirit.md)
- [x] missing a way to configure the reflection lm to use gpt oss
- [x] Mutate only practices that are necessary
- [x] Build GEPA run context from replay artifacts (goal/diff/test/build/outputs summary)
- [ ] Validate promptopt behavior on a curated run

## Infrastructure (PromptOpt artifacts)
- [x] Capture `dotnet_build.txt` / `dotnet_build_error.txt` in PromptOpt run recorder

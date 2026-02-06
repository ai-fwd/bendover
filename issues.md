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

## Presentation.CLI
- [ ] Support multi-line input via Shift+Enter
- [ ] Needs to support Codex auth flow

## Presentation.Server
- [ ] Falling behind the CLI -- will require large changes in design

## PromptOpt.Cli
- [ ] Rename to PolicyOpt -- don't need the CLI part. It's just a tool
- [ ] Rule for building solution successfully `dotnet build Bendover.sln`
- [ ] Rule for running test at solution successfully `dotnet test Bendover.sln`
- [x] Using a StubAgentRunner. Needs to use the actual AgentOrchestrator 
- [ ] Ensure metric_fn & design is correct for practice specific mutation

## SDK
- [ ]

## promptopt (Python)
- [x] --target-practice-file limits to just 1 practice update. Auto choose based on evaluator flags (i.e. SkippingTDD -> tdd_spirit.md)
- [x] missing a way to configure the reflection lm to use gpt oss

---
Name: codebase_overview
TargetRole: Engineer
AreaOfConcern: Architecture
---
These are the projects included in this repo and their purpose

| Project | Primary Purpose | Owns | Must Not Own |
|---|---|---|---|
| `Bendover.Domain` | Core business contracts and models | Entities, value concepts, enums, interfaces, domain exceptions | Framework wiring, transport concerns, infrastructure implementation |
| `Bendover.Application` | Use-case orchestration and business workflows | Orchestrators, use-case services, application-level policies, abstraction-driven flows | Concrete external integrations, host/runtime bootstrapping |
| `Bendover.Infrastructure` | Technical adapters and external system integration | Docker/process/filesystem/chat adapters, concrete implementations of application/domain interfaces | Core business policy and use-case decision logic |
| `Bendover.Presentation.CLI` | Local interactive entrypoint | CLI UX, argument handling, runtime composition for local execution | Business logic beyond request orchestration |
| `Bendover.Presentation.Server` | Server/remote host entrypoint | HTTP/SignalR hosting, server composition root, transport endpoint wiring | Domain/application business rules |
| `Bendover.PromptOpt.CLI` | Replay/evaluation/optimization runtime entrypoint | Prompt optimization orchestration, benchmark/scoring commands, evaluator composition | General interactive runtime behavior unrelated to optimization workflows |
| `Bendover.SDK` | Script-facing capability facade | SDK surface for generated script execution, contract-facing helper API | Application orchestration or host-specific runtime policies |
| `Bendover.ScriptRunner` | Executes generated C# script bodies safely | Script argument parsing, body validation, Roslyn execution host | Application orchestration, transport, persistent business state |
| `Bendover.Tests` | Main test suite for core runtime and integrations | Unit/integration/acceptance tests for Domain/Application/Infrastructure/Presentation.CLI | Production feature logic |
| `Bendover.PromptOpt.CLI.Tests` | Focused test suite for optimization runtime | Tests for PromptOpt CLI orchestration, evaluation rules, service registration | Production feature logic |

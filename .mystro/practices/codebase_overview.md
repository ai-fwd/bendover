---
Name: codebase_overview
TargetRole: Engineer
AreaOfConcern: Architecture
---
These are the projects included in this repo and their purpose

| Project | Primary Purpose | Owns | Must Not Own |
|---|---|---|---|
| `Mystro.Domain` | Core business contracts and models | Entities, value concepts, enums, interfaces, domain exceptions | Framework wiring, transport concerns, infrastructure implementation |
| `Mystro.Application` | Use-case orchestration and business workflows | Orchestrators, use-case services, application-level policies, abstraction-driven flows | Concrete external integrations, host/runtime bootstrapping |
| `Mystro.Infrastructure` | Technical adapters and external system integration | Docker/process/filesystem/chat adapters, concrete implementations of application/domain interfaces | Core business policy and use-case decision logic |
| `Mystro.Presentation.CLI` | Local interactive entrypoint | CLI UX, argument handling, runtime composition for local execution | Business logic beyond request orchestration |
| `Mystro.Presentation.Server` | Server/remote host entrypoint | HTTP/SignalR hosting, server composition root, transport endpoint wiring | Domain/application business rules |
| `Mystro.PromptOpt.CLI` | Replay/evaluation/optimization runtime entrypoint | Prompt optimization orchestration, benchmark/scoring commands, evaluator composition | General interactive runtime behavior unrelated to optimization workflows |
| `Mystro.SDK` | Script-facing capability facade | SDK surface for generated script execution, contract-facing helper API | Application orchestration or host-specific runtime policies |
| `Mystro.ScriptRunner` | Executes generated C# script bodies safely | Script argument parsing, body validation, Roslyn execution host | Application orchestration, transport, persistent business state |
| `Mystro.Tests` | Main test suite for core runtime and integrations | Unit/integration/acceptance tests for Domain/Application/Infrastructure/Presentation.CLI | Production feature logic |
| `Mystro.PromptOpt.CLI.Tests` | Focused test suite for optimization runtime | Tests for PromptOpt CLI orchestration, evaluation rules, service registration | Production feature logic |

---
Name: composition_root_di
TargetRole: Engineer
AreaOfConcern: Composition
---
Dependency injection is configured only in the Presentation projects (CLI/Server/PromptOpt.CLI). Application and Infrastructure stay DI-agnostic and accept dependencies via constructors.

When introducing a new service:
- Add the interface in `Bendover.Domain` or `Bendover.Application` (as appropriate).
- Implement it in `Bendover.Infrastructure` (or the relevant adapter project).
- Register it in the composition root (`Program.cs` or `LocalAgentRunner`) as a singleton unless there is a clear need for a shorter lifetime.

Avoid creating service locators or static access; prefer constructor injection and pass the dependency down from the composition root.

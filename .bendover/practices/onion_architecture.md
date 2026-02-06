---
Name: onion_architecture
TargetRole: Architect
AreaOfConcern: Architecture
---
Bendover follows an Onion Architecture with clear dependency direction.

Projects and responsibilities:
- `Bendover.Domain`: core entities, enums, and interfaces (no external dependencies).
- `Bendover.Application`: orchestration and use cases; depends only on `Bendover.Domain` and uses interfaces for IO/external work.
- `Bendover.Infrastructure`: concrete adapters for external systems (Docker, filesystem, OpenAI, Git, DotNet); implements Domain/Application interfaces.
- `Bendover.Presentation.CLI` and `Bendover.Presentation.Server`: composition roots; wire DI, config, and host the app.
- `Bendover.PromptOpt.CLI`: composition root for prompt-optimization runs and replay.
- `Bendover.SDK`: runtime SDK used by generated actor code, implementing Domain contracts.

When adding features, keep dependencies pointing inward: Presentation -> Application -> Domain, with Infrastructure implementing outward-facing adapters.

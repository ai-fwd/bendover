---
Name: composition_root_di
TargetRole: Engineer
AreaOfConcern: Composition
---

## Intent
Centralize dependency wiring and keep core projects DI container agnostic.

## Rules
- DI registrations occur only in Presentation composition roots (Program.cs / LocalAgentRunner).
- Domain, Application, Infrastructure must not reference the DI container or register services.
- All dependencies are provided through constructors. No service locator, no statics.

## Adding a new service
- Define the interface in Domain or Application (where it is consumed).
- Implement it in Infrastructure or a dedicated adapter project.
- Register the mapping in the composition root.

## Lifetime defaults
- Default to transient unless the type is stateless and safe to share.
- Use scoped only when request scoped state is required.
- Singleton requires explicit justification (thread safe, no request state, no disposable resources).

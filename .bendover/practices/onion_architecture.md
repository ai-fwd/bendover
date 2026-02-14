---
Name: onion_architecture
TargetRole: Engineer
AreaOfConcern: Architecture
---

- Prefer structuring the system around Domain, Application, Infrastructure, and Presentation layers with clear responsibilities.

- Keep Domain focused on business entities, value objects, and rules, independent from frameworks, persistence, and transport.

- Let Application coordinate use cases and define interfaces required by the core, without depending on Infrastructure or transport concerns.

- Implement external concerns such as persistence, filesystem, network, and external tools in Infrastructure, behind interfaces defined in the core.

- Let Presentation handle transport concerns and call Application to execute use cases, without referencing Infrastructure implementations directly.

- Keep all dependencies pointing inward toward the Domain. Outer layers may depend on inner layers, but inner layers must remain independent.

- Prefer defining interfaces in the core and implementing them in outer layers so infrastructure can be replaced without modifying business logic.

- Prefer constructor injection and explicit dependencies so components remain decoupled, testable, and easy to reason about.

- Treat direct use of filesystem, network, database, process execution, or framework types in Domain or Application as a design issue.

- Favor designs where core logic remains functional and testable without real infrastructure by substituting alternate implementations.

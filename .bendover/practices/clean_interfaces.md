---
Name: interfaces_best_practices
TargetRole: Engineer
AreaOfConcern: Design
---

- Prefer concrete types by default, introducing interfaces only when substitution, isolation, or extension is needed.

- Use interfaces to represent boundaries such as persistence, external services, filesystem, process execution, time, or environment.

- Allow single implementation interfaces when they enable testing, isolation, or future substitution, but avoid creating interfaces without a clear purpose.

- Treat interfaces that simply mirror a single concrete class with no boundary, substitution, or testing value as unnecessary abstraction.

- Prefer small, cohesive interfaces that reflect a specific capability rather than broad or generic responsibilities.

- Design interfaces around how they are used by consumers, not around implementation details.

- Define interfaces in the layer that consumes them, and implement them in outer layers so dependencies remain replaceable.

- Favor interface designs that keep core logic independent from infrastructure, frameworks, and external systems.

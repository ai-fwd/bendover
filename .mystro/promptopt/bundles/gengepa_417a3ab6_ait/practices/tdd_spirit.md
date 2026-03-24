---
Name: tdd_spirit
TargetRole: Engineer
AreaOfConcern: Design
---

- Prefer writing tests before introducing new behavior so design emerges from usage.
- Let tests define the public surface area and expected behavior of the system.
- Treat difficulty in testing as a signal to improve the design, not the test.
- Favor simple implementations that satisfy the test, then refine through refactoring.
- Keep core logic deterministic and independent from external IO and frameworks.
- Prefer explicit dependencies and constructor injection over hidden or global state.
---
Name: coding_style
TargetRole: Engineer
AreaOfConcern: Code Quality
---

Core requirements to enforce:
1. Never use chained `||` equality comparisons to represent membership in a fixed group (e.g., `x == "a" || x == "b"`).
2. Model fixed membership lists as `static readonly` sets, preferring `HashSet<T>`, and use `Contains(...)`.
3. Keep comparisons case-insensitive where behavior requires it (e.g., use an appropriate comparer like `StringComparer.OrdinalIgnoreCase` in the set).
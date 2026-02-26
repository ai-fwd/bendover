---
Name: coding_style
TargetRole: Engineer
AreaOfConcern: Code Quality
---

- Do not use chained `||` equality comparisons for membership checks. Instead model them as static readonly sets, and prefer `HashSet<T>` when value belongs to a fixed group
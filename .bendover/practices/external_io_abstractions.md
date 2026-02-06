---
Name: external_io_abstractions
TargetRole: Engineer
AreaOfConcern: External IO
---
IO and side effects are abstracted behind interfaces and implemented in Infrastructure. Application code should use the abstractions and avoid direct filesystem/process calls.

Common patterns in this repo:
- Filesystem access goes through `IFileService` or `System.IO.Abstractions.IFileSystem` injected via Infrastructure.
- External tools use interfaces like `IGitRunner` and `IDotNetRunner` (Infrastructure implementations wrap the real processes).

When adding new IO or external dependencies, define a narrow interface in Domain/Application and implement it in Infrastructure so the Application layer stays testable and isolated.

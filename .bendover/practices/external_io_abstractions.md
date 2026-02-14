---
Name: external_io_abstractions
TargetRole: Engineer
AreaOfConcern: External IO
---

- Prefer accessing filesystem, network, process execution, environment, and time through interfaces rather than direct framework calls.

- Keep Application and Domain logic deterministic and independent from IO and side effects.

- Implement IO and external interactions in Infrastructure behind interfaces defined in the core.

- Prefer injecting IO abstractions via constructors so behavior remains replaceable and testable.

- Favor narrow, task focused interfaces that reflect the capability needed, not the underlying technology.

- Treat direct use of System.IO, Process, HttpClient, Environment, or time APIs in Application or Domain as a design issue.

- Follow repo conventions by using IFileService or System.IO.Abstractions.IFileSystem for filesystem access and runner interfaces (example: IGitRunner, IDotNetRunner) for external tools.

namespace Bendover.Application.Run;

public delegate Task RunDelegate(RunStageContext context, Func<RunStageContext, Task> terminal);

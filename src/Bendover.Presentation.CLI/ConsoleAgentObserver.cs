using Bendover.Domain.Interfaces;
namespace Bendover.Presentation.CLI;

public class ConsoleAgentObserver : IAgentObserver
{
    private readonly CliDashboard _dashboard;

    public ConsoleAgentObserver(CliDashboard dashboard)
    {
        _dashboard = dashboard;
    }

    public Task OnEventAsync(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentProgressEvent progress:
                _dashboard.SetLatestInfo(progress.Message ?? string.Empty);
                break;
            case AgentStepEvent step:
                _dashboard.AddStep(step);
                break;
        }

        return Task.CompletedTask;
    }
}

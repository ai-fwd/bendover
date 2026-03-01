using Bendover.Domain.Interfaces;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Bendover.Presentation.Console;

public enum DashboardStatus
{
    Running,
    Success,
    Error
}

public enum EvaluationPanelState
{
    Pending,
    Running,
    Completed,
    Missing,
    ParseError,
    Failed
}

public sealed record LiveCliDashboardOptions(
    string ModelSummary,
    string RunId,
    string RunDir,
    string Goal,
    bool ShowEvaluationPanel,
    bool ShowExecutionPanel = true,
    string? Subtitle = null);

public sealed record EvaluationPanelSnapshot(
    EvaluationPanelState State,
    string BundleDirectory,
    string OutputDirectory,
    string LeadSelectedPracticesText,
    string EvaluatorPassScoreText,
    string EvaluatorSelectedPracticesText,
    string EvaluatorOffendingPracticesText,
    string? ErrorMessage = null)
{
    public static EvaluationPanelSnapshot Pending(string? outputDirectory = null, string? bundleDirectory = null)
    {
        return new EvaluationPanelSnapshot(
            State: EvaluationPanelState.Pending,
            BundleDirectory: string.IsNullOrWhiteSpace(bundleDirectory) ? "(pending)" : bundleDirectory,
            OutputDirectory: string.IsNullOrWhiteSpace(outputDirectory) ? "(pending)" : outputDirectory,
            LeadSelectedPracticesText: "(pending)",
            EvaluatorPassScoreText: "(pending)",
            EvaluatorSelectedPracticesText: "(pending)",
            EvaluatorOffendingPracticesText: "(pending)");
    }
}

public sealed class LiveCliDashboard
{
    private const string RunningStatusText = "[bold]🎵 take it easy, I will do the work...🎵[/]";

    private readonly object _stateLock = new();
    private readonly List<AgentStepEvent> _steps = new();
    private readonly string[] _spinnerFrames = Spinner.Known.Dots.Frames.ToArray();

    private string _modelSummary = "<unset-model>";
    private string _runId = "<unset-run-id>";
    private string _runDir = ".";
    private string _goal = "(not provided)";
    private string? _subtitle;
    private string _latestInfoMessage = "Waiting for updates...";
    private string? _errorMessage;
    private bool _showEvaluationPanel;
    private bool _showExecutionPanel;
    private EvaluationPanelSnapshot _evaluationSnapshot = EvaluationPanelSnapshot.Pending();
    private int _spinnerIndex;
    private DashboardStatus _status = DashboardStatus.Running;

    private sealed record DashboardSnapshot(
        string ModelSummary,
        string RunId,
        string RunDir,
        string Goal,
        string? Subtitle,
        string LatestInfoMessage,
        string? ErrorMessage,
        bool ShowEvaluationPanel,
        bool ShowExecutionPanel,
        EvaluationPanelSnapshot EvaluationSnapshot,
        int SpinnerIndex,
        DashboardStatus Status,
        AgentStepEvent[] Steps);

    public void Initialize(LiveCliDashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_stateLock)
        {
            _modelSummary = string.IsNullOrWhiteSpace(options.ModelSummary) ? "<unset-model>" : options.ModelSummary;
            _runId = string.IsNullOrWhiteSpace(options.RunId) ? "<unset-run-id>" : options.RunId;
            _runDir = string.IsNullOrWhiteSpace(options.RunDir) ? "." : options.RunDir;
            _goal = string.IsNullOrWhiteSpace(options.Goal) ? "(not provided)" : options.Goal.Trim();
            _subtitle = string.IsNullOrWhiteSpace(options.Subtitle) ? null : options.Subtitle.Trim();
            _latestInfoMessage = "Waiting for updates...";
            _errorMessage = null;
            _showEvaluationPanel = options.ShowEvaluationPanel;
            _showExecutionPanel = options.ShowExecutionPanel;
            _evaluationSnapshot = EvaluationPanelSnapshot.Pending(options.RunDir);
            _spinnerIndex = 0;
            _status = DashboardStatus.Running;
            _steps.Clear();
        }
    }

    public void SetLatestInfo(string message)
    {
        lock (_stateLock)
        {
            _latestInfoMessage = string.IsNullOrWhiteSpace(message)
                ? "Waiting for updates..."
                : message.Trim();
        }
    }

    public void AddStep(AgentStepEvent step)
    {
        ArgumentNullException.ThrowIfNull(step);

        lock (_stateLock)
        {
            _steps.Add(step);
        }
    }

    public void UpdateEvaluation(EvaluationPanelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_stateLock)
        {
            _evaluationSnapshot = snapshot;
        }
    }

    public void MarkSuccess()
    {
        lock (_stateLock)
        {
            _status = DashboardStatus.Success;
        }
    }

    public void MarkError(string errorMessage)
    {
        lock (_stateLock)
        {
            _status = DashboardStatus.Error;
            _errorMessage = string.IsNullOrWhiteSpace(errorMessage) ? "Unknown error." : errorMessage.Trim();
        }
    }

    public IRenderable BuildCurrentStatusPanel()
    {
        return BuildHeaderPanel(Snapshot());
    }

    public async Task RunWithLiveAsync(Func<Task> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            await executeAsync();
            return;
        }

        Task executionTask;
        try
        {
            executionTask = executeAsync();
        }
        catch (Exception ex)
        {
            executionTask = Task.FromException(ex);
        }

        await AnsiConsole
            .Live(BuildLayout())
            .AutoClear(true)
            .Overflow(VerticalOverflow.Visible)
            .StartAsync(async context =>
            {
                while (!executionTask.IsCompleted)
                {
                    AdvanceSpinner();
                    context.UpdateTarget(BuildLayout());
                    context.Refresh();
                    await Task.Delay(120);
                }

                context.UpdateTarget(BuildLayout());
                context.Refresh();
            });

        await executionTask;
        if (AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.Clear();
        }
        AnsiConsole.Write(BuildLayout());
    }

    public static IRenderable BuildPromptStatusPanel(string modelSummary)
    {
        var selectedModel = new Rows(
            new Markup("[bold]Selected Model[/]"),
            new Markup(Markup.Escape(modelSummary)));

        var runDetails = new Rows(
            new Markup("[bold]Run Id:[/] pending"),
            new Markup("[bold]Run Dir:[/] pending"));

        var status = new Markup("[grey]Info:[/] Prompting for goal.");

        var grid = new Grid();
        grid.Expand();
        grid.AddColumn(new GridColumn { Width = 40, NoWrap = true });
        grid.AddColumn(new GridColumn { Width = 24, NoWrap = true });
        grid.AddColumn(new GridColumn());
        grid.AddRow(selectedModel, runDetails, status);

        return new Panel(grid)
        {
            Header = new PanelHeader("Status", Justify.Left),
            Expand = true
        };
    }

    private void AdvanceSpinner()
    {
        lock (_stateLock)
        {
            if (_status != DashboardStatus.Running || _spinnerFrames.Length == 0)
            {
                return;
            }

            _spinnerIndex = (_spinnerIndex + 1) % _spinnerFrames.Length;
        }
    }

    private DashboardSnapshot Snapshot()
    {
        lock (_stateLock)
        {
            return new DashboardSnapshot(
                _modelSummary,
                _runId,
                _runDir,
                _goal,
                _subtitle,
                _latestInfoMessage,
                _errorMessage,
                _showEvaluationPanel,
                _showExecutionPanel,
                _evaluationSnapshot,
                _spinnerIndex,
                _status,
                _steps.ToArray());
        }
    }

    private IRenderable BuildLayout()
    {
        var snapshot = Snapshot();
        var rows = new List<IRenderable>
        {
            BuildBranding(snapshot),
            BuildHeaderPanel(snapshot),
            BuildGoalPanel(snapshot)
        };

        if (snapshot.ShowEvaluationPanel)
        {
            rows.Add(BuildEvaluationPanel(snapshot));
        }

        if (snapshot.ShowExecutionPanel)
        {
            rows.Add(BuildMainPanel(snapshot));
        }
        return new Rows(rows);
    }

    private IRenderable BuildHeaderPanel(DashboardSnapshot snapshot)
    {
        var statusText = snapshot.Status switch
        {
            DashboardStatus.Success => "[bold green]Agent finished successfully.[/]",
            DashboardStatus.Error => "[bold red]There was a problem.[/]",
            _ => $"[bold blue]{Markup.Escape(CurrentSpinnerFrame(snapshot.SpinnerIndex))}[/] {RunningStatusText}"
        };

        return BuildHeaderPanelCore(
            snapshot.ModelSummary,
            snapshot.RunId,
            snapshot.RunDir,
            statusText,
            snapshot.LatestInfoMessage,
            snapshot.ShowEvaluationPanel ? StripMarkup(StateMarkup(snapshot.EvaluationSnapshot.State)) : null);
    }

    private static IRenderable BuildBranding(DashboardSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Subtitle))
        {
            return new Rows(
                new FigletText("BENDOVER")
                {
                    Color = Color.Orange1,
                    Justification = Justify.Center
                },
                new Align(
                    new Markup($"[bold #ffb000]{Markup.Escape(snapshot.Subtitle)}[/]"),
                    HorizontalAlignment.Center));
        }

        return new FigletText("BENDOVER")
        {
            Color = Color.Orange1,
            Justification = Justify.Center
        };
    }

    private static IRenderable BuildHeaderPanelCore(
        string modelSummary,
        string runId,
        string runDir,
        string statusText,
        string latestInfoMessage,
        string? evaluationStateText)
    {
        var runDetails = new Rows(
            new Markup($"[bold]Selected Model:[/] {Markup.Escape(modelSummary)}"),
            new Markup($"[bold]Run Id:[/] {Markup.Escape(runId)}"),
            new Markup($"[bold]Run Dir:[/] {Markup.Escape(runDir)}"));

        var statusRows = new List<IRenderable>();
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            statusRows.Add(new Markup(statusText));
        }

        statusRows.Add(new Markup($"[grey]Info:[/] {Markup.Escape(latestInfoMessage)}"));
        if (!string.IsNullOrWhiteSpace(evaluationStateText))
        {
            statusRows.Add(new Markup($"[grey]Evaluation:[/] {Markup.Escape(evaluationStateText)}"));
        }
        var status = new Rows(statusRows);

        var grid = new Grid();
        grid.Expand();
        grid.AddColumn(new GridColumn());
        grid.AddColumn(new GridColumn());
        grid.AddRow(runDetails, status);

        return new Panel(grid)
        {
            Header = new PanelHeader("Status", Justify.Left),
            Expand = true
        };
    }

    private static IRenderable BuildGoalPanel(DashboardSnapshot snapshot)
    {
        return new Panel(new Markup(Markup.Escape(snapshot.Goal)))
        {
            Header = new PanelHeader("Goal", Justify.Left),
            Expand = true
        };
    }

    private static IRenderable BuildEvaluationPanel(DashboardSnapshot snapshot)
    {
        var evaluation = snapshot.EvaluationSnapshot;
        var grid = new Grid();
        grid.Expand();
        grid.AddColumn(new GridColumn { Width = 20, NoWrap = true });
        grid.AddColumn(new GridColumn());
        grid.AddRow(new Markup("[bold]State:[/]"), new Markup(StateMarkup(evaluation.State)));
        grid.AddRow(new Markup("[bold]Bundle:[/]"), BuildPathValue(evaluation.BundleDirectory));
        grid.AddRow(new Markup("[bold]Output:[/]"), BuildPathValue(evaluation.OutputDirectory));
        grid.AddRow(new Markup("[bold]Selected Practices:[/]"), new Markup(Markup.Escape(DisplayOrDefault(evaluation.LeadSelectedPracticesText, "(pending)"))));
        grid.AddRow(new Markup("[bold]Evaluator Selected:[/]"), new Markup(Markup.Escape(DisplayOrDefault(evaluation.EvaluatorSelectedPracticesText, "(pending)"))));
        grid.AddRow(new Markup("[bold]Evaluator Offending:[/]"), new Markup(Markup.Escape(DisplayOrDefault(evaluation.EvaluatorOffendingPracticesText, "(pending)"))));
        grid.AddRow(new Markup("[bold]Pass / Score:[/]"), new Markup(Markup.Escape(DisplayOrDefault(evaluation.EvaluatorPassScoreText, "(pending)"))));

        if (!string.IsNullOrWhiteSpace(evaluation.ErrorMessage))
        {
            grid.AddRow(new Markup("[bold red]Error:[/]"), new Markup(Markup.Escape(evaluation.ErrorMessage)));
        }

        return new Panel(grid)
        {
            Header = new PanelHeader("Evaluation", Justify.Left),
            Expand = true
        };
    }

    private static IRenderable BuildPathValue(string? path)
    {
        var displayPath = DisplayOrDefault(path, "(pending)");
        if (displayPath == "(pending)")
        {
            return new Markup("(pending)");
        }

        var absolutePath = Path.IsPathRooted(displayPath)
            ? displayPath
            : Path.GetFullPath(displayPath);
        return new Markup(Markup.Escape(absolutePath));
    }

    private static string StateMarkup(EvaluationPanelState state)
    {
        var text = state switch
        {
            EvaluationPanelState.Completed => "[green]Completed[/]",
            EvaluationPanelState.Running => "[blue]Running[/]",
            EvaluationPanelState.Missing => "[yellow]Missing[/]",
            EvaluationPanelState.ParseError => "[red]ParseError[/]",
            EvaluationPanelState.Failed => "[red]Failed[/]",
            _ => "[grey]Pending[/]"
        };

        return text;
    }

    private static string StripMarkup(string value)
    {
        return Markup.Remove(value);
    }

    private static string DisplayOrDefault(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static IRenderable BuildMainPanel(DashboardSnapshot snapshot)
    {
        var rows = new List<IRenderable>();

        if (snapshot.Steps.Length == 0)
        {
            rows.Add(new Markup("[grey]Waiting for execution steps...[/]"));
        }
        else
        {
            for (var index = snapshot.Steps.Length - 1; index >= 0; index--)
            {
                if (index < snapshot.Steps.Length - 1)
                {
                    rows.Add(new Rule());
                }

                rows.Add(BuildStepPanel(snapshot.Steps[index]));
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            if (rows.Count > 0)
            {
                rows.Add(new Rule { Style = new Style(Color.Red) });
            }

            rows.Add(new Panel(new Markup(Markup.Escape(snapshot.ErrorMessage)))
            {
                Header = new PanelHeader("Error", Justify.Left),
                BorderStyle = new Style(Color.Red),
                Expand = true
            });
        }

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader("Execution", Justify.Left),
            Expand = true
        };
    }

    private static IRenderable BuildStepPanel(AgentStepEvent step)
    {
        var plan = string.IsNullOrWhiteSpace(step.Plan) ? "(not provided)" : step.Plan;
        var tool = string.IsNullOrWhiteSpace(step.Tool) ? "(unknown)" : Truncate(step.Tool, 1000);
        var observation = string.IsNullOrWhiteSpace(step.Observation) ? "(none)" : step.Observation;

        return new Rows(
            new Markup($"[bold]Step #{step.StepNumber}[/]"),
            new Markup($"[#ff8800]Plan:[/] {Markup.Escape(plan)}"),
            new Markup($"[yellow]Tool:[/] {Markup.Escape(tool)}"),
            new Markup($"[green]Observation:[/] {Markup.Escape(observation)}"));
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..maxLength]}...";
    }

    private string CurrentSpinnerFrame(int spinnerIndex)
    {
        if (_spinnerFrames.Length == 0)
        {
            return ".";
        }

        var normalizedIndex = Math.Abs(spinnerIndex % _spinnerFrames.Length);
        return _spinnerFrames[normalizedIndex];
    }
}

public sealed class LiveCliDashboardObserver : IAgentObserver
{
    private readonly LiveCliDashboard _dashboard;

    public LiveCliDashboardObserver(LiveCliDashboard dashboard)
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

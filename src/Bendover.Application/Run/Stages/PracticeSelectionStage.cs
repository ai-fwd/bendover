using System.Text.Json;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application.Run.Stages;

public sealed class PracticeSelectionStage : RunStage
{
    private readonly ILeadAgent _leadAgent;
    private readonly IPromptOptRunRecorder _runRecorder;

    public PracticeSelectionStage(
        ILeadAgent leadAgent,
        IPromptOptRunRecorder runRecorder)
    {
        _leadAgent = leadAgent;
        _runRecorder = runRecorder;
    }

    public override int SetupOrder => 40;

    public override async Task ExecuteAsync(RunStageContext context)
    {
        await context.Events.ProgressAsync("Lead Agent Analyzing Request...");

        var allPractices = context.Practices.ToList();
        var availablePracticeNames = new HashSet<string>(
            allPractices.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);
        var selectedPracticeNames = (await _leadAgent.AnalyzeTaskAsync(context.InitialGoal, allPractices, context.AgentsPath)).ToArray();

        var leadMessages = new List<ChatMessage> { new(ChatRole.User, $"Goal: {context.InitialGoal}") };
        await _runRecorder.RecordPromptAsync("lead", leadMessages);
        await _runRecorder.RecordOutputAsync("lead", JsonSerializer.Serialize(selectedPracticeNames));

        var unknownSelectedPractices = selectedPracticeNames
            .Where(name => !availablePracticeNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (selectedPracticeNames.Length == 0)
        {
            var availableCsv = string.Join(", ", availablePracticeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException($"Lead selected no practices. Available practices: [{availableCsv}]");
        }

        if (unknownSelectedPractices.Length > 0)
        {
            var unknownCsv = string.Join(", ", unknownSelectedPractices);
            var availableCsv = string.Join(", ", availablePracticeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException($"Lead selected unknown practices: [{unknownCsv}]. Available practices: [{availableCsv}]");
        }

        var selectedNameSet = new HashSet<string>(selectedPracticeNames, StringComparer.OrdinalIgnoreCase);
        context.SelectedPracticeNames = selectedPracticeNames;
        context.SelectedPractices = allPractices.Where(p => selectedNameSet.Contains(p.Name)).ToList();
        context.PracticesContext = string.Join(
            "\n",
            context.SelectedPractices.Select(p => $"- [{p.Name}] ({p.AreaOfConcern}): {p.Content}"));
        await context.TranscriptWriter.WriteSelectedPracticesAsync(selectedPracticeNames);

        var selectedCsv = string.Join(", ", selectedPracticeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        await context.Events.ProgressAsync($"Selected practices: {selectedCsv}");
    }
}

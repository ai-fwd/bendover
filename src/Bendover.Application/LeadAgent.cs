using System.Text.Json;
using System.Text.RegularExpressions;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application;

public class LeadAgent : ILeadAgent
{
    private readonly IChatClientResolver _clientResolver;
    private readonly IAgentPromptService _agentPromptService;

    public LeadAgent(
        IChatClientResolver clientResolver,
        IAgentPromptService agentPromptService)
    {
        _clientResolver = clientResolver;
        _agentPromptService = agentPromptService;
    }

    public async Task<IEnumerable<string>> AnalyzeTaskAsync(string userPrompt, IReadOnlyCollection<Practice> practices)
    {
        var allPractices = practices ?? Array.Empty<Practice>();
        var systemPrompt = _agentPromptService.LoadLeadPromptTemplate();

        var practicesList = string.Join("\n", allPractices
            .Where(p => !p.Name.Equals("lead_agent_practice", StringComparison.OrdinalIgnoreCase))
            .Select(p => $"- Name: {p.Name}, Role: {p.TargetRole}, Area: {p.AreaOfConcern}"));

        // 3. Construct Context
        var fullSystemPrompt = $"{systemPrompt}\n\nAvailable Practices:\n{practicesList}";

        // 4. Call LLM
        var client = _clientResolver.GetClient(AgentRole.Lead);
        var response = await client.CompleteAsync(new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, fullSystemPrompt),
            new ChatMessage(ChatRole.User, userPrompt)
        });

        var responseText = response.Message.Text;

        // 5. Parse JSON
        return ParsePractices(responseText);
    }

    private IEnumerable<string> ParsePractices(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return new List<string>();
        }

        try
        {
            // Attempt to extract JSON from code blocks if present
            var match = Regex.Match(responseText, @"```(?:json)?\s*(\[.*?\])\s*```", RegexOptions.Singleline);
            var json = match.Success ? match.Groups[1].Value : responseText;

            // Clean up any extra whitespace or specific chars
            json = json.Trim();
            // Basic heuristic to find the array start/end if regex failed but still mixed content
            int start = json.IndexOf('[');
            int end = json.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                json = json.Substring(start, end - start + 1);
            }

            var names = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return names
                .Select(x => x?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Lead Agent response: {ex.Message}. Response: {responseText}");
            return new List<string>();
        }
    }
}

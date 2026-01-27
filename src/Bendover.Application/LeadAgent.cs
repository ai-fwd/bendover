using System.Text.Json;
using System.Text.RegularExpressions;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application;

public class LeadAgent : ILeadAgent
{
    private readonly IChatClientResolver _clientResolver;
    private readonly IPracticeService _practiceService;

    public LeadAgent(IChatClientResolver clientResolver, IPracticeService practiceService)
    {
        _clientResolver = clientResolver;
        _practiceService = practiceService;
    }

    public async Task<IEnumerable<string>> AnalyzeTaskAsync(string userPrompt)
    {
        // 1. Load System Prompt from Practices
        var leadPractices = await _practiceService.GetPracticesForRoleAsync(AgentRole.Lead);
        var leadPractice = leadPractices.FirstOrDefault(p => p.Name == "lead_agent_practice");

        string systemPrompt;
        if (leadPractice != null)
        {
            systemPrompt = leadPractice.Content;
        }
        else
        {
            // Fallback
            systemPrompt = "You are the Lead Agent. Select relevant practice names as JSON array.";
        }

        // 2. Load Practices Metadata (Excluding the lead practice itself from the selection pool if desired, or keep it)
        // Usually, the lead agent doesn't select itself, but it needs to know about others.
        var allPractices = await _practiceService.GetPracticesAsync();
        var practicesList = string.Join("\n", allPractices
            .Where(p => p.Name != "lead_agent_practice")
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

            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Lead Agent response: {ex.Message}. Response: {responseText}");
            return new List<string>();
        }
    }
}

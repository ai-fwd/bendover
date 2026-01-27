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
    private readonly string _systemPromptPath;

    public LeadAgent(IChatClientResolver clientResolver, IPracticeService practiceService,
                     string? systemPromptPath = null)
    {
        _clientResolver = clientResolver;
        _practiceService = practiceService;
        _systemPromptPath = systemPromptPath ?? BendoverPaths.GetSystemPromptPath("lead");
    }

    public async Task<IEnumerable<string>> AnalyzeTaskAsync(string userPrompt)
    {
        // 1. Load System Prompt
        string systemPrompt;
        if (File.Exists(_systemPromptPath))
        {
            systemPrompt = await File.ReadAllTextAsync(_systemPromptPath);
        }
        else
        {
            // Fallback or Error? 
            // For now, minimal fallback if file missing for some reason
            systemPrompt = "You are the Lead Agent. Select relevant practice names as JSON array.";
        }

        // 2. Load Practices Metadata
        var allPractices = await _practiceService.GetPracticesAsync();
        var practicesList = string.Join("\n", allPractices.Select(p => $"- Name: {p.Name}, Role: {p.TargetRole}, Area: {p.AreaOfConcern}"));

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

    private IEnumerable<string> ParsePractices(string responseText)
    {
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

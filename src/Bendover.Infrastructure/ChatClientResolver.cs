using System.ClientModel;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Bendover.Infrastructure;

public class ChatClientResolver : IChatClientResolver
{
    private readonly AgentOptions _options;

    public ChatClientResolver(IOptions<AgentOptions> options)
    {
        _options = options.Value;
    }

    public IChatClient GetClient(AgentRole role)
    {
        var options = _options;
        if (_options.RoleOverrides != null && _options.RoleOverrides.TryGetValue(role, out var overrideOptions))
        {
            // Simple merge: if override has value, use it, else inherit.
            // But for simplicity, we might just assume overrides are complete or fall back.
            // Let's do a fallback merge.
            options = MergeOptions(_options, overrideOptions);
        }

        // Validate options
        if (string.IsNullOrEmpty(options.Endpoint) || string.IsNullOrEmpty(options.Model))
        {
            // Throwing exception or returning a dummy client?
            // User wants "Local vs Remote". Support OpenAI API.
            throw new InvalidOperationException($"Configuration missing for role {role}. Model: {options.Model}, Endpoint: {options.Endpoint}");
        }

        // Create OpenAI Client
        // Note: Microsoft.Extensions.AI.OpenAI uses Azure.AI.OpenAI or OpenAI library.
        // We will use the OpenAI library directly to create the client, then wrap it.
        // Or better, use the extensions directly if possible.

        // The package Microsoft.Extensions.AI.OpenAI provides `OpenAIChatClient`.
        // It takes an `OpenAIClient` or `ChatClient` from OpenAI SDK.

        var openAIClientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.Endpoint)
        };

        // If config has no API key, use "dummy" if local? 
        // Local LLMs often need any non-empty string as key.
        if (string.IsNullOrEmpty(options.ApiKey))
        {
            throw new InvalidOperationException($"ApiKey is missing for role {role}. Please check your .env file.");
        }
        var apiKey = options.ApiKey;

        var openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), openAIClientOptions);
        var chatClient = new OpenAIChatClient(openAIClient, options.Model);

        return chatClient;
    }

    private AgentOptions MergeOptions(AgentOptions baseOpts, AgentOptions overrideOpts)
    {
        return new AgentOptions
        {
            Model = !string.IsNullOrEmpty(overrideOpts.Model) ? overrideOpts.Model : baseOpts.Model,
            Endpoint = !string.IsNullOrEmpty(overrideOpts.Endpoint) ? overrideOpts.Endpoint : baseOpts.Endpoint,
            ApiKey = !string.IsNullOrEmpty(overrideOpts.ApiKey) ? overrideOpts.ApiKey : baseOpts.ApiKey
        };
    }
}

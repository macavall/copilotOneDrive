using System.ClientModel;
using System.Text.Json;
using Agent.Core;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Agent.Worker;

/// <summary>
/// Answers free-form questions with Azure OpenAI. The producer sends <c>type: "llm"</c> with the
/// question in <c>prompt</c>. An optional <c>system</c> extra field overrides the default system
/// prompt. Returns a compact JSON payload with the model's answer.
/// </summary>
public sealed class LlmTaskHandler : ITaskHandler
{
    private static readonly JsonSerializerOptions ResultJson = new() { WriteIndented = false };

    private readonly LlmOptions _options;
    private readonly ILogger<LlmTaskHandler> _logger;

    public LlmTaskHandler(IOptions<LlmOptions> options, ILogger<LlmTaskHandler> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool CanHandle(string type) =>
        string.Equals(type, "llm", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "chat", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "ask", StringComparison.OrdinalIgnoreCase);

    public async Task<string> HandleAsync(TaskMessage message, CancellationToken ct)
    {
        var question = message.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(question))
            throw new InvalidOperationException("The 'prompt' field must contain a question.");

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new InvalidOperationException("No Azure OpenAI endpoint configured (set Llm:Endpoint).");
        if (string.IsNullOrWhiteSpace(_options.Deployment))
            throw new InvalidOperationException("No Azure OpenAI deployment configured (set Llm:Deployment).");

        _logger.LogInformation("Answering LLM question for task {Id}", message.Id);

        var client = BuildClient();
        var chat = client.GetChatClient(_options.Deployment);

        var system = GetExtra(message, "system") ?? _options.SystemPrompt;
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(system),
            new UserChatMessage(question)
        };

        var chatOptions = new ChatCompletionOptions
        {
            MaxOutputTokenCount = _options.MaxOutputTokens,
            Temperature = _options.Temperature
        };

        var completion = await chat.CompleteChatAsync(messages, chatOptions, ct).ConfigureAwait(false);
        var answer = string.Concat(completion.Value.Content.Select(p => p.Text));

        return JsonSerializer.Serialize(new
        {
            deployment = _options.Deployment,
            question,
            answer,
            finishReason = completion.Value.FinishReason.ToString()
        }, ResultJson);
    }

    private AzureOpenAIClient BuildClient()
    {
        var endpoint = new Uri(_options.Endpoint!);
        return _options.AuthMode?.ToLowerInvariant() switch
        {
            "apikey" => new AzureOpenAIClient(endpoint,
                new ApiKeyCredential(_options.ApiKey
                    ?? throw new InvalidOperationException("Llm:ApiKey is required when AuthMode is 'ApiKey'."))),
            "default" => new AzureOpenAIClient(endpoint, new DefaultAzureCredential()),
            _ => new AzureOpenAIClient(endpoint, new AzureCliCredential()),
        };
    }

    private static string? GetExtra(TaskMessage message, string key)
    {
        if (message.Extra is null) return null;
        if (!message.Extra.TryGetValue(key, out var value) || value is null) return null;
        return value is JsonElement { ValueKind: JsonValueKind.String } el
            ? el.GetString()
            : value.ToString();
    }
}

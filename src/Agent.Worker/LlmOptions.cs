namespace Agent.Worker;

/// <summary>Configuration for the LLM task handler (Azure OpenAI).</summary>
public sealed class LlmOptions
{
    /// <summary>Azure OpenAI endpoint, e.g. https://my-resource.openai.azure.com.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Deployment (model) name to call.</summary>
    public string? Deployment { get; set; }

    /// <summary>Auth strategy: "AzureCli" (default), "Default" (DefaultAzureCredential), or "ApiKey".</summary>
    public string AuthMode { get; set; } = "AzureCli";

    /// <summary>API key, only used when AuthMode is "ApiKey".</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional system prompt prepended to every request.</summary>
    public string SystemPrompt { get; set; } =
        "You are a concise assistant answering questions relayed from a phone. Keep answers short.";

    public int MaxOutputTokens { get; set; } = 800;

    public float Temperature { get; set; } = 0.3f;
}

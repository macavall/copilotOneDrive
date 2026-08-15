namespace Agent.Worker;

/// <summary>Configuration for the Kusto task handler.</summary>
public sealed class KustoOptions
{
    /// <summary>Fallback cluster used when a request omits one. Short name (e.g. "wawscus") or full URI.</summary>
    public string? DefaultCluster { get; set; }

    /// <summary>Fallback database used when a request omits one (e.g. "wawsprod").</summary>
    public string? DefaultDatabase { get; set; }

    /// <summary>Auth strategy: "AzureCli" (default), "UserPrompt", or "Default" (DefaultAzureCredential).</summary>
    public string AuthMode { get; set; } = "AzureCli";

    /// <summary>Maximum number of rows returned per query; extra rows are truncated.</summary>
    public int MaxRows { get; set; } = 200;

    /// <summary>Server-side query timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(4);
}

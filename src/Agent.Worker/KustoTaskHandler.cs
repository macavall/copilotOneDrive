using System.Data;
using System.Text.Json;
using Agent.Core;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Microsoft.Extensions.Options;

namespace Agent.Worker;

/// <summary>
/// Runs Kusto (KQL) queries submitted through the shared folder. The producer (Copilot on any
/// device) writes a request with <c>type: "kusto"</c>, the query text in <c>prompt</c>, and
/// optional <c>cluster</c>/<c>database</c> in the message's extra fields. The result is written
/// back to the outbox as a compact JSON payload (columns + rows).
/// </summary>
public sealed class KustoTaskHandler : ITaskHandler
{
    private static readonly JsonSerializerOptions ResultJson = new() { WriteIndented = false };

    private readonly KustoOptions _options;
    private readonly ILogger<KustoTaskHandler> _logger;

    public KustoTaskHandler(IOptions<KustoOptions> options, ILogger<KustoTaskHandler> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool CanHandle(string type) =>
        string.Equals(type, "kusto", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "kql", StringComparison.OrdinalIgnoreCase);

    public async Task<string> HandleAsync(TaskMessage message, CancellationToken ct)
    {
        var query = message.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("The 'prompt' field must contain a KQL query.");

        var cluster = NormalizeCluster(GetExtra(message, "cluster") ?? _options.DefaultCluster)
            ?? throw new InvalidOperationException("No Kusto cluster specified (set 'cluster' in the request or DefaultCluster in config).");
        var database = GetExtra(message, "database") ?? _options.DefaultDatabase
            ?? throw new InvalidOperationException("No Kusto database specified (set 'database' in the request or DefaultDatabase in config).");

        _logger.LogInformation("Running KQL on {Cluster}/{Database} for task {Id}", cluster, database, message.Id);

        var kcsb = BuildConnection(cluster, database);
        using var provider = KustoClientFactory.CreateCslQueryProvider(kcsb);

        var crp = new ClientRequestProperties
        {
            ClientRequestId = $"CopilotBus;{message.Id}"
        };
        crp.SetOption(ClientRequestProperties.OptionServerTimeout, _options.Timeout);

        using var reader = await provider.ExecuteQueryAsync(database, query, crp).ConfigureAwait(false);
        return Serialize(cluster, database, query, reader);
    }

    private KustoConnectionStringBuilder BuildConnection(string cluster, string database)
    {
        var kcsb = new KustoConnectionStringBuilder(cluster, database);
        return _options.AuthMode?.ToLowerInvariant() switch
        {
            "userprompt" => kcsb.WithAadUserPromptAuthentication(),
            "default" => kcsb.WithAadAzureTokenCredentialsAuthentication(
                new Azure.Identity.DefaultAzureCredential()),
            _ => kcsb.WithAadAzCliAuthentication(),
        };
    }

    private string Serialize(string cluster, string database, string query, IDataReader reader)
    {
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            columns[i] = reader.GetName(i);

        var rows = new List<object?[]>();
        var truncated = false;
        while (reader.Read())
        {
            if (rows.Count >= _options.MaxRows)
            {
                truncated = true;
                break;
            }
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[i] = value is DBNull ? null : value;
            }
            rows.Add(row);
        }

        return JsonSerializer.Serialize(new
        {
            cluster,
            database,
            query,
            columns,
            rowCount = rows.Count,
            truncated,
            rows
        }, ResultJson);
    }

    private static string? GetExtra(TaskMessage message, string key)
    {
        if (message.Extra is null) return null;
        if (!message.Extra.TryGetValue(key, out var value) || value is null) return null;
        return value is JsonElement { ValueKind: JsonValueKind.String } el
            ? el.GetString()
            : value.ToString();
    }

    private static string? NormalizeCluster(string? cluster)
    {
        if (string.IsNullOrWhiteSpace(cluster)) return null;
        cluster = cluster.Trim();
        if (!cluster.Contains("://", StringComparison.Ordinal))
        {
            if (!cluster.Contains('.', StringComparison.Ordinal))
                cluster = $"{cluster}.kusto.windows.net";
            cluster = $"https://{cluster}";
        }
        return cluster;
    }
}

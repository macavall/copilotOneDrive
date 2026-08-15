using System.ComponentModel;
using System.Text.Json;
using Agent.Core;
using ModelContextProtocol.Server;

namespace Agent.McpServer;

/// <summary>MCP tools that let Copilot exchange work with the shared-folder worker agent.</summary>
[McpServerToolType]
public sealed class SharedFolderTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly FileBus _bus;

    public SharedFolderTools(FileBus bus) => _bus = bus;

    [McpServerTool(Name = "submit_task")]
    [Description("Publish a task to the shared inbox for the worker agent to process. Returns the task id.")]
    public async Task<string> SubmitTask(
        [Description("The work request / prompt for the worker.")] string prompt,
        [Description("Task type routed to a matching handler. Defaults to 'echo'.")] string type = "echo",
        CancellationToken ct = default)
    {
        _bus.EnsureDirectories();
        var message = new TaskMessage
        {
            Type = type,
            Prompt = prompt,
            Origin = "mcp:copilot"
        };
        await _bus.WriteRequestAsync(message, ct);
        return message.Id;
    }

    [McpServerTool(Name = "submit_kusto")]
    [Description("Publish a Kusto (KQL) query for the worker to run. Returns the task id; read it with get_result/await_result.")]
    public async Task<string> SubmitKusto(
        [Description("The KQL query text.")] string query,
        [Description("Kusto cluster: short name (e.g. 'wawscus') or full URI. Omit to use the worker's default.")] string? cluster = null,
        [Description("Kusto database (e.g. 'wawsprod'). Omit to use the worker's default.")] string? database = null,
        CancellationToken ct = default)
    {
        _bus.EnsureDirectories();
        var extra = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(cluster)) extra["cluster"] = cluster;
        if (!string.IsNullOrWhiteSpace(database)) extra["database"] = database;

        var message = new TaskMessage
        {
            Type = "kusto",
            Prompt = query,
            Origin = "mcp:copilot",
            Extra = extra.Count > 0 ? extra : null
        };
        await _bus.WriteRequestAsync(message, ct);
        return message.Id;
    }
    [Description("Read the current result for a task id. Returns status 'pending' if not yet completed.")]
    public async Task<string> GetResult(
        [Description("The task id returned by submit_task.")] string id,
        CancellationToken ct = default)
    {
        var result = await _bus.TryReadResultAsync(id, ct);
        return result is null
            ? JsonSerializer.Serialize(new { id, status = "pending" }, JsonOptions)
            : JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "await_result")]
    [Description("Submit-and-wait helper: poll the outbox until the task completes or the timeout elapses.")]
    public async Task<string> AwaitResult(
        [Description("The task id returned by submit_task.")] string id,
        [Description("Maximum seconds to wait. Default 30.")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await _bus.TryReadResultAsync(id, ct);
            if (result is { Status: MessageStatus.Done or MessageStatus.Failed })
                return JsonSerializer.Serialize(result, JsonOptions);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        return JsonSerializer.Serialize(new { id, status = "timeout" }, JsonOptions);
    }

    [McpServerTool(Name = "list_pending")]
    [Description("List task ids currently waiting in the inbox (not yet processed by the worker).")]
    public string ListPending()
    {
        var ids = _bus.EnumeratePending()
            .Select(Path.GetFileNameWithoutExtension)
            .ToArray();
        return JsonSerializer.Serialize(new { count = ids.Length, files = ids }, JsonOptions);
    }
}

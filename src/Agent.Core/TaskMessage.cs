using System.Text.Json.Serialization;

namespace Agent.Core;

/// <summary>A single unit of work passed between Copilot (producer) and the worker agent (consumer).</summary>
public sealed class TaskMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>Logical task kind, e.g. "echo", "prompt", "shell".</summary>
    public string Type { get; set; } = "echo";

    public MessageStatus Status { get; set; } = MessageStatus.Pending;

    /// <summary>Free-form request payload from the producer.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Result payload written back by the consumer.</summary>
    public string? Result { get; set; }

    /// <summary>Error detail when <see cref="Status"/> is <see cref="MessageStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Identity of the process that produced the message.</summary>
    public string? Origin { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object>? Extra { get; set; }
}

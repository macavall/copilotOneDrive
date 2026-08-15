using Agent.Core;

namespace Agent.Worker;

/// <summary>Default handler: echoes the prompt back. Replace or add handlers for real work.</summary>
public sealed class EchoTaskHandler : ITaskHandler
{
    public bool CanHandle(string type) =>
        string.Equals(type, "echo", StringComparison.OrdinalIgnoreCase);

    public Task<string> HandleAsync(TaskMessage message, CancellationToken ct) =>
        Task.FromResult($"echo: {message.Prompt}");
}

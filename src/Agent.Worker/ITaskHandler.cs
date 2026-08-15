using Agent.Core;

namespace Agent.Worker;

/// <summary>Processes a claimed <see cref="TaskMessage"/> and returns the result payload.</summary>
public interface ITaskHandler
{
    /// <summary>Task <see cref="TaskMessage.Type"/> values this handler can process.</summary>
    bool CanHandle(string type);

    Task<string> HandleAsync(TaskMessage message, CancellationToken ct);
}

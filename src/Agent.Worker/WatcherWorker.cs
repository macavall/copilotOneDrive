using Agent.Core;
using Microsoft.Extensions.Options;

namespace Agent.Worker;

public sealed class WorkerOptions
{
    /// <summary>Fallback sweep interval; catches files whose FS/OneDrive events were missed.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Types handled by another consumer (the VS Code extension); the worker leaves them alone.</summary>
    public string[] ExternalTypes { get; set; } = new[] { "llm", "chat", "ask" };
}

/// <summary>
/// Watches the shared inbox, claims each request, dispatches it to a matching handler, and writes
/// the result to the outbox. Uses a <see cref="FileSystemWatcher"/> for low latency plus a periodic
/// sweep as a safety net for events dropped by cloud-sync clients.
/// </summary>
public sealed class WatcherWorker : BackgroundService
{
    private readonly FileBus _bus;
    private readonly IEnumerable<ITaskHandler> _handlers;
    private readonly WorkerOptions _options;
    private readonly ILogger<WatcherWorker> _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);

    public WatcherWorker(
        FileBus bus,
        IEnumerable<ITaskHandler> handlers,
        IOptions<WorkerOptions> options,
        ILogger<WatcherWorker> logger)
    {
        _bus = bus;
        _handlers = handlers;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _bus.EnsureDirectories();
        _logger.LogInformation("Watching inbox at {Inbox}", _bus.InboxPath);

        using var watcher = new FileSystemWatcher(_bus.InboxPath, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => Nudge();
        watcher.Changed += (_, _) => Nudge();

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessPendingAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(_options.PollInterval);
                await _signal.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Poll interval elapsed with no event; fall through to next sweep.
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Nudge()
    {
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        foreach (var file in _bus.EnumeratePending())
        {
            if (ct.IsCancellationRequested) return;
            if (!_bus.TryClaim(file)) continue;

            try
            {
                var message = await _bus.ReadMessageAsync(file, ct).ConfigureAwait(false);
                if (message is null)
                {
                    _bus.ReleaseClaim(file);
                    continue;
                }

                var hasHandler = _handlers.Any(h => h.CanHandle(message.Type));
                if (!hasHandler && _options.ExternalTypes.Any(t =>
                        string.Equals(t, message.Type, StringComparison.OrdinalIgnoreCase)))
                {
                    _bus.ReleaseClaim(file); // handled by the VS Code extension
                    continue;
                }

                await HandleAsync(message, file, ct).ConfigureAwait(false);
            }
            finally
            {
                _bus.ReleaseClaim(file);
            }
        }
    }

    private async Task HandleAsync(TaskMessage message, string file, CancellationToken ct)
    {
        var handler = _handlers.FirstOrDefault(h => h.CanHandle(message.Type));
        try
        {
            if (handler is null)
            {
                message.Status = MessageStatus.Failed;
                message.Error = $"No handler registered for type '{message.Type}'.";
                _logger.LogWarning("No handler for task {Id} (type {Type})", message.Id, message.Type);
            }
            else
            {
                message.Status = MessageStatus.InProgress;
                message.Result = await handler.HandleAsync(message, ct).ConfigureAwait(false);
                message.Status = MessageStatus.Done;
                _logger.LogInformation("Completed task {Id} (type {Type})", message.Id, message.Type);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message.Status = MessageStatus.Failed;
            message.Error = ex.Message;
            _logger.LogError(ex, "Task {Id} failed", message.Id);
        }

        message.CompletedAt = DateTimeOffset.UtcNow;
        message.Origin = $"worker:{Environment.MachineName}";
        await _bus.WriteResponseAsync(message, ct).ConfigureAwait(false);
        _bus.MoveToProcessed(file);
    }

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }
}

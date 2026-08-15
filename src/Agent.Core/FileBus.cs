using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Core;

/// <summary>
/// File-based message bus over a shared directory. Producers write requests to <c>inbox/</c>;
/// the consumer claims them via <c>lock/</c>, writes results to <c>outbox/</c>, and archives the
/// original to <c>processed/</c>. All writes are atomic (temp file + rename) so a watcher never
/// observes a partially written or partially synced file.
/// </summary>
public sealed class FileBus
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly FileBusOptions _options;

    public FileBus(FileBusOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootDirectory))
            throw new ArgumentException("RootDirectory must be set.", nameof(options));
        _options = options;
    }

    public string InboxPath => Path.Combine(_options.RootDirectory, _options.InboxFolder);
    public string OutboxPath => Path.Combine(_options.RootDirectory, _options.OutboxFolder);
    public string ProcessedPath => Path.Combine(_options.RootDirectory, _options.ProcessedFolder);
    public string LockPath => Path.Combine(_options.RootDirectory, _options.LockFolder);

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(InboxPath);
        Directory.CreateDirectory(OutboxPath);
        Directory.CreateDirectory(ProcessedPath);
        Directory.CreateDirectory(LockPath);
    }

    /// <summary>Producer: publish a request into the inbox.</summary>
    public async Task<string> WriteRequestAsync(TaskMessage message, CancellationToken ct = default)
    {
        var fileName = $"{FileStamp(message)}.json";
        var dest = Path.Combine(InboxPath, fileName);
        await WriteAtomicAsync(dest, message, ct).ConfigureAwait(false);
        return dest;
    }

    /// <summary>Consumer: publish a result into the outbox.</summary>
    public async Task<string> WriteResponseAsync(TaskMessage message, CancellationToken ct = default)
    {
        var dest = Path.Combine(OutboxPath, $"{message.Id}.json");
        await WriteAtomicAsync(dest, message, ct).ConfigureAwait(false);
        return dest;
    }

    public IEnumerable<string> EnumeratePending() =>
        Directory.Exists(InboxPath)
            ? Directory.EnumerateFiles(InboxPath, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    public async Task<TaskMessage?> ReadMessageAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<TaskMessage>(stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // File may still be syncing/locked; caller retries on next cycle.
            return null;
        }
    }

    /// <summary>Consumer: attempt to claim exclusive ownership of an inbox file.</summary>
    public bool TryClaim(string inboxFile)
    {
        var lockFile = Path.Combine(LockPath, Path.GetFileName(inboxFile) + ".lock");
        try
        {
            using var fs = new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(fs);
            writer.Write($"{Environment.MachineName}:{Environment.ProcessId}:{DateTimeOffset.UtcNow:O}");
            return true;
        }
        catch (IOException)
        {
            if (IsStaleLock(lockFile))
            {
                TryDelete(lockFile);
                return TryClaim(inboxFile);
            }
            return false;
        }
    }

    public void ReleaseClaim(string inboxFile) =>
        TryDelete(Path.Combine(LockPath, Path.GetFileName(inboxFile) + ".lock"));

    /// <summary>Consumer: archive a handled request so it is never processed twice.</summary>
    public void MoveToProcessed(string inboxFile)
    {
        var dest = Path.Combine(ProcessedPath, Path.GetFileName(inboxFile));
        if (File.Exists(dest)) dest = Path.Combine(ProcessedPath, $"{Path.GetFileNameWithoutExtension(inboxFile)}-{Guid.NewGuid():n}.json");
        File.Move(inboxFile, dest);
    }

    /// <summary>Producer: read a result once the consumer has written it.</summary>
    public async Task<TaskMessage?> TryReadResultAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(OutboxPath, $"{id}.json");
        return File.Exists(path) ? await ReadMessageAsync(path, ct).ConfigureAwait(false) : null;
    }

    private async Task WriteAtomicAsync(string dest, TaskMessage message, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Guid.NewGuid():n}.tmp");
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, message, JsonOptions, ct).ConfigureAwait(false);
        }
        File.Move(temp, dest, overwrite: true);
    }

    private bool IsStaleLock(string lockFile)
    {
        try
        {
            return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(lockFile) > _options.LockTimeout;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
    }

    private static string FileStamp(TaskMessage message) =>
        $"{message.CreatedAt.UtcDateTime:yyyyMMddTHHmmssfff}-{message.Id}";
}

namespace Agent.Core;

/// <summary>Configuration for the shared-folder message bus.</summary>
public sealed class FileBusOptions
{
    /// <summary>Root shared directory (e.g. a OneDrive-synced folder) used as the message bus.</summary>
    public string RootDirectory { get; set; } = string.Empty;

    public string InboxFolder { get; set; } = "inbox";
    public string OutboxFolder { get; set; } = "outbox";
    public string ProcessedFolder { get; set; } = "processed";
    public string LockFolder { get; set; } = "lock";

    /// <summary>A lock file older than this is treated as stale and may be reclaimed.</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

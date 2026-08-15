namespace Agent.Core;

/// <summary>Lifecycle state of a task exchanged through the shared folder.</summary>
public enum MessageStatus
{
    Pending,
    InProgress,
    Done,
    Failed
}

namespace Agent.Worker;

/// <summary>Configuration for the shell task handler.</summary>
public sealed class ShellOptions
{
    /// <summary>Master switch. When false, shell requests are rejected (arbitrary command execution off).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Shell executable. "pwsh" (PowerShell 7) by default; falls back to "powershell".</summary>
    public string Shell { get; set; } = "pwsh";

    /// <summary>Working directory for commands. Defaults to the user profile when empty.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Kill the command if it runs longer than this.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Cap on captured stdout/stderr characters (each) to keep result files small.</summary>
    public int MaxOutputChars { get; set; } = 20000;
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agent.Core;
using Microsoft.Extensions.Options;

namespace Agent.Worker;

/// <summary>
/// Runs a shell command on this machine. The producer sends <c>type: "shell"</c> with the command
/// text in <c>prompt</c>. Returns a compact JSON payload (exit code + captured stdout/stderr).
/// SECURITY: this executes arbitrary commands; only enable it when the shared folder is trusted.
/// </summary>
public sealed class ShellTaskHandler : ITaskHandler
{
    private static readonly JsonSerializerOptions ResultJson = new() { WriteIndented = false };

    private readonly ShellOptions _options;
    private readonly ILogger<ShellTaskHandler> _logger;

    public ShellTaskHandler(IOptions<ShellOptions> options, ILogger<ShellTaskHandler> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool CanHandle(string type) =>
        string.Equals(type, "shell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "cmd", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "pwsh", StringComparison.OrdinalIgnoreCase);

    public async Task<string> HandleAsync(TaskMessage message, CancellationToken ct)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Shell handler is disabled (set Shell:Enabled=true to allow command execution).");

        var command = message.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("The 'prompt' field must contain a command to run.");

        var workDir = string.IsNullOrWhiteSpace(_options.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _options.WorkingDirectory;

        _logger.LogInformation("Running shell command for task {Id}", message.Id);

        var psi = new ProcessStartInfo
        {
            FileName = _options.Shell,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.Timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        return JsonSerializer.Serialize(new
        {
            command,
            workingDirectory = workDir,
            exitCode = timedOut ? (int?)null : process.ExitCode,
            timedOut,
            stdout = Truncate(stdout.ToString()),
            stderr = Truncate(stderr.ToString())
        }, ResultJson);
    }

    private string Truncate(string text)
    {
        text = text.TrimEnd();
        return text.Length <= _options.MaxOutputChars
            ? text
            : text[.._options.MaxOutputChars] + "\n...[truncated]";
    }
}

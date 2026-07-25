using System.Diagnostics;
using System.Text;

namespace Boh.Web.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Runs an external tool with output captured and a hard timeout.
/// </summary>
/// <remarks>
/// Both callers (ffmpeg and gallery-dl) run inside a request, so a process that hangs would
/// otherwise hold a connection open indefinitely. Killing the whole tree matters for
/// gallery-dl, which spawns children of its own.
/// </remarks>
public sealed class ProcessRunner(ILogger<ProcessRunner> logger)
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Passed as a list so arguments are never re-parsed by a shell.
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        logger.LogDebug("Running {FileName} {Arguments}", fileName, string.Join(' ', arguments));

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process, fileName);

            // Distinguish our own timeout from the caller abandoning the request.
            if (ct.IsCancellationRequested) throw;

            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    private void TryKill(Process process, string fileName)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            logger.LogWarning(ex, "Could not kill {FileName} after timeout", fileName);
        }
    }
}

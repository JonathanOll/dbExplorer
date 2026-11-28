using System.Diagnostics;
using System.Text;

namespace DbExplorer.Core.Claude.Assist;

/// <summary>One headless run of the claude command. <see cref="Input"/> goes to its standard input.</summary>
public sealed record ClaudeProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, string Input);

/// <param name="Error">The start of what the process wrote on its standard error.</param>
public sealed record ClaudeProcessExit(int Code, string Error);

/// <summary>Runs a process and hands over its output line by line; tests fake it.</summary>
public interface IClaudeProcessRunner
{
    /// <summary>Cancelling kills the process and everything it started, then throws <see cref="OperationCanceledException"/>.</summary>
    Task<ClaudeProcessExit> RunAsync(ClaudeProcessRequest request, Action<string> onLine, CancellationToken ct);
}

/// <summary>The real runner: no window, no shell, UTF-8 both ways.</summary>
public sealed class ClaudeProcessRunner : IClaudeProcessRunner
{
    private const int MaxError = 8_000;

    public async Task<ClaudeProcessExit> RunAsync(ClaudeProcessRequest request, Action<string> onLine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var utf8 = new UTF8Encoding(false);
        var start = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
        try
        {
            // Written and drained alongside the reading: a full pipe on either side must not stall the other.
            var input = WriteAsync(process, request.Input, ct);
            var error = ReadErrorAsync(process, ct);
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line) onLine(line);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await input.ConfigureAwait(false);
            return new ClaudeProcessExit(process.ExitCode, await error.ConfigureAwait(false));
        }
        finally
        {
            Kill(process);
        }
    }

    private static async Task WriteAsync(Process process, string text, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) { /* the process ended without reading it all: its exit says why */ }
        catch (OperationCanceledException) { }
    }

    private static async Task<string> ReadErrorAsync(Process process, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                if (text.Length < MaxError) text.Append(buffer, 0, Math.Min(read, MaxError - text.Length));
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* it ended in the meantime */ }
        catch (System.ComponentModel.Win32Exception) { /* it is ending */ }
    }
}

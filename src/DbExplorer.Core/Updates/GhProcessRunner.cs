using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DbExplorer.Core.Updates;

/// <summary>Real <see cref="IGhRunner"/>: starts `gh` hidden, captures output, kills it on timeout/cancel.</summary>
public sealed class GhProcessRunner : IGhRunner
{
    private readonly string _exe;

    public GhProcessRunner(string exe = "gh") => _exe = exe;

    public async Task<GhResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GH_PROMPT_DISABLED"] = "1"; // never wait for user input
        psi.Environment["NO_COLOR"] = "1";

        Process? process;
        try
        {
            process = Process.Start(psi);
            if (process is null) return GhResult.Missing();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException or PlatformNotSupportedException)
        {
            return GhResult.Missing();
        }

        using (process)
        {
            try { process.StandardInput.Close(); } catch (IOException) { }

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                // Exit seen: drain the pipes (bounded, in case a grandchild keeps them open).
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                return new GhResult(ct.IsCancellationRequested ? GhRunStatus.Cancelled : GhRunStatus.TimedOut, -1, "", "");
            }
            catch (TimeoutException) { /* drain gave up: use what we have */ }

            return new GhResult(GhRunStatus.Completed, process.ExitCode, Done(stdout), Done(stderr));
        }
    }

    private static string Done(Task<string> t) => t.IsCompletedSuccessfully ? t.Result : "";

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }
}

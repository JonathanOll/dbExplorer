namespace DbExplorer.Core.Updates;

public enum GhRunStatus
{
    /// <summary>The process ran to completion (check ExitCode).</summary>
    Completed,
    /// <summary>gh could not be started (not installed / not on PATH).</summary>
    NotInstalled,
    /// <summary>gh was killed after the timeout.</summary>
    TimedOut,
    /// <summary>The caller cancelled; gh was killed.</summary>
    Cancelled,
}

/// <summary>Outcome of one gh invocation. Never an exception.</summary>
public sealed record GhResult(GhRunStatus Status, int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => Status == GhRunStatus.Completed && ExitCode == 0;

    public static GhResult Ok(string stdout = "") => new(GhRunStatus.Completed, 0, stdout, "");
    public static GhResult Fail(int exitCode, string stderr = "") => new(GhRunStatus.Completed, exitCode, "", stderr);
    public static GhResult Missing() => new(GhRunStatus.NotInstalled, -1, "", "gh not found");
}

/// <summary>Runs the GitHub CLI. Implementations must not throw for process failures.</summary>
public interface IGhRunner
{
    Task<GhResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

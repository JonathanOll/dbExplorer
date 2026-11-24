using System.Diagnostics;
using System.Text;

namespace DbExplorer.App.Updates;

/// <summary>Outcome of <see cref="UpdateApplier.Launch"/>: the relay is running, or why it was not started.</summary>
public sealed record UpdateLaunchResult(bool Started, string? Reason = null)
{
    public static UpdateLaunchResult Ok() => new(true);
    public static UpdateLaunchResult Failed(string reason) => new(false, reason);
}

/// <summary>
/// The "apply" half of the auto-update. The running app cannot overwrite its own exe and dlls, so <see cref="Launch"/> starts a
/// detached, hidden Windows PowerShell relay (<c>ApplyUpdate.ps1</c>, embedded) and the caller then shuts the app down. The relay
/// waits for the process to exit, swaps the files (recoverable: old files kept in <c>.update-backup</c> until the new ones are in)
/// and relaunches the app, the new version on success, the old one on any failure.
/// </summary>
public static class UpdateApplier
{
    private const string ScriptResource = "DbExplorer.App.Updates.ApplyUpdate.ps1";
    private const string AppExeName = "DbExplorer.exe";

    /// <summary>
    /// Whether this process can replace itself. Simple heuristic, on purpose: the exe must be <c>DbExplorer.exe</c> (under
    /// <c>dotnet run</c> / <c>dotnet DbExplorer.dll</c> the process is <c>dotnet.exe</c>), must not sit in a build output folder
    /// (<c>\bin\Debug\</c>, <c>\bin\Release\</c>; the published layout lives elsewhere, e.g. <c>dist\</c> or an install folder),
    /// must be inside <paramref name="installDir"/>, and the folder must be writable (a throw-away probe file is created in it).
    /// </summary>
    public static (bool CanUpdate, string? Reason) CanSelfUpdate(string? exePath, string? installDir)
    {
        if (!OperatingSystem.IsWindows()) return (false, "Self-update is only supported on Windows.");
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(installDir)) return (false, "The install location is unknown.");
        var name = Path.GetFileName(exePath);
        if (name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return (false, "Running under dotnet (development): not an installed copy.");
        if (!name.Equals(AppExeName, StringComparison.OrdinalIgnoreCase)) return (false, $"The running program is {name}, not {AppExeName}.");
        var full = Path.GetFullPath(exePath);
        if (full.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || full.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
            return (false, "Running from a build output folder (development): not an installed copy.");
        if (!Directory.Exists(installDir)) return (false, "The install folder does not exist.");
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        if (!string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase))
            return (false, "The program is not in the install folder.");
        try
        {
            var probe = Path.Combine(dir, ".update-probe-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception e) { return (false, "The install folder is not writable: " + e.Message); }
        return (true, null);
    }

    /// <summary>
    /// Starts the relay and returns at once; never throws. On <see cref="UpdateLaunchResult.Started"/> the caller must shut the
    /// app down (the relay waits up to 60 s for <paramref name="processId"/> to exit, then gives up leaving everything untouched).
    /// </summary>
    public static UpdateLaunchResult Launch(string zipPath, string installDir, string exePath, int processId, IReadOnlyList<string> relaunchArgs)
        => Launch(zipPath, installDir, exePath, processId, relaunchArgs, null, []);

    /// <summary>Test seam: another temp root, and extra relay script parameters (timeouts, failure injection).</summary>
    internal static UpdateLaunchResult Launch(string zipPath, string installDir, string exePath, int processId,
        IReadOnlyList<string> relaunchArgs, string? tempRoot, IReadOnlyList<string> extraScriptArgs)
    {
        try
        {
            if (!File.Exists(zipPath)) return UpdateLaunchResult.Failed("The downloaded update file is missing.");
            var (can, reason) = CanSelfUpdate(exePath, installDir);
            if (!can) return UpdateLaunchResult.Failed(reason!);
            var powershell = PowerShellPath();
            if (powershell is null) return UpdateLaunchResult.Failed("Windows PowerShell was not found.");
            var script = WriteRelay(zipPath, installDir, exePath, processId, relaunchArgs, tempRoot, out var scriptArgs);

            var psi = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),   // never the install dir: it would stay locked by the relay
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
                psi.ArgumentList.Add(a);
            foreach (var a in scriptArgs.Concat(extraScriptArgs)) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            return process is null ? UpdateLaunchResult.Failed("The update relay did not start.") : UpdateLaunchResult.Ok();
        }
        catch (Exception e) { return UpdateLaunchResult.Failed("The update relay could not be started: " + e.Message); }
    }

    /// <summary>Windows PowerShell 5.1 (always present on Windows; pwsh 7 is not required).</summary>
    internal static string? PowerShellPath()
    {
        var path = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Writes the relay script (and the relaunch arguments) to a fresh temp dir; returns the script path and its parameters.</summary>
    internal static string WriteRelay(string zipPath, string installDir, string exePath, int processId,
        IReadOnlyList<string> relaunchArgs, string? tempRoot, out List<string> scriptArgs)
    {
        if (relaunchArgs.Any(a => a.Contains('\n') || a.Contains('\r'))) throw new ArgumentException("A relaunch argument contains a line break.");
        var dir = Path.Combine(tempRoot ?? Path.GetTempPath(), "dbExplorer-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        using var resource = typeof(UpdateApplier).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException("Embedded resource missing: " + ScriptResource);
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var script = Path.Combine(dir, "ApplyUpdate.ps1");
        File.WriteAllText(script, reader.ReadToEnd(), new UTF8Encoding(true));   // BOM: Windows PowerShell 5.1 reads UTF-8 only with it

        scriptArgs = ["-ZipPath", zipPath, "-InstallDir", Path.TrimEndingDirectorySeparator(installDir), "-ExePath", exePath, "-ProcessId", processId.ToString()];
        if (relaunchArgs.Count > 0)
        {
            var argsFile = Path.Combine(dir, "args.txt");
            File.WriteAllLines(argsFile, relaunchArgs, new UTF8Encoding(false));
            scriptArgs.AddRange(["-ArgsFile", argsFile]);
        }
        return script;
    }
}

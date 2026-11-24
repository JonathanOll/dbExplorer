using System.Diagnostics;
using System.IO.Compression;
using DbExplorer.App.Updates;

namespace DbExplorer.App.Tests.Updates;

/// <summary>Skipped on machines without Windows PowerShell 5.1.</summary>
public sealed class PowerShellFactAttribute : FactAttribute
{
    public PowerShellFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || UpdateApplier.PowerShellPath() is null) Skip = "Windows PowerShell is not available.";
    }
}

/// <summary>
/// Runs the real relay script against a throw-away layout: <c>root\install</c> (the "installed app": a fake.cmd standing in for
/// the exe, via the script's -ExeName parameter, plus a version.txt) and <c>root\update.zip</c>. (A copy of a system exe cannot
/// be run on some machines.) The relaunch is observable: fake.cmd is started in the install dir and writes its two arguments and
/// version.txt to <c>root\marker.txt</c>, so the marker says which version was started and with what arguments.
/// </summary>
public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-update-test-" + Guid.NewGuid().ToString("N")[..8]);
    private string Install => Path.Combine(_root, "install");
    private string Zip => Path.Combine(_root, "update.zip");
    private string Marker => Path.Combine(_root, "marker.txt");
    private string Relay => Path.Combine(_root, "relay");
    private static readonly string[] RelaunchArgs = ["first arg", "second"];   // one argument with a space on purpose
    private const string FakeExe = "fake.cmd";
    private const string FakeExeBody = "@echo off\r\necho %~1^|%~2^|>..\\marker.tmp\r\ntype version.txt>>..\\marker.tmp\r\nmove /y ..\\marker.tmp ..\\marker.txt>nul\r\n";

    public UpdateApplierTests()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Path.Combine(Install, FakeExe), FakeExeBody);
        File.WriteAllText(Path.Combine(Install, "version.txt"), "old");
        File.WriteAllText(Path.Combine(Install, "old-only.dll"), "old");
        Directory.CreateDirectory(Path.Combine(Install, "sub"));
        File.WriteAllText(Path.Combine(Install, "sub", "old.txt"), "old");
    }

    public void Dispose()
    {
        for (var i = 0; i < 20 && Directory.Exists(_root); i++)
        {
            try { Directory.Delete(_root, true); }
            catch { Thread.Sleep(100); }
        }
    }

    private void MakeZip(bool withExe = true)
    {
        var src = Path.Combine(_root, "zipsrc");
        Directory.CreateDirectory(Path.Combine(src, "lib"));
        if (withExe) File.WriteAllText(Path.Combine(src, FakeExe), FakeExeBody);
        File.WriteAllText(Path.Combine(src, "version.txt"), "new");
        File.WriteAllText(Path.Combine(src, "new.dll"), "new");
        File.WriteAllText(Path.Combine(src, "lib", "y.dll"), "new");
        ZipFile.CreateFromDirectory(src, Zip);
        Directory.Delete(src, true);
    }

    /// <summary>Writes the relay for this layout and runs it to the end; returns its exit code.</summary>
    private int RunRelay(int processId = 0, string[]? extra = null, int timeoutMs = 30_000)
    {
        var script = UpdateApplier.WriteRelay(Zip, Install, Path.Combine(Install, FakeExe), processId, RelaunchArgs, Relay, out var args);
        var psi = new ProcessStartInfo(UpdateApplier.PowerShellPath()!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(a);
        foreach (var a in args.Concat(["-ExeName", FakeExe, "-RetryCount", "2", "-RetryDelayMs", "50"]).Concat(extra ?? [])) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        Assert.True(p.WaitForExit(timeoutMs), "the relay script did not finish");
        return p.ExitCode;
    }

    /// <summary>The version of the relaunched app (its marker is written atomically; the arguments line is checked here too).</summary>
    private string WaitForMarker()
    {
        for (var i = 0; i < 200 && !File.Exists(Marker); i++) Thread.Sleep(50);
        Assert.True(File.Exists(Marker), "the app was not relaunched");
        var lines = File.ReadAllLines(Marker);
        Assert.Equal("first arg|second|", lines[0]);   // the original arguments, the one with a space kept whole
        return lines[1].Trim();
    }

    private string ReadVersion() => File.ReadAllText(Path.Combine(Install, "version.txt"));

    private void AssertOldIntact()
    {
        Assert.Equal("old", ReadVersion());
        Assert.True(File.Exists(Path.Combine(Install, FakeExe)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Install, "old-only.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Install, "sub", "old.txt")));
        Assert.False(File.Exists(Path.Combine(Install, "new.dll")));
        Assert.False(Directory.Exists(Path.Combine(Install, "lib")));
        Assert.False(Directory.Exists(Path.Combine(Install, ".update-backup")));
    }

    private string KeptLog() => File.ReadAllText(Directory.GetFiles(Relay, "update.log", SearchOption.AllDirectories).Single());

    [PowerShellFact]
    public void Success_SwapsTheFiles_RelaunchesTheNewExeWithTheArgs_AndCleansUp()
    {
        MakeZip();

        Assert.Equal(0, RunRelay());

        Assert.Equal("new", ReadVersion());
        Assert.Equal("new", File.ReadAllText(Path.Combine(Install, "new.dll")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Install, "lib", "y.dll")));
        Assert.False(File.Exists(Path.Combine(Install, "old-only.dll")));
        Assert.False(Directory.Exists(Path.Combine(Install, "sub")));
        Assert.False(Directory.Exists(Path.Combine(Install, ".update-backup")));
        Assert.Equal("new", WaitForMarker());   // started in the install dir with the original arguments
        Assert.False(File.Exists(Zip));   // the downloaded zip is deleted
        Assert.Empty(Directory.GetFileSystemEntries(Relay));   // script, staging and log dir are gone
    }

    [PowerShellFact]
    public void BadZip_LeavesTheOldFilesIntact_AndRelaunchesTheOldExe()
    {
        File.WriteAllText(Zip, "this is not a zip");

        Assert.Equal(1, RunRelay());

        AssertOldIntact();
        Assert.Equal("old", WaitForMarker());
        Assert.True(File.Exists(Zip));
        Assert.Contains("FAILED", KeptLog());
    }

    [PowerShellFact]
    public void ZipWithoutTheExe_LeavesTheOldFilesIntact_AndRelaunchesTheOldExe()
    {
        MakeZip(withExe: false);

        Assert.Equal(1, RunRelay());

        AssertOldIntact();
        Assert.Equal("old", WaitForMarker());
        Assert.Contains("not found in the zip", KeptLog());
    }

    [PowerShellFact]
    public void MissingZip_LeavesTheOldFilesIntact_AndRelaunchesTheOldExe()
    {
        Assert.Equal(1, RunRelay());

        AssertOldIntact();
        Assert.Equal("old", WaitForMarker());
    }

    [PowerShellFact]
    public void CopyFailure_RestoresTheBackup_RemovesTheHalfCopiedNewFiles_AndRelaunchesTheOldExe()
    {
        MakeZip();

        Assert.Equal(1, RunRelay(extra: ["-TestFailAfterCopy", "2"]));

        AssertOldIntact();
        Assert.Equal("old", WaitForMarker());
        Assert.True(File.Exists(Zip));
        Assert.Contains("backup restored", KeptLog());
    }

    [PowerShellFact]
    public void LockedOldFile_AbortsTheSwap_PutsBackWhatWasMoved_AndRelaunchesTheOldExe()
    {
        MakeZip();
        using var locked = new FileStream(Path.Combine(Install, "old-only.dll"), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(1, RunRelay());

        locked.Dispose();
        AssertOldIntact();
        Assert.Equal("old", WaitForMarker());
    }

    [PowerShellFact]
    public void AppThatNeverExits_TimesOut_TouchesNothing_AndDoesNotRelaunch()
    {
        MakeZip();
        using var app = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.Equal(2, RunRelay(processId: app.Id, extra: ["-WaitTimeoutSec", "1"]));
        }
        finally
        {
            try { app.Kill(true); } catch (InvalidOperationException) { }
        }

        AssertOldIntact();
        Assert.True(File.Exists(Zip));
        Assert.False(File.Exists(Marker));
        Assert.Contains("aborted", KeptLog());
    }

    [PowerShellFact]
    public void WaitsForTheAppToExit_BeforeSwapping()
    {
        MakeZip();
        using var app = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;

        Assert.Equal(0, RunRelay(processId: app.Id));

        Assert.True(app.HasExited);
        Assert.Equal("new", ReadVersion());
        Assert.Equal("new", WaitForMarker());
    }

    [PowerShellFact]
    public void InterruptedUpdate_BrokenInstallIsRestoredFromTheLeftOverBackup_ThenTheUpdateRuns()
    {
        // A previous run died after moving the files out: the install holds no exe, the backup holds everything.
        var backup = Path.Combine(Install, ".update-backup");
        Directory.CreateDirectory(backup);
        foreach (var entry in Directory.GetFileSystemEntries(Install).Where(e => Path.GetFileName(e) != ".update-backup"))
            Directory.Move(entry, Path.Combine(backup, Path.GetFileName(entry)));   // dirs and files alike (same volume)
        File.WriteAllText(Zip, "this is not a zip");

        Assert.Equal(1, RunRelay());   // the update itself fails (bad zip), the old install must be whole again

        AssertOldIntact();
        Assert.Equal("old", WaitForMarker());
    }

    [PowerShellFact]
    public void Launch_RunsTheRelayDetached_AndTheUpdateCompletes()
    {
        MakeZip();
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();

        // CanSelfUpdate wants the real exe name: only the guard looks at it, the relay relaunches the fake through -ExeName.
        var result = UpdateApplier.Launch(Zip, Install, Path.Combine(Install, "DbExplorer.exe"), gone.Id, RelaunchArgs, Relay, ["-ExeName", FakeExe, "-RetryDelayMs", "50"]);

        Assert.True(result.Started, result.Reason);
        Assert.Equal("new", WaitForMarker());
        for (var i = 0; i < 100 && Directory.Exists(Install + "\\.update-backup"); i++) Thread.Sleep(50);
        for (var i = 0; i < 100 && Directory.GetFileSystemEntries(Relay).Length > 0; i++) Thread.Sleep(50);
        Assert.Empty(Directory.GetFileSystemEntries(Relay));
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public void Launch_MissingZip_ReturnsFailed_NeverThrows()
    {
        var result = UpdateApplier.Launch(Zip, Install, Path.Combine(Install, "DbExplorer.exe"), 0, []);

        Assert.False(result.Started);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void CanSelfUpdate_InstalledCopy_IsAllowed()
    {
        var (ok, reason) = UpdateApplier.CanSelfUpdate(Path.Combine(Install, "DbExplorer.exe"), Install);

        Assert.True(ok, reason);
        Assert.Null(reason);
        Assert.Empty(Directory.GetFiles(Install, ".update-probe-*"));   // the writability probe is removed
    }

    [Fact]
    public void CanSelfUpdate_Refuses_DotnetHost_BuildOutputFolders_UnknownOrMissingFolders_AndForeignExe()
    {
        var dev = Path.Combine(_root, "repo", "src", "App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(dev);
        var release = Path.Combine(_root, "repo", "src", "App", "bin", "Release", "net10.0");
        Directory.CreateDirectory(release);

        Assert.False(UpdateApplier.CanSelfUpdate(Path.Combine(Install, "dotnet.exe"), Install).CanUpdate);
        Assert.False(UpdateApplier.CanSelfUpdate(Path.Combine(dev, "DbExplorer.exe"), dev).CanUpdate);
        Assert.False(UpdateApplier.CanSelfUpdate(Path.Combine(release, "DbExplorer.exe"), release).CanUpdate);
        Assert.False(UpdateApplier.CanSelfUpdate(Path.Combine(Install, "Other.exe"), Install).CanUpdate);
        Assert.False(UpdateApplier.CanSelfUpdate(Path.Combine(_root, "nope", "DbExplorer.exe"), Path.Combine(_root, "nope")).CanUpdate);
        Assert.False(UpdateApplier.CanSelfUpdate(Path.Combine(Install, "DbExplorer.exe"), Path.Combine(_root, "elsewhere")).CanUpdate);
        Assert.False(UpdateApplier.CanSelfUpdate(null, Install).CanUpdate);
        Assert.False(UpdateApplier.CanSelfUpdate(Path.Combine(Install, "DbExplorer.exe"), "").CanUpdate);
    }
}

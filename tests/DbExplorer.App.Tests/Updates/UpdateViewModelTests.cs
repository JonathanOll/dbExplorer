using DbExplorer.App.Updates;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Updates;

namespace DbExplorer.App.Tests.Updates;

public class UpdateViewModelTests : IDisposable
{
    internal static readonly ReleaseInfo Release = new("owner/repo", "v1.2.0", new AppVersion(1, 2, 0), "DbExplorer.zip", 1000, null);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-update-vm-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly FakeChecker _checker = new();
    private readonly FakeDownloader _downloader;
    private readonly FakeApplier _applier;
    private readonly FakeHost _host;

    public UpdateViewModelTests()
    {
        _downloader = new FakeDownloader(_log);
        _applier = new FakeApplier(_log);
        _host = new FakeHost(_log);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private UpdateViewModel Vm(bool autoCheck = true, bool suppressed = false, Action<bool>? save = null) =>
        new(_checker, _downloader, _applier, () => Path.Combine(_dir, "staging"), autoCheck, suppressed, save) { Host = _host };

    private async Task<UpdateViewModel> Available()
    {
        _checker.Result = new UpdateCheckResult.UpdateAvailable(Release);
        var vm = Vm();
        await vm.CheckAsync();
        return vm;
    }

    // ---- the check ----

    private sealed class SlowChecker(TaskCompletionSource<UpdateCheckResult> gate) : IUpdateChecker
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) => gate.Task;
    }

    [Fact]
    public async Task WhileChecking_TheBandSaysSo_AndItGoesWhenTheAnswerIsIn()
    {
        var gate = new TaskCompletionSource<UpdateCheckResult>();
        var vm = new UpdateViewModel(new SlowChecker(gate), _downloader, _applier, () => Path.Combine(_dir, "staging")) { Host = _host };
        Assert.False(vm.IsChecking);

        var check = vm.CheckAsync();
        Assert.True(vm.IsChecking);
        Assert.False(vm.IsVisible);   // the "update available" banner is another band

        gate.SetResult(new UpdateCheckResult.UpToDate(new AppVersion(1, 2, 0), new AppVersion(1, 2, 0)));
        await check;
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task ACheckThatFails_StillClearsTheCheckingBand()
    {
        _checker.Throw = true;
        var vm = Vm();
        await vm.CheckAsync();
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task NoCheck_NoCheckingBand()
    {
        var vm = Vm(suppressed: true);
        await vm.CheckAsync();
        Assert.False(vm.IsChecking);
        Assert.Equal(0, _checker.Calls);
    }


    [Fact]
    public async Task NewerRelease_MakesTheBannerAvailable()
    {
        var vm = await Available();

        Assert.Equal(UpdateState.Available, vm.State);
        Assert.True(vm.IsVisible);
        Assert.True(vm.CanAct);
        Assert.Equal("Version 1.2.0 is available.", vm.Text);
        Assert.Equal("Update and restart", vm.ActionText);
        Assert.Equal("Later", vm.DismissText);
    }

    [Fact]
    public async Task UpToDate_StaysIdleAndHidden()
    {
        _checker.Result = new UpdateCheckResult.UpToDate(new AppVersion(1, 2, 0), new AppVersion(1, 2, 0));
        var vm = Vm();

        await vm.CheckAsync();

        Assert.Equal(UpdateState.Idle, vm.State);
        Assert.False(vm.IsVisible);
    }

    [Theory]
    [InlineData(UnavailableReason.GhMissing)]
    [InlineData(UnavailableReason.NotAuthenticatedOrNetwork)]
    [InlineData(UnavailableReason.BadResponse)]
    [InlineData(UnavailableReason.DevBuild)]
    public async Task UnavailableCheck_IsSilent(UnavailableReason reason)
    {
        _checker.Result = new UpdateCheckResult.Unavailable(reason, "offline");
        var vm = Vm();

        await vm.CheckAsync();

        Assert.Equal(UpdateState.Idle, vm.State);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task ACheckThatThrows_IsSilent()
    {
        _checker.Throw = true;
        var vm = Vm();

        await vm.CheckAsync();

        Assert.Equal(UpdateState.Idle, vm.State);
    }

    [Fact]
    public async Task SettingOff_NeverChecks()
    {
        var vm = Vm(autoCheck: false);

        await vm.CheckAsync();

        Assert.Equal(0, _checker.Calls);
        Assert.Equal(UpdateState.Idle, vm.State);
    }

    [Fact]
    public async Task SwitchedOffForThisRun_NeverChecks_EvenWithTheSettingOn()
    {
        var vm = Vm(autoCheck: true, suppressed: true);

        await vm.CheckAsync();

        Assert.Equal(0, _checker.Calls);
    }

    [Fact]
    public async Task SettingFlippedOffBeforeTheCheck_IsHonoured()
    {
        var vm = Vm();
        vm.AutoCheckEnabled = false;

        await vm.CheckAsync();

        Assert.Equal(0, _checker.Calls);
    }

    [Fact]
    public void FlippingTheSetting_IsPersisted_AndAFailingSaveIsSwallowed()
    {
        var saved = new List<bool>();
        var vm = Vm(save: saved.Add);
        vm.AutoCheckEnabled = false;
        vm.AutoCheckEnabled = true;
        Assert.Equal([false, true], saved);

        var broken = Vm(save: _ => throw new IOException("disk full"));
        broken.AutoCheckEnabled = false;   // must not throw
        Assert.False(broken.AutoCheckEnabled);
    }

    [Fact]
    public async Task WithoutAChecker_NothingHappens()
    {
        var vm = new UpdateViewModel();

        await vm.CheckAsync();
        await vm.UpdateAsync();

        Assert.Equal(UpdateState.Idle, vm.State);
    }

    [Fact]
    public async Task ASecondCheck_DoesNotAskAgain_OnceAnUpdateIsOffered()
    {
        var vm = await Available();

        await vm.CheckAsync();

        Assert.Equal(1, _checker.Calls);
    }

    // ---- dismiss ----

    [Fact]
    public async Task Later_HidesTheBannerForThisRun_AndNoLaterCheckBringsItBack()
    {
        var vm = await Available();

        vm.DismissCommand.Execute(null);
        await vm.CheckAsync();

        Assert.Equal(UpdateState.Dismissed, vm.State);
        Assert.False(vm.IsVisible);
        Assert.Equal(1, _checker.Calls);
        Assert.Empty(_downloader.Requests);
    }

    // ---- the update ----

    [Fact]
    public async Task Update_DownloadsToAFreshFolder_AsksAboutUnsavedWork_ThenLaunchesAndShutsDown()
    {
        var vm = await Available();

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(["download", "confirm", "launch", "shutdown"], _log);
        Assert.Equal(Path.Combine(_dir, "staging"), _downloader.Requests.Single().Dir);
        Assert.Equal(Release, _downloader.Requests.Single().Release);
        Assert.Equal(Path.Combine(_dir, "staging", "DbExplorer.zip"), _applier.Zips.Single());
        Assert.Equal(UpdateState.Applying, vm.State);
        Assert.Equal("Restarting to install version 1.2.0...", vm.Text);
        Assert.False(vm.CanAct);
    }

    [Fact]
    public async Task DownloadProgress_ShowsAsAFraction_AndAPercentInTheText()
    {
        var vm = await Available();
        var seen = new List<(double, string)>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateViewModel.Progress)) seen.Add((vm.Progress, vm.Text));
        };
        _downloader.OnDownload = progress =>
        {
            progress!.Report(0.25);
            progress.Report(0.5);
            Assert.True(vm.IsDownloading);
            Assert.False(vm.CanAct);   // no buttons while downloading
            Assert.Equal("Downloading version 1.2.0... 50 %", vm.Text);
        };

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Contains((0.25, "Downloading version 1.2.0... 25 %"), seen);
        Assert.Contains((0.5, "Downloading version 1.2.0... 50 %"), seen);
    }

    [Fact]
    public async Task DownloadFailure_ShowsTheReason_AndCanBeRetried()
    {
        var vm = await Available();
        _downloader.Result = DownloadResult.Failed(DownloadFailure.GhFailed, "HTTP 502");

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.True(vm.IsFailed);
        Assert.True(vm.IsVisible);
        Assert.Contains("HTTP 502", vm.Text);
        Assert.Equal("Retry", vm.ActionText);
        Assert.Equal("Dismiss", vm.DismissText);
        Assert.Empty(_applier.Zips);
        Assert.DoesNotContain("confirm", _log);

        _downloader.Result = null;   // the network is back
        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(["download", "download", "confirm", "launch", "shutdown"], _log);
    }

    [Fact]
    public async Task HashMismatch_FailsWithAClearMessage_AndInstallsNothing()
    {
        var vm = await Available();
        _downloader.Result = DownloadResult.Failed(DownloadFailure.HashMismatch, "expected aaa, got bbb");

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("checksum", vm.Text);
        Assert.Empty(_applier.Zips);
        Assert.DoesNotContain("shutdown", _log);
    }

    [Fact]
    public async Task GhMissingDuringDownload_IsReported()
    {
        var vm = await Available();
        _downloader.Result = DownloadResult.Failed(DownloadFailure.GhMissing);

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("gh", vm.Text);
    }

    [Fact]
    public async Task LaunchFailure_ShowsTheReason_RestoresTheTabs_AndKeepsTheAppOpen()
    {
        var vm = await Available();
        _applier.Result = UpdateLaunchResult.Failed("Windows PowerShell was not found.");

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("Windows PowerShell was not found.", vm.Text);
        Assert.Equal(["download", "confirm", "launch", "aborted"], _log);   // never shut down; tabs that said yes go back to normal
    }

    [Fact]
    public async Task ALaunchThatThrows_IsAFailure_NotACrash()
    {
        var vm = await Available();
        _applier.Throw = true;

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.Contains("aborted", _log);
        Assert.DoesNotContain("shutdown", _log);
    }

    [Fact]
    public async Task CancelAtTheUnsavedWorkPrompt_AbortsTheUpdate_WithoutLaunchingOrClosing()
    {
        var vm = await Available();
        _host.Confirm = false;

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(["download", "confirm"], _log);
        Assert.Equal(UpdateState.Available, vm.State);
        Assert.True(vm.CanAct);
        Assert.Empty(_applier.Zips);
    }

    [Fact]
    public async Task AfterACancel_ASecondClickReusesTheDownload()
    {
        var vm = await Available();
        _host.Confirm = false;
        await vm.UpdateCommand.ExecuteAsync(null);
        _host.Confirm = true;

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(["download", "confirm", "confirm", "launch", "shutdown"], _log);   // one download only
    }

    [Fact]
    public async Task APromptThatThrows_IsAFailure_AndTheAppStaysOpen()
    {
        var vm = await Available();
        _host.ThrowOnConfirm = true;

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Failed, vm.State);
        Assert.DoesNotContain("launch", _log);
        Assert.DoesNotContain("shutdown", _log);
    }

    [Fact]
    public async Task UpdateWhileAlreadyDownloading_IsIgnored()
    {
        var vm = await Available();
        var gate = new TaskCompletionSource();
        _downloader.Wait = gate.Task;
        var first = vm.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Downloading, vm.State);
        await vm.UpdateCommand.ExecuteAsync(null);   // a second click: nothing
        vm.DismissCommand.Execute(null);             // "Later" cannot interrupt a download either
        Assert.Equal(UpdateState.Downloading, vm.State);

        gate.SetResult();
        await first;

        Assert.Single(_downloader.Requests);
        Assert.Equal(["download", "confirm", "launch", "shutdown"], _log);
    }

    [Fact]
    public async Task Dismissing_AfterAFailure_RemovesTheBanner()
    {
        var vm = await Available();
        _downloader.Result = DownloadResult.Failed(DownloadFailure.GhFailed);
        await vm.UpdateCommand.ExecuteAsync(null);

        vm.DismissCommand.Execute(null);

        Assert.Equal(UpdateState.Dismissed, vm.State);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task WithoutAHost_NothingIsDownloaded()
    {
        var vm = await Available();
        vm.Host = null;

        await vm.UpdateCommand.ExecuteAsync(null);

        Assert.Empty(_downloader.Requests);
        Assert.Equal(UpdateState.Available, vm.State);
    }

    [Fact]
    public void TheShell_HasAnInertUpdateBanner_UntilItIsWiredUp()
    {
        using var fx = new TreeFixture();
        var main = new MainViewModel(fx.CreateTree(DbExplorer.Core.Model.AppConfig.Empty()), fx.Status);

        Assert.False(main.Update.IsVisible);
        var vm = Vm();
        main.UseUpdates(vm);
        Assert.Same(vm, main.Update);
    }

    // ---- fakes ----

    internal sealed class FakeChecker : IUpdateChecker
    {
        public UpdateCheckResult Result { get; set; } = new UpdateCheckResult.UpdateAvailable(Release);
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
        {
            Calls++;
            return Throw ? throw new InvalidOperationException("boom") : Task.FromResult(Result);
        }
    }

    internal sealed class FakeDownloader(List<string> log) : IUpdateDownloader
    {
        public List<(ReleaseInfo Release, string Dir)> Requests { get; } = [];
        public DownloadResult? Result { get; set; }
        public Action<IProgress<double>?>? OnDownload { get; set; }
        public Task? Wait { get; set; }

        public async Task<DownloadResult> DownloadAsync(ReleaseInfo release, string stagingDir, IProgress<double>? progress, CancellationToken ct)
        {
            log.Add("download");
            Requests.Add((release, stagingDir));
            OnDownload?.Invoke(progress);
            if (Wait is not null) await Wait;
            if (Result is { } failed) return failed;
            Directory.CreateDirectory(stagingDir);
            var path = Path.Combine(stagingDir, "DbExplorer.zip");
            await File.WriteAllBytesAsync(path, [1, 2, 3], ct);
            return DownloadResult.Success(path, verified: true);
        }
    }

    internal sealed class FakeApplier(List<string> log) : IUpdateApplier
    {
        public List<string> Zips { get; } = [];
        public UpdateLaunchResult Result { get; set; } = UpdateLaunchResult.Ok();
        public bool Throw { get; set; }

        public UpdateLaunchResult Launch(string zipPath)
        {
            log.Add("launch");
            if (Throw) throw new InvalidOperationException("boom");
            Zips.Add(zipPath);
            return Result;
        }
    }

    internal sealed class FakeHost(List<string> log) : IUpdateHost
    {
        public bool Confirm { get; set; } = true;
        public bool ThrowOnConfirm { get; set; }

        public Task<bool> ConfirmExitAsync()
        {
            log.Add("confirm");
            return ThrowOnConfirm ? throw new InvalidOperationException("boom") : Task.FromResult(Confirm);
        }

        public void ExitAborted() => log.Add("aborted");
        public void Shutdown() => log.Add("shutdown");
    }
}

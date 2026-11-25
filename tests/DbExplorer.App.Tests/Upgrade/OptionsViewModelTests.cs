using DbExplorer.App.ViewModels;

namespace DbExplorer.App.Tests.Upgrade;

public class OptionsViewModelTests
{
    private readonly List<string> _validated = [];

    private OptionsViewModel Vm(string? current = null, string? error = null) =>
        new(current, p => { _validated.Add(p); return error; }, p => $"info for {p}");

    [Fact]
    public void BlankPath_ConfirmsWithNull_AndNoError()
    {
        var vm = Vm("   ");

        Assert.True(vm.TryConfirm());
        Assert.Null(vm.Result!.DbUpdatePath);
        Assert.Null(vm.PathError);
        Assert.Empty(_validated);
    }

    [Fact]
    public void NullCurrentPath_StartsBlank()
    {
        var vm = Vm(null);

        Assert.Equal("", vm.DbUpdatePath);
        Assert.True(vm.TryConfirm());
        Assert.Null(vm.Result!.DbUpdatePath);
    }

    [Fact]
    public void ValidPath_ResultIsTheTrimmedPath()
    {
        var vm = Vm();
        vm.DbUpdatePath = @"  C:\dbupdate  ";

        Assert.True(vm.TryConfirm());
        Assert.Equal(@"C:\dbupdate", vm.Result!.DbUpdatePath);
        Assert.Null(vm.PathError);
        Assert.Equal([@"C:\dbupdate"], _validated);
    }

    [Fact]
    public void ValidatorMessage_SetsPathError_AndRefusesToConfirm()
    {
        var vm = Vm(error: "Folder not found.");
        vm.DbUpdatePath = @"C:\nope";

        Assert.False(vm.TryConfirm());
        Assert.Null(vm.Result);
        Assert.Equal("Folder not found.", vm.PathError);
        Assert.True(vm.HasPathError);
    }

    [Fact]
    public void EditingTheText_ClearsTheError()
    {
        var vm = Vm(error: "Folder not found.");
        vm.DbUpdatePath = @"C:\nope";
        vm.ValidatePath();
        Assert.True(vm.HasPathError);

        vm.DbUpdatePath = @"C:\nope2";

        Assert.Null(vm.PathError);
        Assert.False(vm.HasPathError);
        Assert.Null(vm.PathInfo);
    }

    [Fact]
    public void ValidatePath_DoesNotCallTheValidatorForBlankText()
    {
        var vm = Vm();
        vm.DbUpdatePath = "  ";

        vm.ValidatePath();

        Assert.Empty(_validated);
        Assert.Null(vm.PathError);
        Assert.Null(vm.PathInfo);
    }

    [Fact]
    public void PathInfo_ComesFromDescribe_ForAValidPath_AndIsNullForAnInvalidOne()
    {
        var ok = Vm();
        ok.DbUpdatePath = @"C:\dbupdate";
        ok.ValidatePath();
        Assert.Equal(@"info for C:\dbupdate", ok.PathInfo);

        var bad = Vm(error: "no");
        bad.DbUpdatePath = @"C:\x";
        bad.ValidatePath();
        Assert.Null(bad.PathInfo);
        Assert.Equal("no", bad.PathError);
    }

    [Fact]
    public void WithoutDescribe_PathInfoStaysNull()
    {
        var vm = new OptionsViewModel(null, _ => null) { DbUpdatePath = @"C:\d" };

        vm.ValidatePath();

        Assert.Null(vm.PathInfo);
        Assert.Null(vm.PathError);
    }

    [Fact]
    public async Task ValidateSoon_ChecksTheTypedFolder_AfterTheDelay_AndShowsTheInfo()
    {
        var vm = Vm();
        vm.DbUpdatePath = @"C:\dbupdate";

        var check = vm.ValidateSoonAsync(delayMs: 10);
        Assert.True(vm.IsChecking);
        await check;

        Assert.False(vm.IsChecking);
        Assert.Null(vm.PathError);
        Assert.Equal(@"info for C:\dbupdate", vm.PathInfo);
        Assert.False(vm.ShowHint);
    }

    [Fact]
    public async Task ValidateSoon_ANewerKeystrokeSupersedesTheOlderCheck()
    {
        var vm = Vm();
        vm.DbUpdatePath = @"C:";
        var first = vm.ValidateSoonAsync(delayMs: 200);
        vm.DbUpdatePath = @"C:b";
        var second = vm.ValidateSoonAsync(delayMs: 10);

        await Task.WhenAll(first, second);

        Assert.Equal([@"C:b"], _validated);   // the older one never reached the disk
        Assert.False(vm.IsChecking);
        Assert.Equal(@"info for C:b", vm.PathInfo);
    }

    [Fact]
    public async Task ValidateSoon_ReportsAnUnusableFolder_AndBlankTextCheckNothing()
    {
        var bad = Vm(error: "Folder not found.");
        bad.DbUpdatePath = @"C:
ope";
        await bad.ValidateSoonAsync(delayMs: 5);
        Assert.Equal("Folder not found.", bad.PathError);
        Assert.Null(bad.PathInfo);

        var blank = Vm();
        blank.DbUpdatePath = "  ";
        await blank.ValidateSoonAsync(delayMs: 5);
        Assert.Equal([@"C:
ope"], _validated);   // only the typed folder was checked, never the blank one
        Assert.False(blank.IsChecking);
        Assert.True(blank.IsFeatureOff);
        Assert.Contains("off", blank.FeatureHint);
        Assert.True(blank.ShowHint);
    }

    [Fact]
    public async Task ValidateSoon_SkipsAFolderThatWasJustValidated()
    {
        var vm = Vm();
        vm.DbUpdatePath = @"C:\dbupdate";
        vm.ValidatePath();   // the Browse button validates at once

        await vm.ValidateSoonAsync(delayMs: 5);

        Assert.Equal([@"C:\dbupdate"], _validated);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task Confirm_SupersedesAPendingCheck()
    {
        var vm = Vm();
        vm.DbUpdatePath = @"C:\dbupdate";
        var pending = vm.ValidateSoonAsync(delayMs: 300);

        Assert.True(vm.TryConfirm());
        await pending;

        Assert.False(vm.IsChecking);
        Assert.Equal([@"C:\dbupdate"], _validated);   // validated once, by OK
    }
}

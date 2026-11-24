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
}

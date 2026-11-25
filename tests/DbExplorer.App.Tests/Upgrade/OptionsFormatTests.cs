using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>The "SQL formatting" group of the Options dialog: three choices and a live preview.</summary>
public class OptionsFormatTests
{
    private static OptionsViewModel Vm(SqlFormatOptions? format = null) => new(null, _ => null, null, format: format);

    [Fact]
    public void Defaults_ShowAnUppercaseFourSpacePreview()
    {
        var vm = Vm();

        Assert.Equal(0, vm.KeywordCaseIndex);
        Assert.Equal(1, vm.IndentIndex);
        Assert.Equal(0, vm.CommaIndex);
        Assert.StartsWith("SELECT\n    o.id,\n    c.name,", vm.Preview.ReplaceLineEndings("\n"));
        Assert.Contains("\nWHERE o.status IN ('new', 'paid')\n    AND o.created", vm.Preview.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void TheStoredOptions_FillTheChoices()
    {
        var vm = Vm(new SqlFormatOptions { KeywordCase = KeywordCase.Keep, IndentSize = 8, CommaPosition = CommaPosition.Leading });
        Assert.Equal((2, 2, 1), (vm.KeywordCaseIndex, vm.IndentIndex, vm.CommaIndex));

        var tabs = Vm(new SqlFormatOptions { UseTabs = true });
        Assert.Equal(OptionsViewModel.IndentChoices.Count - 1, tabs.IndentIndex);
    }

    [Fact]
    public void ChangingAChoice_UpdatesThePreviewAndTheResult()
    {
        var vm = Vm();
        vm.KeywordCaseIndex = 1;
        vm.CommaIndex = 1;
        vm.IndentIndex = 0;

        var preview = vm.Preview.ReplaceLineEndings("\n");
        Assert.StartsWith("select\n  o.id\n  , c.name", preview);
        Assert.True(vm.TryConfirm());
        Assert.Equal(new SqlFormatOptions { KeywordCase = KeywordCase.Lower, CommaPosition = CommaPosition.Leading, IndentSize = 2 }, vm.Result!.Format);
    }

    [Fact]
    public void Tabs_ArePreviewedAsSpaces_ButSavedAsTabs()
    {
        var vm = Vm();
        vm.IndentIndex = OptionsViewModel.IndentChoices.Count - 1;

        Assert.DoesNotContain('\t', vm.Preview);
        Assert.True(vm.TryConfirm());
        Assert.True(vm.Result!.Format!.UseTabs);
    }

    [AvaloniaFact]
    public void TheDialog_ShowsTheGroupAndFollowsThePreview()
    {
        var vm = Vm();
        var window = new OptionsDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var combos = window.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(3, combos.Count);
        var preview = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PreviewText");
        Assert.Equal(vm.Preview, preview.Text);

        combos[0].SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, vm.KeywordCaseIndex);
        Assert.StartsWith("select", preview.Text);
        window.Close();
    }
}

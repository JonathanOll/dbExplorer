using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Config;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

/// <summary>The restyled form dialogs on a real (headless) window: structure, states and keyboard behaviour.</summary>
public class DialogViewTests : IDisposable
{
    private readonly TreeFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static T Show<T>(T window) where T : Window
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Button? ButtonNamed(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == text);

    private static bool ShowsText(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    private static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled, $"'{button.Content}' is disabled");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    // ---- connection dialog ----

    [AvaloniaFact]
    public void ConnectionDialog_EngineTiles_FollowTheEngine_AndTheFormFollowsThem()
    {
        var vm = new ConnectionDialogViewModel(_fx.Protector, [], []);
        var window = Show(new ConnectionDialog { DataContext = vm });
        var tiles = window.FindControl<ListBox>("EngineList")!;

        Assert.Equal(3, tiles.ItemCount);
        Assert.Equal(DbKind.Postgres, tiles.SelectedItem);
        Assert.True(window.FindControl<TextBox>("HostBox")!.IsEffectivelyVisible);

        tiles.SelectedItem = DbKind.Sqlite;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DbKind.Sqlite, vm.Kind);
        Assert.False(window.FindControl<TextBox>("HostBox")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<TextBox>("PasswordBox")!.IsEffectivelyVisible);
        Assert.NotNull(ButtonNamed(window, "Browse…"));
        window.Close();
    }

    [AvaloniaFact]
    public void ConnectionDialog_OkWithMissingFields_ShowsInlineLines_AndStaysOpen()
    {
        var vm = new ConnectionDialogViewModel(_fx.Protector, [], []);
        var window = Show(new ConnectionDialog { DataContext = vm });
        var closed = false;
        window.Closed += (_, _) => closed = true;
        Assert.False(ShowsText(window, "Name is required."));

        Click(ButtonNamed(window, "Add connection")!);

        Assert.False(closed);
        Assert.True(ShowsText(window, "Name is required."));
        Assert.True(ShowsText(window, "Host is required."));
        Assert.Contains("error", window.FindControl<TextBox>("NameBox")!.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void ConnectionDialog_PasswordShowToggle_RevealsAndHides()
    {
        var vm = new ConnectionDialogViewModel(_fx.Protector, [], []) { Password = "secret" };
        var window = Show(new ConnectionDialog { DataContext = vm });
        var box = window.FindControl<TextBox>("PasswordBox")!;
        var toggle = window.FindControl<ToggleButton>("RevealButton")!;
        Assert.False(box.RevealPassword);

        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(box.RevealPassword);
        Assert.Equal("Hide", toggle.Content);
        window.Close();
    }

    [AvaloniaFact]
    public void ConnectionDialog_TrustCertificate_IsOffUntilEncryptIsOn()
    {
        var vm = new ConnectionDialogViewModel(_fx.Protector, [], []) { Kind = DbKind.SqlServer };
        var window = Show(new ConnectionDialog { DataContext = vm });
        var trust = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.IsChecked is false && ToolTip.GetTip(c) is string);

        Assert.False(trust.IsEnabled);

        vm.Encrypt = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(trust.IsEnabled);
        window.Close();
    }

    // ---- group editor ----

    private static AppConfig Config(IEnumerable<ConnectionConfig> connections) => new(new(), connections.ToList(), new());

    [AvaloniaFact]
    public void GroupEditor_ArrowButtons_AreLiveOnlyWhenTheyHaveSomethingToDo_AndEnterInTheListDoesNotSave()
    {
        var a = TreeFixture.Conn("A", database: "a.db");
        var b = TreeFixture.Conn("B", database: "b.db");
        var vm = new GroupEditorViewModel(Config([a, b]), null) { Name = "Pair" };
        var window = Show(new GroupEditorDialog { DataContext = vm });
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var add = window.FindControl<Button>("AddButton")!;
        var remove = window.FindControl<Button>("RemoveButton")!;
        var up = window.FindControl<Button>("UpButton")!;
        var down = window.FindControl<Button>("DownButton")!;
        var candidates = window.FindControl<ListBox>("CandidateList")!;
        var members = window.FindControl<ListBox>("MemberList")!;

        Assert.False(add.IsEnabled || remove.IsEnabled || up.IsEnabled || down.IsEnabled);

        candidates.SelectedItems!.Add(a);
        candidates.SelectedItems.Add(b);
        Dispatcher.UIThread.RunJobs();
        Assert.True(add.IsEnabled);

        candidates.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });   // adds both, does not save
        Dispatcher.UIThread.RunJobs();

        Assert.False(closed);
        Assert.Equal([a, b], vm.Selected);
        Assert.False(add.IsEnabled);

        members.SelectedItems!.Add(a);
        Dispatcher.UIThread.RunJobs();
        Assert.True(remove.IsEnabled);
        Assert.False(up.IsEnabled);    // already first
        Assert.True(down.IsEnabled);

        Click(down);
        Assert.Equal([b, a], vm.Selected);
        Assert.True(up.IsEnabled);
        Assert.False(down.IsEnabled);  // now last
        window.Close();
    }

    [AvaloniaFact]
    public void GroupEditor_MembersAreNumbered_AndTheEmptyListInvites()
    {
        var a = TreeFixture.Conn("A", database: "a.db");
        var b = TreeFixture.Conn("B", database: "b.db");
        var vm = new GroupEditorViewModel(Config([a, b]), null);
        var window = Show(new GroupEditorDialog { DataContext = vm });
        Assert.True(ShowsText(window, "No members yet"));

        vm.AddRange([a, b]);
        Dispatcher.UIThread.RunJobs();

        Assert.False(ShowsText(window, "No members yet"));
        Assert.True(ShowsText(window, "1"));
        Assert.True(ShowsText(window, "2"));
        Assert.True(ShowsText(window, "MEMBERS (2)"));

        vm.MoveTo(b, 0);   // the numbers follow the order
        Dispatcher.UIThread.RunJobs();
        var first = window.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.DataContext is ConnectionConfig)
            .First(i => i.Tag is 1 && i.DataContext is ConnectionConfig { Name: "B" });
        Assert.NotNull(first);
        window.Close();
    }

    [AvaloniaFact]
    public void GroupEditor_NameClash_ShowsUnderTheNameBox()
    {
        var a = TreeFixture.Conn("A", database: "a.db");
        var cfg = Config([a]);
        cfg.Groups.Add(new ConnectionGroup(Guid.NewGuid(), "Billing", [a.Id]));
        var vm = new GroupEditorViewModel(cfg, null);
        var window = Show(new GroupEditorDialog { DataContext = vm });

        vm.Name = "billing";
        Dispatcher.UIThread.RunJobs();

        Assert.True(ShowsText(window, "A group named \"billing\" already exists."));
        Assert.Contains("error", window.FindControl<TextBox>("NameBox")!.Classes);
        window.Close();
    }

    // ---- schema differences ----

    [AvaloniaFact]
    public void SchemaDiff_ShowsOneCardPerDifferingDatabase_FoldsTheRest_AndCopiesNothingByItself()
    {
        var vm = new SchemaDiffViewModel("G",
        [
            new SchemaDiff("A", ["logs"], [], []),
            new SchemaDiff("B", [], [], []),
            new SchemaDiff("C", [], ["logs"], []),
        ], []);
        var window = Show(new SchemaDiffDialog { DataContext = vm });

        Assert.True(ShowsText(window, "A"));
        Assert.True(ShowsText(window, "C"));
        Assert.True(ShowsText(window, "logs"));
        Assert.True(ShowsText(window, "Identical to the others: B"));
        Assert.True(ShowsText(window, "1 missing"));
        Assert.NotNull(ButtonNamed(window, "Copy report"));
        Assert.NotNull(ButtonNamed(window, "Close"));
        window.Close();
    }

    [AvaloniaFact]
    public void SchemaDiff_FoldingACard_HidesItsTables()
    {
        var vm = new SchemaDiffViewModel("G", [new SchemaDiff("A", ["logs"], [], []), new SchemaDiff("B", [], ["logs"], [])], []);
        var window = Show(new SchemaDiffDialog { DataContext = vm });
        Assert.True(vm.DifferingMembers[0].IsExpanded);

        vm.DifferingMembers[0].IsExpanded = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == "logs" && t.IsEffectivelyVisible));   // B's only
        window.Close();
    }
}

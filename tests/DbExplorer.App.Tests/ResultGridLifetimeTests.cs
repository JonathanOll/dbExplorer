using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

/// <summary>
/// The tab view-model outlives its views (the TabControl recreates the view on every re-selection), so a view must
/// not stay subscribed to it once detached.
/// </summary>
public class ResultGridLifetimeTests : IDisposable
{
    private readonly TableTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    // PropertyChanged of ObservableObject is a plain field-like event: its delegate's invocation list is the subscriber list.
    private static int Subscribers(object source, string eventName)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field is not null) return (field.GetValue(source) as Delegate)?.GetInvocationList().Length ?? 0;
        }
        return 0;
    }

    [AvaloniaFact]
    public async Task ResultGrid_UnsubscribesFromFeatures_WhenDetached()
    {
        var tab = await _t.OpenTab("orders");
        var window = new Window();
        var grid = new ResultGrid { Columns = tab.GridColumns, ItemsSource = tab.Rows, Features = tab };
        window.Content = grid;
        var baseline = Subscribers(tab, "PropertyChanged");

        window.Show();
        Assert.Equal(baseline + 1, Subscribers(tab, "PropertyChanged"));

        window.Content = null;   // detach
        Assert.Equal(baseline, Subscribers(tab, "PropertyChanged"));

        window.Content = grid;   // re-attach resubscribes exactly once
        Assert.Equal(baseline + 1, Subscribers(tab, "PropertyChanged"));
        window.Close();
    }

    // Sync, non-inlined helpers: no local or async state machine in the test body keeps a view alive for the GC check.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference Select(ContentControl host, TableTabViewModel tab)
    {
        host.Content = tab;   // the content host builds the view from the App's data template (as the TabControl does)
        Dispatcher.UIThread.RunJobs();
        return new WeakReference(host.GetVisualDescendants().OfType<TableTabView>().Single());
    }

    private static void Deselect(ContentControl host)
    {
        host.Content = null;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ReselectingTheTab_DoesNotLeakViewsOrSubscriptions()
    {
        var tab = await _t.OpenTab("orders");
        var host = new ContentControl();
        var window = new Window { Content = host };
        window.Show();
        var baselineProps = Subscribers(tab, "PropertyChanged");
        var baselineFocus = Subscribers(tab, "FocusColumnRequested");
        var weakViews = new List<WeakReference>();
        var rowCollections = new List<object> { tab.Rows };

        for (var i = 0; i < 30; i++)
        {
            weakViews.Add(Select(host, tab));
            Assert.Equal(baselineFocus + 1, Subscribers(tab, "FocusColumnRequested"));   // exactly one live view
            await tab.ToggleSortAsync("id");   // header repaint on the live grid; replaces tab.Rows
            rowCollections.Add(tab.Rows);
            Deselect(host);
        }

        // The view-model (long-lived) must not list any view as a subscriber any more.
        Assert.Equal(baselineProps, Subscribers(tab, "PropertyChanged"));
        Assert.Equal(baselineFocus, Subscribers(tab, "FocusColumnRequested"));
        // The DataGrid never unsubscribes from the collection it was given, so it must only ever see a private mirror.
        Assert.All(rowCollections, c => Assert.Equal(0, Subscribers(c, "CollectionChanged")));

        for (var i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        var alive = weakViews.Count(w => w.IsAlive);
        window.Close();
        // (Avalonia keeps the most recently used control alive via a static; the other 29 must be collected.)
        Assert.True(alive <= 1, $"{alive} of 30 detached TableTabViews are still alive");
    }
}
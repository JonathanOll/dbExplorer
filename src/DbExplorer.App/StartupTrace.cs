using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DbExplorer.App;

/// <summary>
/// Opt-in start-up timing: with env <c>DBEXPLORER_STARTUP_TRACE=&lt;file&gt;</c>, writes the milliseconds from process
/// start to each milestone, the number of JIT-ed methods and the assemblies loaded at the first frame and once idle.
/// Without the variable every call is a null check.
/// </summary>
internal static class StartupTrace
{
    private static readonly string? File = Environment.GetEnvironmentVariable("DBEXPLORER_STARTUP_TRACE");
    private static readonly List<string>? Lines = string.IsNullOrEmpty(File) ? null : [];
    private static double _offset;
    private static Stopwatch? _clock;

    public static void Mark(string name)
    {
        if (Lines is null) return;
        if (_clock is null)
        {
            // The first mark ties the stopwatch to the process start, so runtime start-up is counted too.
            using var process = Process.GetCurrentProcess();
            _offset = (DateTime.Now - process.StartTime).TotalMilliseconds;
            _clock = Stopwatch.StartNew();
        }
        var ms = _offset + _clock.Elapsed.TotalMilliseconds;
        Lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"{name}\t{ms:F1}\tjit={System.Runtime.JitInfo.GetCompiledMethodCount()}"));
    }

    /// <summary>Call after the other <c>Opened</c> handlers are wired, so "interactive" comes after the tab restore.</summary>
    public static void Attach(Window window)
    {
        if (Lines is null) return;
        window.Opened += (_, _) =>
        {
            Mark("opened");
            // The callback of the second animation frame runs once the first frame has been rendered.
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ =>
            {
                Mark("first-frame");
                Assemblies("first-frame");
            }));
            // Posted behind the tab restore (Background); idle priority runs after the layout and render it caused.
            Dispatcher.UIThread.Post(() => Dispatcher.UIThread.Post(() =>
            {
                Mark("interactive");
                Assemblies("interactive");
                // A little later, so that "first-frame" is in the file even when it came after "interactive".
                DispatcherTimer.RunOnce(Write, TimeSpan.FromMilliseconds(500));
            }, DispatcherPriority.ApplicationIdle), DispatcherPriority.Background);
        };
    }

    private static void Assemblies(string when) =>
        Lines!.Add($"assemblies@{when}\t" + string.Join(',',
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Order(StringComparer.Ordinal)));

    private static void Write()
    {
        try { System.IO.File.WriteAllLines(File!, Lines!); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.WriteLine(e.Message); }
    }
}

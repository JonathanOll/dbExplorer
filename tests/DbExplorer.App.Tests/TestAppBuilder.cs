using Avalonia;
using Avalonia.Headless;
using DbExplorer.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace DbExplorer.App.Tests;

/// <summary>Boots the real App (theme, DataGrid styles) on the headless platform for [AvaloniaFact] tests.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DbExplorer.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

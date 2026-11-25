using System.Diagnostics;

namespace DbExplorer.App;

/// <summary>Opens things in the system file manager. Best effort: a failure is reported by the caller (the view models catch).</summary>
internal static class ShellLauncher
{
    /// <summary>Opens a folder.</summary>
    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

    /// <summary>Opens the folder of a file with the file selected (Windows; elsewhere the folder alone).</summary>
    public static void Reveal(string file)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + file }, UseShellExecute = false });
        else if (Path.GetDirectoryName(file) is { } dir) OpenFolder(dir);
    }
}

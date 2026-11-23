using System.Runtime.InteropServices;

namespace DbExplorer.App;

/// <summary>
/// Reports a fatal startup problem (a bad command line) with a plain Win32 message box: no Avalonia, no window, nothing that
/// costs startup time on the normal path (this type is only touched when there is an error).
/// </summary>
internal static class StartupError
{
    private const uint MB_OK = 0x0, MB_ICONERROR = 0x10;

    public static void Show(string message) => MessageBoxW(IntPtr.Zero, message, "dbExplorer", MB_OK | MB_ICONERROR);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

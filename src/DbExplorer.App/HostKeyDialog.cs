using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Tunnels;

namespace DbExplorer.App;

/// <summary>
/// The trust-on-first-use question of an SSH server: its fingerprint, and Cancel as the default so that a reflex Enter
/// never trusts a server.
/// </summary>
internal sealed class HostKeyDialog(Window owner) : IHostKeyPrompt
{
    public Task<bool> ConfirmNewHostAsync(HostKeyInfo key, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ct.IsCancellationRequested || !owner.IsVisible) return false;
            var (window, cancel) = Build(key);
            // Above whatever is open: the question may come from the Test button of the connection dialog.
            var top = owner;
            while (top.OwnedWindows.LastOrDefault(w => w.IsVisible) is { } child) top = child;
            window.Opened += (_, _) => cancel.Focus();
            using var cancelled = ct.Register(() => Dispatcher.UIThread.Post(() => window.Close(false)));
            return await window.ShowDialog<bool>(top);
        });

    /// <summary>The dialog and its Cancel button (separate from showing it, for tests and screenshots).</summary>
    internal static (Window Window, Button Cancel) Build(HostKeyInfo key)
    {
        var intro = new TextBlock
        {
            Text = $"{key.Host}:{key.Port} is a new SSH server for dbExplorer.", FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var algorithm = new TextBlock { Text = $"Key fingerprint ({key.Algorithm})", Margin = new Avalonia.Thickness(0, 12, 0, 4) };
        algorithm.Classes.Add("dim");
        var fingerprint = new SelectableTextBlock
        {
            Name = "Fingerprint", Text = key.Fingerprint, TextWrapping = TextWrapping.Wrap,
        };
        fingerprint.Bind(SelectableTextBlock.FontFamilyProperty, fingerprint.GetResourceObservable("DbxMonoFont"));
        var advice = new TextBlock
        {
            Text = "Check it with the server's administrator. If you trust it, it is stored and the connection is refused should it ever change.",
            TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        advice.Classes.Add("dim");
        var body = new StackPanel { Width = 400, Children = { intro, algorithm, fingerprint, advice } };

        var trust = DialogShell.Button("Trust and connect");
        var cancel = DialogShell.Button("Cancel", isDefault: true, isCancel: true);
        var window = DialogShell.Create("Unknown SSH server", "IconWarning", body, [trust, cancel], tone: DialogTone.Warning);
        trust.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        return (window, cancel);
    }
}

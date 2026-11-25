using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;

namespace DbExplorer.App;

/// <summary>Real <see cref="IDialogService"/>: modal windows owned by the main window.</summary>
public sealed class DialogService(Window owner) : IDialogService
{
    public async Task<bool> ShowConnectionDialogAsync(ConnectionDialogViewModel viewModel)
    {
        var dialog = new ConnectionDialog { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(owner);
    }

    public Task<string?> PromptTextAsync(string title, string label, string initial) =>
        PromptTextAsync(new PromptRequest(title, label, initial, "Save"));

    public async Task<string?> PromptTextAsync(PromptRequest request)
    {
        var box = new TextBox { Text = request.Initial, MinWidth = 300, Watermark = request.Placeholder };
        var ok = DialogShell.Button(request.OkText, isDefault: true, primary: true);
        var cancel = DialogShell.Button("Cancel", isCancel: true);
        var heading = new TextBlock { Text = request.Label, Margin = new Thickness(0, 0, 0, 6) };
        heading.Classes.Add("dim");
        var problem = new TextBlock { Margin = new Thickness(0, 6, 0, 0), IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380 };
        problem.Classes.Add("fielderror");
        var window = DialogShell.Create(request.Title, "IconFolderAdd", new StackPanel { Children = { heading, box, problem } }, [ok, cancel]);

        // Validation runs on every edit: the button is disabled while the text is refused; the reason shows once the user has typed.
        void Check(bool show)
        {
            var message = request.Validate?.Invoke(box.Text ?? "");
            ok.IsEnabled = message is null;
            problem.Text = message;
            problem.IsVisible = show && message is not null;
            if (show && message is not null) box.Classes.Add("error");
            else box.Classes.Remove("error");
        }
        box.TextChanged += (_, _) => Check(true);
        Check(false);

        ok.Click += (_, _) => window.Close(box.Text);
        cancel.Click += (_, _) => window.Close(null);
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await window.ShowDialog<string?>(owner);
    }

    public async Task<bool> ShowGroupEditorAsync(GroupEditorViewModel viewModel)
    {
        var dialog = new GroupEditorDialog { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task ShowSchemaDiffAsync(SchemaDiffViewModel viewModel)
    {
        var dialog = new SchemaDiffDialog { DataContext = viewModel };
        await dialog.ShowDialog(owner);
    }

    public async Task<int?> ChooseAsync(string title, string label, IReadOnlyList<string> options)
    {
        var heading = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        heading.Classes.Add("dim");
        var list = new ListBox { ItemsSource = options, SelectedIndex = 0, MinWidth = 300, MaxHeight = 240 };
        var ok = DialogShell.Button("Select", isDefault: true, primary: true);
        var cancel = DialogShell.Button("Cancel", isCancel: true);
        var window = DialogShell.Create(title, "IconGroup", new StackPanel { Children = { heading, list } }, [ok, cancel]);
        ok.Click += (_, _) => window.Close(list.SelectedIndex >= 0 ? list.SelectedIndex : null);
        cancel.Click += (_, _) => window.Close(null);
        list.DoubleTapped += (_, _) => { if (list.SelectedIndex >= 0) window.Close(list.SelectedIndex); };
        window.Opened += (_, _) => list.Focus();
        return await window.ShowDialog<int?>(owner);
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        ConfirmAsync(new ConfirmRequest(title, message, "Yes", null, DialogTone.Warning));

    public async Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        // A destructive confirmation defaults to Cancel: Enter on a reflex must not delete anything.
        var danger = request.Tone == DialogTone.Danger;
        var confirm = DialogShell.Button(request.ConfirmText, isDefault: !danger, primary: !danger, danger: danger);
        var cancel = DialogShell.Button("Cancel", isDefault: danger, isCancel: true);
        var icon = request.Tone == DialogTone.Info ? "IconInfo" : "IconWarning";
        var window = DialogShell.Create(request.Title, icon, DialogShell.Message(request.Message, request.Detail), [confirm, cancel], tone: request.Tone);
        confirm.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        if (danger) window.Opened += (_, _) => cancel.Focus();
        return await window.ShowDialog<bool>(owner);
    }
}

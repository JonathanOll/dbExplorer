using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DbExplorer.App.Views;

/// <summary>One statement of the preview: its verb (UPDATE, DELETE, INSERT...) tells the user at a glance what kind of change it is.</summary>
public sealed record StatementItem(string Sql)
{
    /// <summary>The leading keyword in capitals; a statement the preview does not recognise is shown as SQL.</summary>
    public string Kind { get; } = KindOf(Sql);

    public bool IsUpdate => Kind == "UPDATE";
    public bool IsDelete => Kind == "DELETE";
    public bool IsInsert => Kind == "INSERT";

    private static string KindOf(string sql)
    {
        var span = sql.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && char.IsLetter(span[end])) end++;
        return end == 0 ? "SQL" : span[..end].ToString().ToUpperInvariant();
    }
}

/// <summary>Lists the statements a save would run (display only) with their count and kinds; Apply or Cancel.</summary>
public partial class SqlPreviewDialog : Window
{
    private readonly IReadOnlyList<string> _statements = [];

    // Cancel is the default and focused button: a stray Enter or Space must never execute writes. Apply is a deliberate click, Tab
    // or Ctrl+Enter.
    public SqlPreviewDialog()
    {
        InitializeComponent();
        Opened += (_, _) => CancelButton.Focus();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public SqlPreviewDialog(IReadOnlyList<string> statements, string target) : this()
    {
        _statements = statements;
        Title = $"Review changes · {target}";
        Target.Text = target;
        Summary.Text = (statements.Count == 1 ? "1 statement will run" : $"{statements.Count} statements will run") + $" on {target}";
        var items = statements.Select(s => new StatementItem(s)).ToList();
        Breakdown.Text = string.Join(" · ", new[]
        {
            Count(items, "DELETE", "delete", "deletes"), Count(items, "UPDATE", "update", "updates"), Count(items, "INSERT", "insert", "inserts"),
        }.Where(t => t is not null));
        Statements.ItemsSource = items;
    }

    private static string? Count(List<StatementItem> items, string kind, string one, string many)
    {
        var n = items.Count(i => i.Kind == kind);
        return n == 0 ? null : n == 1 ? $"1 {one}" : $"{n} {many}";
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.Control) return;
        e.Handled = true;
        Close(true);
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, _statements));
            CopyButton.Content = "Copied";
        }
        catch (Exception)
        {
            // a clipboard that is busy must not break the review
        }
    }
}

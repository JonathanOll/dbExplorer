namespace DbExplorer.App.ViewModels;

/// <summary>The table (or view) a column belongs to, shared by all its columns.</summary>
/// <param name="Table">"schema.table": shown as the parent of the column.</param>
/// <param name="Context">The connection, in the search across connections; null otherwise.</param>
/// <param name="Open">Opens the table positioned on the column of that name.</param>
public sealed record QuickOpenColumnOwner(string Table, string? Context, string? Group, Func<string, Task> Open)
{
    /// <summary>What a qualified query ("orders.total") is matched against, before the column name.</summary>
    public Core.Model.DbEnvironment Environment { get; init; }

    public string Prefix { get; } = Context is null ? Table : $"{Context} · {Table}";
}

/// <summary>A searchable column. A struct: there are tens of times more columns than tables, rows are built for the winners only.</summary>
public readonly record struct QuickOpenColumn(QuickOpenColumnOwner Owner, string Name);

/// <summary>What one scope of the quick open searches: its rows, and its columns (listed when first searched).</summary>
internal sealed class QuickOpenSource(QuickOpenItem[] items, Lazy<QuickOpenColumn[]>? columns = null)
{
    public QuickOpenItem[] Items { get; private set; } = items;

    public bool HasColumns => columns is not null;

    public QuickOpenColumn[] Columns => columns?.Value ?? [];

    /// <summary>Raised when rows arrive after the list was opened.</summary>
    public event Action? Changed;

    public void Append(IEnumerable<QuickOpenItem> more)
    {
        Items = [.. Items, .. more];
        Changed?.Invoke();
    }
}

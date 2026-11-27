namespace DbExplorer.App.ViewModels;

/// <summary>The table (or view) a column belongs to, shared by all its columns.</summary>
/// <param name="Table">"schema.table": shown as the parent of the column.</param>
/// <param name="Context">The connection, in the search across connections; null otherwise.</param>
/// <param name="Open">Opens the table positioned on the column of that name.</param>
public sealed record QuickOpenColumnOwner(string Table, string? Context, string? Group, Func<string, Task> Open)
{
    public Core.Model.DbEnvironment Environment { get; init; }

    /// <summary>The connection the table lives in; null when the list names none.</summary>
    public QuickOpenScope? Scope { get; init; }

    /// <summary>What identifies the column's row ("connection · schema.table"), before the column name.</summary>
    public string Prefix { get; } = Context is null ? Table : $"{Group} · {Table}";
}

/// <summary>A searchable column. A struct: there are tens of times more columns than tables, rows are built for the winners only.</summary>
public readonly record struct QuickOpenColumn(QuickOpenColumnOwner Owner, string Name);

/// <summary>The rows of the other connections, and the connections that have nothing cached to search.</summary>
internal sealed record QuickOpenElsewhereRows(QuickOpenItem[] Items, Lazy<QuickOpenColumn[]> Columns, IReadOnlyList<QuickOpenScope> Uncached);

/// <summary>How the search of one connection can be extended.</summary>
/// <param name="Here">The searched connection.</param>
/// <param name="Places">The other connections: their names are known without reading anything.</param>
/// <param name="Read">Reads their cached objects (off the UI thread).</param>
internal sealed record QuickOpenExtension(QuickOpenScope Here, IReadOnlyList<QuickOpenScope> Places, Func<QuickOpenElsewhereRows> Read);

/// <summary>What one scope of the quick open searches: its rows, and its columns (listed when first searched).</summary>
internal sealed class QuickOpenSource(QuickOpenItem[] items, Lazy<QuickOpenColumn[]>? columns = null)
{
    private QuickOpenItem[]? _allItems;
    private QuickOpenColumn[]? _allColumns;

    public QuickOpenItem[] Items { get; private set; } = items;

    public bool HasColumns => columns is not null;

    public QuickOpenColumn[] Columns => columns?.Value ?? [];

    /// <summary>Raised when rows arrive after the list was opened.</summary>
    public event Action? Changed;

    /// <summary>Null for a list that cannot be extended (one scope, or already every connection).</summary>
    public QuickOpenExtension? Extension { get; set; }

    /// <summary>The rows of the other connections, once read.</summary>
    public QuickOpenElsewhereRows? Elsewhere { get; private set; }

    /// <summary>The read of <see cref="Elsewhere"/>, started once.</summary>
    public Task? Reading { get; set; }

    public QuickOpenItem[] AllItems => Elsewhere is null ? Items : _allItems ??= [.. Items, .. Elsewhere.Items];

    public QuickOpenColumn[] AllColumns => Elsewhere is null ? Columns : _allColumns ??= [.. Columns, .. Elsewhere.Columns.Value];

    /// <summary>Whether a row or column of this list (not of the others) contains the word in its own or its table's name.</summary>
    public bool Mentions(string word) =>
        Items.Any(i => i.DisplayLabel.Contains(word, StringComparison.OrdinalIgnoreCase))
        || Columns.Any(c => c.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    public void Append(IEnumerable<QuickOpenItem> more)
    {
        Items = [.. Items, .. more];
        _allItems = null;
        Changed?.Invoke();
    }

    public void Extend(QuickOpenElsewhereRows rows)
    {
        Elsewhere = rows;
        Changed?.Invoke();
    }
}

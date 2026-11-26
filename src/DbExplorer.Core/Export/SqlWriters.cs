using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Export;

/// <summary>One <c>INSERT</c> per row; identifiers quoted by the engine's driver, values by <see cref="SqlLiterals"/>.</summary>
public sealed class SqlInsertWriter(TextWriter output, DbKind kind, TableRef table) : RowWriter
{
    private readonly IDbDriver _driver = DriverFactory.For(kind);
    private string _prefix = "";
    private int _columns;

    public override void Begin(IReadOnlyList<ExportColumn> columns)
    {
        _columns = columns.Count;
        _prefix = $"INSERT INTO {_driver.QuoteTable(table)} ({string.Join(", ", columns.Select(c => _driver.Quote(c.Name)))}) VALUES (";
    }

    public override void Write(object?[] row)
    {
        output.Write(_prefix);
        for (var i = 0; i < _columns; i++)
        {
            if (i > 0) output.Write(", ");
            output.Write(SqlLiterals.Format(kind, Value(row, i)));
        }
        output.Write(");\n");
    }
}

/// <summary>One <c>UPDATE</c> per row: every column that is not part of the key is set, the key identifies the row.</summary>
public sealed class SqlUpdateWriter(TextWriter output, DbKind kind, TableRef table, IReadOnlyList<string> keyColumns) : RowWriter
{
    private readonly IDbDriver _driver = DriverFactory.For(kind);
    private IReadOnlyList<ExportColumn> _columns = [];
    private int[] _keys = [];
    private int[] _sets = [];

    public override void Begin(IReadOnlyList<ExportColumn> columns)
    {
        if (keyColumns.Count == 0) throw new ExportException("UPDATE needs at least one key column.");
        _columns = columns;
        _keys = keyColumns.Select(name =>
        {
            for (var i = 0; i < columns.Count; i++)
                if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new ExportException($"The key column '{name}' is not among the exported columns.");
        }).ToArray();
        _sets = Enumerable.Range(0, columns.Count).Except(_keys).ToArray();
        if (_sets.Length == 0) throw new ExportException("Nothing to update: every exported column is part of the key.");
    }

    public override void Write(object?[] row)
    {
        output.Write($"UPDATE {_driver.QuoteTable(table)} SET ");
        output.Write(string.Join(", ", _sets.Select(i => $"{_driver.Quote(_columns[i].Name)} = {SqlLiterals.Format(kind, Value(row, i))}")));
        output.Write(" WHERE ");
        output.Write(string.Join(" AND ", _keys.Select(i => Value(row, i) is { } v
            ? $"{_driver.Quote(_columns[i].Name)} = {SqlLiterals.Format(kind, v)}"
            : $"{_driver.Quote(_columns[i].Name)} IS NULL")));
        output.Write(";\n");
    }
}

/// <summary><c>IN (...)</c> of the first column. NULLs are left out: <c>IN</c> never matches them.</summary>
public sealed class InListWriter(TextWriter output, DbKind kind) : RowWriter
{
    private bool _any;

    public override void Begin(IReadOnlyList<ExportColumn> columns) => output.Write("IN (");

    public override void Write(object?[] row)
    {
        if (Value(row, 0) is not { } value) return;
        if (_any) output.Write(", ");
        _any = true;
        output.Write(SqlLiterals.Format(kind, value));
    }

    public override void End() => output.Write(_any ? ")" : "NULL)");
}

/// <summary>The column names alone, comma separated.</summary>
public sealed class ColumnNamesWriter(TextWriter output) : RowWriter
{
    public override void Begin(IReadOnlyList<ExportColumn> columns) => output.Write(string.Join(", ", columns.Select(c => c.Name)));

    public override void Write(object?[] row) { }
}

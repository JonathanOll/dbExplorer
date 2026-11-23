using System.Globalization;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Querying;

public abstract record RowEdit;

/// <summary><paramref name="Key"/> holds the ORIGINAL values of the key columns; <paramref name="Changes"/> the new values.</summary>
public sealed record UpdateRow(
    IReadOnlyDictionary<string, object?> Key,
    IReadOnlyDictionary<string, object?> Changes) : RowEdit;

public sealed record InsertRow(IReadOnlyDictionary<string, object?> Values) : RowEdit;

public sealed record DeleteRow(IReadOnlyDictionary<string, object?> Key) : RowEdit;

/// <summary>
/// <see cref="Command"/> is what gets executed (values bound as parameters). <see cref="PreviewSql"/> has the
/// values inlined and escaped: it is for display only and must never be executed.
/// <see cref="ExpectsSingleRow"/> is set on UPDATE/DELETE (planned from one grid row): the applier fails closed
/// unless exactly one row is affected.
/// </summary>
public sealed record PlannedStatement(SqlCommandSpec Command, string PreviewSql, bool ExpectsSingleRow = false);

/// <summary>An edit refers to an unknown column or carries a value that is not valid for the column type.</summary>
public sealed class InvalidEditException(string column, string message)
    : Exception($"Column '{column}': {message}")
{
    public string Column { get; } = column;
}

public static class EditPlanner
{
    /// <summary>
    /// Turns staged edits into one statement each, in the order given. Identifiers are quoted by the driver and
    /// every non-NULL value is bound as a parameter (NULL is written as the literal NULL so its type is never
    /// inferred by the server). Edited text values are converted to the column's CLR type; key values are bound
    /// exactly as given, since they are the values read back from the database.
    /// An UPDATE or DELETE whose key is empty or incomplete is rejected: it would touch unintended rows.
    /// An <see cref="UpdateRow"/> without changes produces no statement.
    /// </summary>
    public static IReadOnlyList<PlannedStatement> Plan(
        IDbDriver d, TableInfo t, IReadOnlyList<string> keyColumns, IReadOnlyList<RowEdit> edits)
    {
        if (t.IsView) throw new InvalidOperationException($"{t.Ref} is a view and cannot be edited.");

        var plan = new List<PlannedStatement>();
        foreach (var edit in edits)
        {
            var statement = edit switch
            {
                UpdateRow u => PlanUpdate(d, t, keyColumns, u),
                InsertRow i => PlanInsert(d, t, i),
                DeleteRow del => PlanDelete(d, t, keyColumns, del),
                _ => throw new ArgumentException($"Unknown edit type {edit.GetType().Name}.", nameof(edits)),
            };
            if (statement is not null) plan.Add(statement);
        }
        return plan;
    }

    private static PlannedStatement? PlanUpdate(IDbDriver d, TableInfo t, IReadOnlyList<string> keyColumns, UpdateRow u)
    {
        if (u.Changes.Count == 0) return null;
        var sets = u.Changes.Select(kv =>
        {
            var column = FindColumn(t, kv.Key);
            return (Column: column, Value: Coerce(column, kv.Value));
        }).ToList();
        var key = ResolveKey(t, keyColumns, u.Key);

        return Build(d, render => string.Concat(
            "UPDATE ", d.QuoteTable(t.Ref),
            " SET ", string.Join(", ", sets.Select(s => $"{d.Quote(s.Column.Name)} = {Value(render, s.Value, s.Column)}")),
            " WHERE ", Where(d, key, render)), expectsSingleRow: true);
    }

    private static PlannedStatement PlanDelete(IDbDriver d, TableInfo t, IReadOnlyList<string> keyColumns, DeleteRow del)
    {
        var key = ResolveKey(t, keyColumns, del.Key);
        return Build(d, render => $"DELETE FROM {d.QuoteTable(t.Ref)} WHERE {Where(d, key, render)}", expectsSingleRow: true);
    }

    private static PlannedStatement PlanInsert(IDbDriver d, TableInfo t, InsertRow i)
    {
        var values = i.Values.Select(kv =>
        {
            var column = FindColumn(t, kv.Key);
            return (Column: column, Value: Coerce(column, kv.Value));
        }).ToList();

        if (values.Count == 0)
            return Build(d, _ => $"INSERT INTO {d.QuoteTable(t.Ref)} DEFAULT VALUES");

        return Build(d, render => string.Concat(
            "INSERT INTO ", d.QuoteTable(t.Ref),
            " (", string.Join(", ", values.Select(v => d.Quote(v.Column.Name))), ")",
            " VALUES (", string.Join(", ", values.Select(v => Value(render, v.Value, v.Column))), ")"));
    }

    /// <summary>
    /// Renders the statement twice with the same code: once binding parameters, once inlining literals, so the
    /// two can never disagree and a parameter-looking identifier is never rewritten by mistake.
    /// </summary>
    private static PlannedStatement Build(
        IDbDriver d, Func<Func<object, ColumnInfo?, string>, string> render, bool expectsSingleRow = false)
    {
        var parameters = new List<SqlParam>();
        var sql = render((value, column) =>
        {
            var name = d.ParamPrefix + "p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new SqlParam(name, value, column));
            return name;
        });
        var preview = render((value, _) => Literal(d.Kind, value));
        return new PlannedStatement(new SqlCommandSpec(sql, parameters), preview, expectsSingleRow);
    }

    private static string Value(Func<object, ColumnInfo?, string> render, object? value, ColumnInfo column) =>
        value is null ? "NULL" : render(value, column);

    private static string Where(
        IDbDriver d, List<(ColumnInfo Column, object? Value)> key, Func<object, ColumnInfo?, string> render) =>
        string.Join(" AND ", key.Select(k => k.Value is null
            ? $"{d.Quote(k.Column.Name)} IS NULL" // "= NULL" never matches
            : $"{d.Quote(k.Column.Name)} = {render(k.Value, k.Column)}"));

    private static List<(ColumnInfo Column, object? Value)> ResolveKey(
        TableInfo t, IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, object?> key)
    {
        if (keyColumns.Count == 0)
            throw new ArgumentException($"No key columns for {t.Ref}: refusing an UPDATE/DELETE without WHERE.", nameof(keyColumns));

        var resolved = new List<(ColumnInfo, object?)>();
        foreach (var name in keyColumns)
        {
            var column = FindColumn(t, name);
            if (!TryGetValue(key, column.Name, out var value) && !TryGetValue(key, name, out value))
                throw new ArgumentException($"No value given for key column '{column.Name}' of {t.Ref}.", nameof(key));
            resolved.Add((column, value));
        }
        return resolved;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, object?> dict, string name, out object? value)
    {
        if (dict.TryGetValue(name, out value)) return true;
        foreach (var kv in dict)
        {
            if (!string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = kv.Value;
            return true;
        }
        value = null;
        return false;
    }

    private static ColumnInfo FindColumn(TableInfo t, string name) =>
        t.Columns.FirstOrDefault(c => c.Name == name)
        ?? t.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidEditException(name, $"column does not exist in {t.Ref}.");

    /// <summary>
    /// Text typed in the grid becomes the column's CLR type (the SQLite driver reports DATETIME/BOOLEAN columns
    /// as DateTime/bool although they are read back as text/integers). Non-text values are bound as given.
    /// </summary>
    private static object? Coerce(ColumnInfo column, object? value)
    {
        if (value is not string text) return value;
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        if (type == typeof(string)) return text;
        if (type == typeof(byte[])) throw new InvalidEditException(column.Name, "binary values cannot be edited as text.");
        try
        {
            return TableQueryBuilder.ConvertValue(column, text);
        }
        catch (FilterException)
        {
            throw new InvalidEditException(column.Name, $"'{text}' is not a valid {type.Name} value.");
        }
    }

    /// <summary>Renders a value as a SQL literal for display. Never executed.</summary>
    private static string Literal(DbKind kind, object value)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (value)
        {
            // SQL Server: without N a copied preview turns non-ASCII text into '?' in a script (display only, never executed).
            case string s: return kind == DbKind.SqlServer ? "N" + Quoted(s) : Quoted(s);
            case bool b: return kind == DbKind.Postgres ? (b ? "TRUE" : "FALSE") : (b ? "1" : "0");
            case DateTime dt: return Quoted(dt.ToString("yyyy-MM-dd HH:mm:ss.fff", inv));
            case DateTimeOffset dto: return Quoted(dto.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", inv));
            case TimeSpan ts: return Quoted(ts.ToString("c", inv));
            case Guid g: return Quoted(g.ToString());
            case byte[] bytes:
                var hex = Convert.ToHexString(bytes);
                return kind switch
                {
                    DbKind.SqlServer => "0x" + hex,
                    DbKind.Postgres => $"'\\x{hex}'",
                    _ => $"X'{hex}'",
                };
            case double dbl: return dbl.ToString("R", inv);
            case float f: return f.ToString("R", inv);
            case IFormattable formattable: return formattable.ToString(null, inv);
            default: return Quoted(Convert.ToString(value, inv) ?? "");
        }
    }

    private static string Quoted(string s) => "'" + s.Replace("'", "''") + "'";
}

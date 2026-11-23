using System.Globalization;
using System.Text;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Querying;

public record ColumnFilter(string Column, string Text);

public record SortSpec(string Column, bool Descending);

/// <summary>
/// A page request on a table or view. <paramref name="RawWhere"/> is user-typed SQL appended verbatim (inside
/// parentheses): it is the only text in a built command that is not quoted or parameterised.
/// </summary>
public record TableQuery(
    TableInfo Table,
    IReadOnlyList<ColumnFilter> Filters,
    string? RawWhere,
    IReadOnlyList<SortSpec> Sort,
    int Offset,
    int Limit);

/// <summary>
/// <see cref="Name"/> includes the driver's parameter prefix, exactly as it appears in the SQL. <see cref="Column"/> is the
/// column the value is compared with or written to (null for LIKE patterns): <see cref="ParameterTyping"/> uses it to give
/// date/time parameters their exact database type instead of the provider's inference.
/// </summary>
public record SqlParam(string Name, object? Value, ColumnInfo? Column = null);

public record SqlCommandSpec(string Sql, IReadOnlyList<SqlParam> Params);

/// <summary>A column filter that cannot be applied (unknown column, value not valid for the column type).</summary>
public sealed class FilterException(string column, string message)
    : Exception($"Filter on column '{column}': {message}")
{
    public string Column { get; } = column;
}

/// <summary>
/// Builds the SELECT and COUNT commands for a table view. Filter values are always bound as parameters; only
/// identifiers are interpolated, and always quoted through the driver.
/// </summary>
public static class TableQueryBuilder
{
    public static SqlCommandSpec Build(IDbDriver d, TableQuery q)
    {
        var (from, parameters) = BuildFromWhere(d, q);
        var select = "SELECT * " + from;
        return new SqlCommandSpec(d.PageSql(select, BuildOrderBy(d, q), q.Offset, q.Limit), parameters);
    }

    public static SqlCommandSpec BuildCount(IDbDriver d, TableQuery q)
    {
        var (from, parameters) = BuildFromWhere(d, q);
        return new SqlCommandSpec("SELECT COUNT(*) " + from, parameters);
    }

    private static (string Sql, List<SqlParam> Params) BuildFromWhere(IDbDriver d, TableQuery q)
    {
        var parameters = new List<SqlParam>();
        var predicates = new List<string>();

        foreach (var filter in q.Filters)
        {
            var expr = FilterParser.Parse(filter.Text);
            if (expr is null) continue;
            var column = FindColumn(q.Table, filter.Column);
            predicates.Add(BuildPredicate(d, column, expr, parameters));
        }
        if (!string.IsNullOrWhiteSpace(q.RawWhere)) predicates.Add($"({q.RawWhere}\n)"); // own line: a trailing "-- comment" must not swallow the ")"

        var sql = "FROM " + d.QuoteTable(q.Table.Ref);
        if (predicates.Count > 0) sql += " WHERE " + string.Join(" AND ", predicates);
        return (sql, parameters);
    }

    private static string BuildPredicate(IDbDriver d, ColumnInfo column, FilterExpr expr, List<SqlParam> parameters)
    {
        var col = d.Quote(column.Name);

        string Bind(object? value, ColumnInfo? typedAs = null)
        {
            var name = d.ParamPrefix + "p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new SqlParam(name, value, typedAs));
            return name;
        }

        object Typed(string text) => ConvertValue(column, text);

        return expr.Op switch
        {
            FilterOp.IsNull => $"{col} IS NULL",
            FilterOp.NotNull => $"{col} IS NOT NULL",
            FilterOp.Contains =>
                $"{d.CastToText(col)} LIKE {Bind("%" + d.EscapeLike(expr.Values[0]) + "%")} ESCAPE '\\'",
            FilterOp.Eq => $"{col} = {Bind(Typed(expr.Values[0]), column)}",
            FilterOp.Ne => $"{col} <> {Bind(Typed(expr.Values[0]), column)}",
            FilterOp.Lt => $"{col} < {Bind(Typed(expr.Values[0]), column)}",
            FilterOp.Gt => $"{col} > {Bind(Typed(expr.Values[0]), column)}",
            FilterOp.Le => $"{col} <= {Bind(Typed(expr.Values[0]), column)}",
            FilterOp.Ge => $"{col} >= {Bind(Typed(expr.Values[0]), column)}",
            FilterOp.In => $"{col} IN ({string.Join(", ", expr.Values.Select(v => Bind(Typed(v), column)))})",
            _ => throw new ArgumentOutOfRangeException(nameof(expr), expr.Op, "Unknown filter operator."),
        };
    }

    private static string BuildOrderBy(IDbDriver d, TableQuery q)
    {
        var terms = q.Sort.Select(s => d.Quote(s.Column) + (s.Descending ? " DESC" : " ASC")).ToList();

        // Primary key as tiebreaker (after any explicit sort) so OFFSET paging is stable.
        terms.AddRange(q.Table.Columns
            .Where(c => c.IsPrimaryKey && !q.Sort.Any(s => string.Equals(s.Column, c.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Ordinal)
            .Select(c => d.Quote(c.Name)));
        if (terms.Count > 0) return string.Join(", ", terms);

        // SQL Server requires an ORDER BY for OFFSET/FETCH; (SELECT NULL) keeps the engine's natural order.
        return d.Kind == DbKind.SqlServer ? "(SELECT NULL)" : "";
    }

    private static ColumnInfo FindColumn(TableInfo table, string name) =>
        table.Columns.FirstOrDefault(c => c.Name == name)
        ?? table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new FilterException(name, $"column does not exist in {table.Ref}.");

    /// <summary>Converts filter text to the column's CLR type so the driver binds a correctly typed parameter.</summary>
    internal static object ConvertValue(ColumnInfo column, string text)
    {
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        var inv = CultureInfo.InvariantCulture;
        object? result = null;
        bool ok;

        if (type == typeof(string)) return text;
        else if (type == typeof(bool))
        {
            ok = TryParseBool(text, out var v);
            result = v;
        }
        else if (type == typeof(Guid))
        {
            ok = Guid.TryParse(text, out var v);
            result = v;
        }
        else if (type == typeof(DateTime))
        {
            // PostgreSQL timestamptz: typed text is UTC (the driver reads such values as UTC and writes them back as UTC), so
            // the server never reinterprets it in the session time zone. An explicit offset in the text is honoured.
            var utc = column.DataType.Contains("with time zone", StringComparison.OrdinalIgnoreCase)
                      && column.DataType.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase);
            ok = DateTime.TryParse(text, inv,
                utc ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.None, out var v);
            result = v;
        }
        else if (type == typeof(DateTimeOffset))
        {
            ok = DateTimeOffset.TryParse(text, inv, DateTimeStyles.None, out var v);
            result = v;
        }
        else if (type == typeof(TimeSpan))
        {
            ok = TimeSpan.TryParse(text, inv, out var v);
            result = v;
        }
        else if (type == typeof(byte[]))
            throw new FilterException(column.Name, "binary columns cannot be filtered on a value.");
        else if (IsNumeric(type))
        {
            // Explicit styles: no thousands separator (a French "1,5" must fail, not become 15) and no NaN/Infinity.
            ok = TryParseNumber(text, type, out result);
        }
        else return text; // Unknown CLR type: let the server coerce the text.

        if (!ok)
            throw new FilterException(column.Name, $"'{text}' is not a valid {type.Name} value.");
        return result!;
    }

    private static bool TryParseNumber(string text, Type type, out object? value)
    {
        var inv = CultureInfo.InvariantCulture;
        const NumberStyles Int = NumberStyles.Integer;
        const NumberStyles Fp = NumberStyles.Float;
        bool ok;
        value = null;
        if (type == typeof(int)) { ok = int.TryParse(text, Int, inv, out var v); value = v; }
        else if (type == typeof(long)) { ok = long.TryParse(text, Int, inv, out var v); value = v; }
        else if (type == typeof(short)) { ok = short.TryParse(text, Int, inv, out var v); value = v; }
        else if (type == typeof(byte)) { ok = byte.TryParse(text, Int, inv, out var v); value = v; }
        else if (type == typeof(sbyte)) { ok = sbyte.TryParse(text, Int, inv, out var v); value = v; }
        else if (type == typeof(uint)) { ok = uint.TryParse(text, Int, inv, out var v); value = v; }
        else if (type == typeof(ulong)) { ok = ulong.TryParse(text, Int, inv, out var v); value = v; }
        else if (type == typeof(ushort)) { ok = ushort.TryParse(text, Int, inv, out var v); value = v; }
        else if (type == typeof(decimal)) { ok = decimal.TryParse(text, Fp, inv, out var v); value = v; }
        else if (type == typeof(double)) { ok = double.TryParse(text, Fp, inv, out var v) && double.IsFinite(v); value = v; }
        else { ok = float.TryParse(text, Fp, inv, out var v) && float.IsFinite(v); value = v; }
        return ok;
    }

    private static bool IsNumeric(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
        || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort)
        || t == typeof(decimal) || t == typeof(double) || t == typeof(float);

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "1": value = true; return true;
            case "false" or "0": value = false; return true;
            default: value = false; return false;
        }
    }
}

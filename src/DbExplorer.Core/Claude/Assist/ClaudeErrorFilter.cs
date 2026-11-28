using System.Text.RegularExpressions;

namespace DbExplorer.Core.Claude.Assist;

/// <summary>
/// What of an error message may be sent for a schema-only connection. Messages can quote the rows of a table, so nothing
/// is passed on by default: only the lines of a known shape, in which names stay and values are replaced by "?".
/// </summary>
public static class ClaudeErrorFilter
{
    public const string Withheld = "(The rest of the error message is withheld: it may quote data, and this connection is schema only.)";

    // {n} a name of the database: kept. {t} a piece of the user's SQL: kept when the SQL as sent holds it. {v} a value: hidden.
    private static readonly Regex[] Shapes = new[]
    {
        // SQL Server
        "Invalid column name '{n}'.",
        "Invalid object name '{n}'.",
        "Incorrect syntax near '{t}'.",
        "Incorrect syntax near '{t}'. Expecting {n}.",
        "Incorrect syntax near the keyword '{t}'.",
        "Ambiguous column name '{n}'.",
        "Could not find stored procedure '{n}'.",
        "The multi-part identifier \"{n}\" could not be bound.",
        "Column '{n}' is invalid in the select list because it is not contained in either an aggregate function or the GROUP BY clause.",
        "There is already an object named '{n}' in the database.",
        "Divide by zero error encountered.",
        "Conversion failed when converting the {n} value '{v}' to data type {n}.",
        "Conversion failed when converting date and/or time from character string.",
        "Error converting data type {n} to {n}.",
        "Arithmetic overflow error converting {n} to data type {n}.",
        "Must declare the scalar variable \"{n}\".",
        "Subquery returned more than 1 value. This is not permitted when the subquery follows =, !=, <, <= , >, >= or when the subquery is used as an expression.",
        "Violation of {n} KEY constraint '{n}'. Cannot insert duplicate key in object '{n}'. The duplicate key value is ({v}).",
        "Cannot insert duplicate key row in object '{n}' with unique index '{n}'. The duplicate key value is ({v}).",
        "Cannot insert the value NULL into column '{n}', table '{n}'; column does not allow nulls. {n} fails.",
        "The {n} statement conflicted with the {n} constraint \"{n}\". The conflict occurred in database \"{n}\", table \"{n}\", column '{n}'.",
        "String or binary data would be truncated in table '{n}', column '{n}'. Truncated value: '{v}'.",
        "String or binary data would be truncated.",
        "The statement has been terminated.",
        // PostgreSQL
        "column \"{n}\" does not exist",
        "column {n} does not exist",
        "relation \"{n}\" does not exist",
        "syntax error at or near \"{t}\"",
        "syntax error at end of input",
        "function {n}({n}) does not exist",
        "operator does not exist: {n} {t} {n}",
        "column \"{n}\" must appear in the GROUP BY clause or be used in an aggregate function",
        "column reference \"{n}\" is ambiguous",
        "invalid input syntax for type {n}: \"{v}\"",
        "division by zero",
        "duplicate key value violates unique constraint \"{n}\"",
        "null value in column \"{n}\" of relation \"{n}\" violates not-null constraint",
        "insert or update on table \"{n}\" violates foreign key constraint \"{n}\"",
        "new row for relation \"{n}\" violates check constraint \"{n}\"",
        "value too long for type {n}({n})",
        "permission denied for table {n}",
        // SQLite
        "no such column: {n}",
        "no such table: {n}",
        "no such function: {n}",
        "near \"{t}\": syntax error",
        "ambiguous column name: {n}",
        "UNIQUE constraint failed: {n}",
        "UNIQUE constraint failed: {n}, {n}",
        "NOT NULL constraint failed: {n}",
        "FOREIGN KEY constraint failed",
        "incomplete input",
        "misuse of aggregate: {n}()",
        "wrong number of arguments to function {n}()",
        "datatype mismatch",
        // MySQL / MariaDB
        "Unknown column '{n}' in '{n} {n}'",
        "Unknown column '{n}' in '{n}'",
        "Table '{n}' doesn't exist",
        "You have an error in your SQL syntax; check the manual that corresponds to your {n} server version for the right syntax to use near '{v}' at line {n}",
        "Duplicate entry '{v}' for key '{n}'",
        "Column '{n}' cannot be null",
        "Incorrect {n} value: '{v}' for column '{n}' at row {n}",
        "Data too long for column '{n}' at row {n}",
    }.Select(Compile).ToArray();

    // Prefixes the providers put in front of a message: kept, they hold codes only.
    private static readonly Regex Prefix = new(@"^(?<code>[0-9A-Z]{5}: |SQLite Error \d+: )", RegexOptions.CultureInvariant);

    private static Regex Compile(string shape)
    {
        var index = 0;
        var pattern = Regex.Replace(Regex.Escape(shape), @"\\\{([ntv])}", m =>
        {
            var group = $"{m.Groups[1].Value}{index++}";
            return m.Groups[1].Value == "v" ? $"(?<{group}>.*?)" : $"(?<{group}>.{{1,128}}?)";
        });
        return new Regex("^" + pattern + "$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <param name="sentSql">The SQL as it is sent (values already hidden).</param>
    public static string Filter(string error, string sentSql)
    {
        var kept = new List<string>();
        var left = false;
        foreach (var raw in error.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Length <= 1_000 && Line(line, sentSql) is { } safe) kept.Add(safe);
            else left = true;
        }
        if (left || kept.Count == 0) kept.Add(Withheld);
        return string.Join('\n', kept);
    }

    private static string? Line(string line, string sentSql)
    {
        var prefix = Prefix.Match(line).Value;
        var body = line[prefix.Length..];
        // SQLite: SQLite Error 1: 'no such column: x'.
        if (prefix.StartsWith("SQLite", StringComparison.Ordinal) && body.StartsWith('\'') && body.EndsWith("'.", StringComparison.Ordinal) && body.Length > 3)
            body = body[1..^2];
        foreach (var shape in Shapes)
        {
            Match match;
            try { match = shape.Match(body); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;

            var result = body;
            var safe = true;
            // From the end, so the earlier positions stay right.
            foreach (var group in match.Groups.Cast<Group>().Where(g => g.Success && g.Name.Length > 1).OrderByDescending(g => g.Index))
            {
                var value = group.Value;
                if (group.Name[0] == 'n' && !IsName(value)) safe = false;
                if (group.Name[0] == 'v' || group.Name[0] == 't' && !sentSql.Contains(value, StringComparison.OrdinalIgnoreCase))
                    result = result[..group.Index] + "?" + result[(group.Index + group.Length)..];
            }
            if (safe) return prefix + result;
        }
        return null;
    }

    // An identifier, possibly qualified, or a type or keyword of a few words ("character varying", "FOREIGN KEY").
    private static bool IsName(string text) =>
        text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '$' or '#' or '@' or '[' or ']' or ' ')
        && text.Count(c => c == ' ') <= 2;
}

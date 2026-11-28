using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Claude.Assist;

/// <summary>
/// Hides the values of a SQL text before it leaves the machine on a schema-only connection: string and number literals and
/// comments become placeholders, which <see cref="Restore"/> puts back into what Claude proposes. Whatever any of the
/// engine's ways of reading the text calls a literal or a comment is hidden. The lengths of types ("varchar(50)") stay.
/// </summary>
public sealed partial class ClaudeRedactor
{
    private readonly DbKind _kind;
    private readonly HashSet<string> _identifiers;
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.Ordinal);   // original -> placeholder
    private readonly Dictionary<string, string> _originals = new(StringComparer.Ordinal);      // placeholder -> original

    private static readonly HashSet<string> SizedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit", "binary", "char", "character", "datetime2", "datetimeoffset", "dec", "decimal", "float", "nchar", "numeric",
        "nvarchar", "time", "timestamp", "timestamptz", "varbinary", "varchar", "varying",
    };

    /// <param name="identifiers">Names of the database's objects and columns: a double-quoted run that is one of them stays
    /// (SQL Server and SQLite may read any other one as a string).</param>
    public ClaudeRedactor(DbKind kind, IEnumerable<string>? identifiers = null)
    {
        _kind = kind;
        _identifiers = new HashSet<string>(identifiers ?? [], StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"__[snc]\d+__")]
    private static partial Regex Placeholder();

    private enum Hidden { String, Number, Comment }

    private readonly record struct Span(int Start, int End, Hidden Kind);

    public string Redact(string sql)
    {
        var spans = new List<Span>();
        foreach (var reading in SqlLexOptions.Readings(_kind)) Collect(sql, 0, sql.Length, reading, spans);
        if (spans.Count == 0) return sql;

        var result = new StringBuilder(sql.Length);
        var done = 0;
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            // Two readings that disagree: everything either of them hides goes.
            while (i + 1 < spans.Count && spans[i + 1].Start < span.End)
            {
                var next = spans[++i];
                if (next.End > span.End) span = new Span(span.Start, next.End, Hidden.String);
            }
            result.Append(sql, done, span.Start - done).Append(Hide(sql[span.Start..span.End], span.Kind));
            done = span.End;
        }
        return result.Append(sql, done, sql.Length - done).ToString();
    }

    /// <summary>Puts the hidden texts back. <paramref name="complete"/> is false when a placeholder is left that cannot be restored.</summary>
    public string Restore(string text, out bool complete)
    {
        var known = _originals.Keys.OrderByDescending(k => k.Length).ToList();
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var match = text[i] is '_' or '\'' or '"' or '/' or '$' ? known.FirstOrDefault(k => string.CompareOrdinal(text, i, k, 0, k.Length) == 0) : null;
            if (match is null)
            {
                result.Append(text[i++]);
                continue;
            }
            var original = _originals[match];
            result.Append(original);
            i += match.Length;
            // A "--" comment moved in front of code would swallow it.
            if (match.StartsWith("/*", StringComparison.Ordinal) && (original.StartsWith("--", StringComparison.Ordinal) || original.StartsWith('#'))
                && i < text.Length && text[i] is not ('\r' or '\n'))
                result.Append('\n');
        }
        var restored = result.ToString();
        complete = Placeholder().Matches(text).All(m => _originals.Keys.Any(k => k.Contains(m.Value, StringComparison.Ordinal)))
                   && Placeholder().Matches(restored).All(m => _originals.Values.Any(v => v.Contains(m.Value, StringComparison.Ordinal)));
        return restored;
    }

    private string Hide(string original, Hidden kind)
    {
        if (_placeholders.TryGetValue(original, out var known)) return known;
        var n = _placeholders.Count + 1;
        var placeholder = kind switch
        {
            Hidden.Comment => $"/*__c{n}__*/",
            Hidden.Number => $"__n{n}__",
            _ => original[0] == '"' ? $"\"__s{n}__\"" : $"'__s{n}__'",
        };
        _placeholders[original] = placeholder;
        _originals[placeholder] = original;
        return placeholder;
    }

    private void Collect(string sql, int from, int to, SqlLexOptions options, List<Span> spans)
    {
        var text = sql[from..to];
        var tokens = SqlLexer.Tokenize(text, options);
        var previousEnd = 0;
        var sizeUntil = 0;   // inside the parentheses of a sized type
        for (var i = 0; i <= tokens.Count; i++)
        {
            Comments(text, previousEnd, i < tokens.Count ? tokens[i].Start : text.Length, options, from, spans);
            if (i == tokens.Count) break;
            var t = tokens[i];
            if (t.Start < previousEnd) continue;   // already taken as part of a number
            previousEnd = t.End;
            var first = text[t.Start];
            switch (t.Kind)
            {
                case SqlTokenKind.Executable:
                    spans.Add(new Span(from + t.Start, from + t.End, Hidden.Comment));
                    break;
                case SqlTokenKind.Quoted when first == '\'':
                    spans.Add(new Span(from + t.Start, from + t.End, Hidden.String));
                    break;
                case SqlTokenKind.Quoted when first == '"' && IsString(text, t):
                    spans.Add(new Span(from + t.Start, from + t.End, Hidden.String));
                    break;
                case SqlTokenKind.Quoted when first == '$':
                    // A function body is code: its own literals and comments are hidden, the rest stays.
                    var delimiter = text.IndexOf('$', t.Start + 1) - t.Start + 1;
                    if (delimiter > 1 && t.End - t.Start >= 2 * delimiter)
                    {
                        var (bodyStart, bodyEnd) = (t.Start + delimiter, t.End - delimiter);
                        // Only after AS or DO is it a body; anywhere else it is a string like any other.
                        if (i > 0 && text[tokens[i - 1].Start..tokens[i - 1].End].ToUpperInvariant() is "AS" or "DO")
                            Collect(sql, from + bodyStart, from + bodyEnd, options, spans);
                        else spans.Add(new Span(from + t.Start, from + t.End, Hidden.String));
                    }
                    else spans.Add(new Span(from + t.Start, from + t.End, Hidden.String));
                    break;
                case SqlTokenKind.Word when SizedTypes.Contains(text[t.Start..t.End]):
                    if (i + 1 < tokens.Count && text[tokens[i + 1].Start] == '(' && text.IndexOf(')', tokens[i + 1].Start) is var close and > 0
                        && text.AsSpan(tokens[i + 1].Start + 1, close - tokens[i + 1].Start - 1).IndexOfAnyExcept("0123456789, \t") < 0)
                        sizeUntil = close;
                    break;
                case SqlTokenKind.Other when char.IsAsciiDigit(first) && t.Start >= sizeUntil:
                    if (t.Start > 0 && (SqlLexer.IsWordChar(text[t.Start - 1]) || text[t.Start - 1] is '$' or ':')) break;
                    var end = t.Start + 1;
                    while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '.' or '_')) end++;
                    spans.Add(new Span(from + t.Start, from + end, Hidden.Number));
                    previousEnd = end;
                    break;
            }
        }
    }

    private bool IsString(string text, SqlToken t) => _kind switch
    {
        DbKind.MySql => true,
        DbKind.Postgres => false,
        _ => t.End - t.Start < 2 || !_identifiers.Contains(text.Substring(t.Start + 1, t.End - t.Start - 2)),
    };

    private static void Comments(string text, int from, int to, SqlLexOptions options, int offset, List<Span> spans)
    {
        for (var i = from; i < to;)
        {
            if (!char.IsWhiteSpace(text[i]) && SqlLexer.TrySkipComment(text, i, options, out var end) && end > i)
            {
                spans.Add(new Span(offset + i, offset + end, Hidden.Comment));
                i = end;
            }
            else i++;
        }
    }
}

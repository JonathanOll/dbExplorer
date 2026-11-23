using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

/// <param name="Start">Offset of the first character in the script.</param>
/// <param name="End">Exclusive offset just after the last character of <paramref name="Text"/>.</param>
public sealed record ScriptStatement(string Text, int Start, int End);

public static class ScriptSplitter
{
    /// <summary>
    /// Splits a script into statements. Separators: ';' (all kinds) and a lone GO line (SQL Server only).
    /// Semicolons inside strings, quoted identifiers, comments and PostgreSQL dollar quotes do not split.
    /// A statement's text excludes its terminating separator and any trailing comment; comments in front of it
    /// belong to it. Comment-only and empty fragments are dropped.
    /// </summary>
    public static IReadOnlyList<ScriptStatement> Split(string script, DbKind kind) => SplitCore(script, kind, blankLines: false);

    /// <summary>
    /// As <see cref="Split"/>, and a blank line (a line with nothing but whitespace) also ends a statement, unless it is inside a
    /// string, quoted identifier, comment or dollar-quoted body. T-SQL needs no ';' between statements, so for "run the
    /// statement under the cursor" the blank line is the only visible boundary (DBeaver does the same). Used by Ctrl+Enter
    /// without a selection; F5 and a selection run as written, split by <see cref="Split"/>.
    /// </summary>
    public static IReadOnlyList<ScriptStatement> SplitAtBlankLines(string script, DbKind kind) =>
        SplitCore(script, kind, blankLines: true);

    /// <summary>
    /// The statement Ctrl+Enter runs for a caret at <paramref name="offset"/> (no selection): the one containing it, else the
    /// closest one above (a caret on a blank line runs the statement above it), else the first. Null when the script has none.
    /// </summary>
    public static ScriptStatement? StatementAtCaret(string script, DbKind kind, int offset)
    {
        var segments = SplitAtBlankLines(script, kind);
        var at = StatementAt(segments, Math.Clamp(offset, 0, script.Length));
        return at is null ? null : JoinOrphanWhere(script, kind, segments, at);
    }

    /// <summary>
    /// Safety rule against the blank-line cut: an UPDATE or DELETE with no WHERE of its own, followed (in the same ';'/GO
    /// statement) by a segment that starts with WHERE, is one statement. Without this the caret on
    /// <c>UPDATE t SET a = 1</c> would run it on every row and leave the condition behind.
    /// </summary>
    private static ScriptStatement JoinOrphanWhere(string script, DbKind kind, IReadOnlyList<ScriptStatement> segments, ScriptStatement at)
    {
        var index = -1;
        for (var i = 0; i < segments.Count; i++)
            if (ReferenceEquals(segments[i], at)) index = i;
        if (index < 0) return at;

        // The caret is on the UPDATE/DELETE (join forward) or on the orphan WHERE (join backward).
        if (index + 1 < segments.Count && TryJoin(script, kind, at, segments[index + 1]) is { } forward) return forward;
        if (index > 0 && TryJoin(script, kind, segments[index - 1], at) is { } backward) return backward;
        return at;
    }

    private static ScriptStatement? TryJoin(string script, DbKind kind, ScriptStatement head, ScriptStatement next)
    {
        var options = SqlLexOptions.For(kind);
        var words = SqlLexer.Tokenize(head.Text, options);
        if (words.Count == 0 || words[0].Kind != SqlTokenKind.Word) return null;
        var first = head.Text.AsSpan(words[0].Start, words[0].End - words[0].Start);
        if (!first.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) && !first.Equals("DELETE", StringComparison.OrdinalIgnoreCase)) return null;
        if (words.Any(t => IsWord(head.Text, t, "WHERE"))) return null;

        var nextWords = SqlLexer.Tokenize(next.Text, options);
        if (nextWords.Count == 0 || !IsWord(next.Text, nextWords[0], "WHERE")) return null;

        // Only when no ';' or GO separates them: the same statement for Split.
        if (!Split(script, kind).Any(s => s.Start <= head.Start && s.End >= next.End)) return null;
        return new ScriptStatement(script[head.Start..next.End], head.Start, next.End);
    }

    private static bool IsWord(string text, SqlToken t, string word) =>
        t.Kind == SqlTokenKind.Word && t.End - t.Start == word.Length
        && text.AsSpan(t.Start, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ScriptStatement> SplitCore(string script, DbKind kind, bool blankLines)
    {
        var options = SqlLexOptions.For(kind);
        var tokens = SqlLexer.Tokenize(script, options);
        var result = new List<ScriptStatement>();

        var segmentStart = 0;   // offset just after the previous separator
        var firstToken = -1;    // index of the first token of the current statement
        var lastEnd = 0;        // end offset of the last token of the current statement

        void Flush()
        {
            if (firstToken >= 0)
            {
                var start = SkipWhitespace(script, segmentStart);
                result.Add(new ScriptStatement(script[start..lastEnd], start, lastEnd));
            }
            firstToken = -1;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (blankLines
                && LastBlankLine(script, i == 0 ? 0 : tokens[i - 1].End, t.Start, startsBlank: i == 0, options.NestedBlockComments) is var gap and >= 0)
            {
                Flush();
                segmentStart = gap;
            }

            if (t.Kind == SqlTokenKind.Semicolon)
            {
                Flush();
                segmentStart = t.End;
            }
            else if (kind == DbKind.SqlServer && IsGoSeparator(script, t, out var goEnd))
            {
                Flush();
                segmentStart = goEnd;
                while (i + 1 < tokens.Count && tokens[i + 1].Start < goEnd) i++;   // skip the rest of the GO line
            }
            else
            {
                if (firstToken < 0) firstToken = i;
                lastEnd = t.End;
            }
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Offset of the line break that ends the last blank line in the gap between two tokens (whitespace and comments only), or
    /// -1. Text inside a comment is never a blank line; <paramref name="startsBlank"/> says the gap begins at a line start.
    /// </summary>
    private static int LastBlankLine(string s, int from, int to, bool startsBlank, bool nestedComments)
    {
        var found = -1;
        var lineBlank = startsBlank;
        var i = from;
        while (i < to)
        {
            var c = s[i];
            if (c == '\n' || (c == '\r' && !(i + 1 < s.Length && s[i + 1] == '\n')))
            {
                if (lineBlank) found = i;
                lineBlank = true;
                i++;
            }
            else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                i = SqlLexer.SkipLineComment(s, i);
                lineBlank = false;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i = SqlLexer.SkipBlockComment(s, i, nestedComments);
                lineBlank = false;
            }
            else
            {
                if (!char.IsWhiteSpace(c)) lineBlank = false;
                i++;
            }
        }
        return found;
    }

    /// <summary>
    /// The statement the cursor belongs to: the one containing <paramref name="offset"/> (boundaries included),
    /// otherwise the closest statement before it, otherwise the first one. Null when there are no statements.
    /// </summary>
    public static ScriptStatement? StatementAt(IReadOnlyList<ScriptStatement> statements, int offset)
    {
        ScriptStatement? previous = null;
        foreach (var s in statements)
        {
            if (offset < s.Start) return previous ?? s;
            if (offset <= s.End) return s;
            previous = s;
        }
        return previous;
    }

    /// <summary>
    /// A GO batch separator is the word GO alone on its line, optionally followed by a repeat count and a
    /// -- comment. <paramref name="end"/> is the offset of the end of that line.
    /// </summary>
    private static bool IsGoSeparator(string s, SqlToken t, out int end)
    {
        end = t.End;
        if (t.Kind != SqlTokenKind.Word || t.End - t.Start != 2
            || !s.AsSpan(t.Start, 2).Equals("GO", StringComparison.OrdinalIgnoreCase))
            return false;

        for (var i = t.Start - 1; i >= 0 && s[i] != '\n' && s[i] != '\r'; i--)
            if (!char.IsWhiteSpace(s[i])) return false;

        var j = SkipBlanks(s, t.End);
        while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        j = SkipBlanks(s, j);
        if (j < s.Length && s[j] != '\n' && s[j] != '\r' && !(s[j] == '-' && j + 1 < s.Length && s[j + 1] == '-'))
            return false;

        while (j < s.Length && s[j] != '\n' && s[j] != '\r') j++;   // the rest of the line is only a comment
        end = j;
        return true;
    }

    private static int SkipBlanks(string s, int i)
    {
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        return i;
    }

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }
}

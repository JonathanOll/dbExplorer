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
    /// Splits a SQL Server script into the batches the server would receive: only a lone GO line separates them, a ';' stays
    /// inside its batch (so <c>IF ... BEGIN ... DROP INDEX ...; CREATE INDEX ...; END</c> is never cut). Strings, [bracketed]
    /// identifiers and comments are respected. A batch's text excludes its GO line; comment-only and empty batches are dropped.
    /// A <c>GO n</c> line still only separates here: use <see cref="FindUnsupportedGo"/> to refuse it. A line that looks like a GO
    /// separator but is not one (<c>GO;</c>, <c>GO SELECT 1</c>...) is never silently merged into the previous batch: it throws
    /// <see cref="InvalidOperationException"/>, so callers refuse such a script first. Used by the upgrade runner.
    /// </summary>
    public static IReadOnlyList<ScriptStatement> SplitBatches(string script) =>
        SplitCore(script, DbKind.SqlServer, blankLines: false, semicolons: false, strictGo: true);

    /// <summary>
    /// The 1-based line of the first <c>GO n</c> separator with a repeat count other than 1 (T-SQL runs the batch n times; the
    /// splitter would silently run it once), or null. <c>GO</c> and <c>GO 1</c> are fine. A count too big for an int counts as above 1.
    /// </summary>
    public static int? FindGoRepeat(string script)
    {
        var tokens = SqlLexer.Tokenize(script, SqlLexOptions.For(DbKind.SqlServer));
        for (var i = 0; i < tokens.Count; i++)
        {
            var go = ClassifyGo(script, tokens, i);
            if (go.Kind == GoKind.Repeat) return LineOf(script, tokens[i].Start);
            if (go.Kind == GoKind.Separator) i = SkipGoLine(tokens, i, go.End);
        }
        return null;
    }

    /// <param name="Line">1-based line of the GO.</param>
    /// <param name="Reason">User-facing, without the line number (<c>GO n</c> for a repeat count).</param>
    public sealed record UnsupportedGo(int Line, string Reason);

    /// <summary>
    /// The first GO line the upgrade cannot play as written, or null: a <c>GO n</c> repeat count (not 1), or a line that starts with
    /// GO but is not a separator (<c>GO;</c>, <c>GO SELECT 1</c>, <c>GO 5x</c>...). sqlcmd and SSMS reject such lines too, and merging
    /// them into the previous batch would run it without the boundary the author intended. A word GO elsewhere in a line, GOTO,
    /// and GO inside strings, brackets and comments are not GO lines.
    /// </summary>
    public static UnsupportedGo? FindUnsupportedGo(string script)
    {
        var tokens = SqlLexer.Tokenize(script, SqlLexOptions.For(DbKind.SqlServer));
        for (var i = 0; i < tokens.Count; i++)
        {
            var go = ClassifyGo(script, tokens, i);
            switch (go.Kind)
            {
                case GoKind.Repeat:
                    return new UnsupportedGo(LineOf(script, tokens[i].Start), "'GO n' (repeat count) is not supported");
                case GoKind.Unsupported:
                    return new UnsupportedGo(LineOf(script, tokens[i].Start),
                        "GO must be alone on its line (only a count of 1 or a comment may follow it); this line is not supported");
                case GoKind.Separator:
                    i = SkipGoLine(tokens, i, go.End);
                    break;
            }
        }
        return null;
    }

    private static int LineOf(string script, int offset)
    {
        var line = 1;
        for (var k = 0; k < offset; k++)
            if (script[k] == '\n') line++;
        return line;
    }

    // Index of the last token that belongs to the GO line ending at goEnd (the loops' own i++ moves past it).
    private static int SkipGoLine(IReadOnlyList<SqlToken> tokens, int i, int goEnd)
    {
        while (i + 1 < tokens.Count && tokens[i + 1].Start < goEnd) i++;
        return i;
    }

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

    /// <param name="semicolons">False: a ';' is an ordinary token of its statement, only GO (and blank lines) separate.</param>
    /// <param name="strictGo">True: a GO-like line that is not a separator throws instead of staying in its statement.</param>
    private static IReadOnlyList<ScriptStatement> SplitCore(string script, DbKind kind, bool blankLines, bool semicolons = true, bool strictGo = false)
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

            if (t.Kind == SqlTokenKind.Semicolon && semicolons)
            {
                Flush();
                segmentStart = t.End;
            }
            else if (kind == DbKind.SqlServer && ClassifyGo(script, tokens, i) is var go && go.Kind is GoKind.Separator or GoKind.Repeat)
            {
                Flush();
                segmentStart = go.End;
                i = SkipGoLine(tokens, i, go.End);   // skip the rest of the GO line
            }
            else
            {
                if (strictGo && kind == DbKind.SqlServer && ClassifyGo(script, tokens, i).Kind == GoKind.Unsupported)
                    throw new InvalidOperationException(
                        $"Line {LineOf(script, t.Start)} starts with GO but is not a GO separator; refuse the script instead of splitting it.");
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

    private enum GoKind { None, Separator, Repeat, Unsupported }

    /// <param name="End">For a separator or a repeat: the offset of the end of the GO line.</param>
    private readonly record struct GoLine(GoKind Kind, int End);

    /// <summary>
    /// A GO batch separator is the word GO first on its line (only whitespace and block comments before it), optionally followed
    /// by a repeat count and then only block comments and a -- comment. A count other than 1 is a <see cref="GoKind.Repeat"/>
    /// (it still separates). Any other text after GO makes the line <see cref="GoKind.Unsupported"/>. A word GO that is not first
    /// on its line, or that is part of a longer word (GOTO), is <see cref="GoKind.None"/>.
    /// </summary>
    private static GoLine ClassifyGo(string s, IReadOnlyList<SqlToken> tokens, int index)
    {
        var t = tokens[index];
        if (t.Kind != SqlTokenKind.Word || t.End - t.Start != 2
            || !s.AsSpan(t.Start, 2).Equals("GO", StringComparison.OrdinalIgnoreCase))
            return new GoLine(GoKind.None, t.End);

        if (!FirstOnItsLine(s, index == 0 ? 0 : tokens[index - 1].End, t.Start, startsLine: index == 0))
            return new GoLine(GoKind.None, t.End);

        var j = SkipInlineSpace(s, t.End);
        var digitsFrom = j;
        while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        var repeat = false;
        if (j > digitsFrom)
            repeat = !int.TryParse(s.AsSpan(digitsFrom, j - digitsFrom), out var count) || count != 1;

        while (true)
        {
            j = SkipInlineSpace(s, j);
            if (j >= s.Length || s[j] == '\n' || s[j] == '\r') break;
            if (s[j] == '-' && j + 1 < s.Length && s[j + 1] == '-')
            {
                j = SqlLexer.SkipLineComment(s, j);
                break;
            }
            if (s[j] == '/' && j + 1 < s.Length && s[j + 1] == '*')
            {
                j = SqlLexer.SkipBlockComment(s, j, nested: true);
                continue;
            }
            return new GoLine(GoKind.Unsupported, t.End);
        }
        return new GoLine(repeat ? GoKind.Repeat : GoKind.Separator, j);
    }

    /// <summary>
    /// True when the gap between the previous token and the GO holds a line break, or nothing but whitespace and block comments
    /// from the start of the script; a -- comment never precedes a token of its own line.
    /// </summary>
    private static bool FirstOnItsLine(string s, int from, int to, bool startsLine)
    {
        var onLineStart = startsLine;
        var i = from;
        while (i < to)
        {
            var c = s[i];
            if (c is '\n' or '\r') { onLineStart = true; i++; }
            else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') { i = SqlLexer.SkipLineComment(s, i); onLineStart = false; }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') i = SqlLexer.SkipBlockComment(s, i, nested: true);
            else i++;
        }
        return onLineStart;
    }

    private static int SkipInlineSpace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]) && s[i] is not ('\n' or '\r')) i++;
        return i;
    }

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }
}

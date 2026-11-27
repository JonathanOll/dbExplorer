using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

internal enum SqlTokenKind
{
    /// <summary>Unquoted identifier or keyword.</summary>
    Word,
    /// <summary>String literal or quoted identifier (never inspected for keywords).</summary>
    Quoted,
    Semicolon,
    /// <summary>Any other single significant character (digits, operators, parentheses...).</summary>
    Other,
    /// <summary>A MySQL / MariaDB executable comment (<c>/*! ... */</c>, <c>/*M! ... */</c>): the server may run what it holds.</summary>
    Executable,
    /// <summary>A <c>DELIMITER x</c> line of a MySQL script: a client command, never sent to the server.</summary>
    Directive,
}

/// <param name="End">Exclusive end offset.</param>
internal readonly record struct SqlToken(SqlTokenKind Kind, int Start, int End);

/// <summary>Dialect switches for <see cref="SqlLexer"/>.</summary>
internal readonly record struct SqlLexOptions(
    bool DollarQuotes = false,
    bool NestedBlockComments = false,
    bool BackslashEscapesInEStrings = false,
    bool BracketIdentifiers = false,
    bool MySqlComments = false,
    bool BackslashEscapesInSingleQuotes = false,
    bool BackslashEscapesInDoubleQuotes = false,
    bool Delimiters = false)
{
    /// <summary>The lexing rules of each database; shared by the splitter and the read-only guard.</summary>
    public static SqlLexOptions For(DbKind kind) => kind switch
    {
        DbKind.Postgres => new SqlLexOptions(DollarQuotes: true, NestedBlockComments: true, BackslashEscapesInEStrings: true),
        DbKind.SqlServer => new SqlLexOptions(NestedBlockComments: true, BracketIdentifiers: true),
        // MySqlComments: '#' comments, "--" only before a blank, line comments ending at '\n' only, executable comments.
        DbKind.MySql => new SqlLexOptions(MySqlComments: true, BackslashEscapesInSingleQuotes: true, BackslashEscapesInDoubleQuotes: true),
        _ => new SqlLexOptions(BracketIdentifiers: true),
    };

    /// <summary>
    /// Every way a server of this kind may read the quotes of a text. MySQL's depends on the session's sql_mode
    /// (NO_BACKSLASH_ESCAPES, ANSI_QUOTES, MariaDB's MSSQL brackets) and on its version ($$ strings), which the text
    /// alone does not say.
    /// </summary>
    public static IReadOnlyList<SqlLexOptions> Readings(DbKind kind)
    {
        var usual = For(kind);
        if (kind != DbKind.MySql) return [usual];
        var ansi = usual with { BackslashEscapesInDoubleQuotes = false };
        var raw = ansi with { BackslashEscapesInSingleQuotes = false };
        return
        [
            usual, ansi, raw, ansi with { BracketIdentifiers = true }, raw with { BracketIdentifiers = true },
            usual with { DollarQuotes = true }, raw with { DollarQuotes = true },
        ];
    }
}

/// <summary>
/// Character-level state machine that turns SQL text into significant tokens: whitespace and comments are skipped,
/// string literals and quoted identifiers are single opaque <see cref="SqlTokenKind.Quoted"/> tokens.
/// Unterminated strings, identifiers and comments run to the end of the text.
/// </summary>
internal static class SqlLexer
{
    public static List<SqlToken> Tokenize(string s, SqlLexOptions o)
    {
        var tokens = new List<SqlToken>();
        var delimiter = ";";
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (o.Delimiters)
            {
                if (TryDirective(s, i, tokens, out var directiveEnd, out var next))
                {
                    delimiter = next;
                    tokens.Add(new SqlToken(SqlTokenKind.Directive, i, directiveEnd));
                    i = directiveEnd;
                    continue;
                }
                if (delimiter != ";" && Matches(s, i, delimiter))
                {
                    tokens.Add(new SqlToken(SqlTokenKind.Semicolon, i, i + delimiter.Length));
                    i += delimiter.Length;
                    continue;
                }
            }

            if (TrySkipComment(s, i, o, out var afterComment)) { i = afterComment; continue; }

            var start = i;
            if (o.MySqlComments && IsExecutableComment(s, i))
            {
                i = SkipExecutableComment(s, i, o);
                tokens.Add(new SqlToken(SqlTokenKind.Executable, start, i));
            }
            else if (c == ';' && delimiter == ";")
            {
                i++;
                tokens.Add(new SqlToken(SqlTokenKind.Semicolon, start, i));
            }
            else if (c == '\'')
            {
                var escapes = o.BackslashEscapesInSingleQuotes
                              || o.BackslashEscapesInEStrings && start > 0 && s[start - 1] is 'E' or 'e'
                              && (start < 2 || !IsWordChar(s[start - 2]));
                i = SkipQuoted(s, i, '\'', escapes);
                tokens.Add(new SqlToken(SqlTokenKind.Quoted, start, i));
            }
            else if (c is '"' or '`')
            {
                i = SkipQuoted(s, i, c, backslashEscapes: c == '"' && o.BackslashEscapesInDoubleQuotes);
                tokens.Add(new SqlToken(SqlTokenKind.Quoted, start, i));
            }
            else if (c == '[' && o.BracketIdentifiers && s.IndexOf(']', i + 1) >= 0)
            {
                i = SkipBracketIdentifier(s, i);
                tokens.Add(new SqlToken(SqlTokenKind.Quoted, start, i));
            }
            else if (c == '$' && o.DollarQuotes && TryDollarQuote(s, i, out var end))
            {
                i = end;
                tokens.Add(new SqlToken(SqlTokenKind.Quoted, start, i));
            }
            else if (IsWordStart(c))
            {
                i++;
                // '#' starts a comment in MySQL even against a word; a custom delimiter ("END$$") ends one.
                while (i < s.Length && IsWordChar(s[i]) && !(o.MySqlComments && s[i] == '#')
                       && !(delimiter != ";" && Matches(s, i, delimiter))) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Word, start, i));
            }
            else
            {
                i++;
                tokens.Add(new SqlToken(SqlTokenKind.Other, start, i));
            }
        }
        return tokens;
    }

    internal static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    internal static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    private static bool Matches(string s, int i, string text) => string.CompareOrdinal(s, i, text, 0, text.Length) == 0;

    /// <summary>
    /// <c>DELIMITER x</c>: first on its line, between two statements (as the mysql client reads it).
    /// </summary>
    private static bool TryDirective(string s, int i, List<SqlToken> before, out int end, out string delimiter)
    {
        const string Word = "delimiter";
        end = i;
        delimiter = "";
        if (before.Count > 0 && before[^1].Kind is not (SqlTokenKind.Semicolon or SqlTokenKind.Directive)) return false;
        if (i + Word.Length >= s.Length || !s.AsSpan(i, Word.Length).Equals(Word, StringComparison.OrdinalIgnoreCase)
            || s[i + Word.Length] is not (' ' or '\t')) return false;
        for (var k = i - 1; k >= 0 && s[k] != '\n'; k--)
            if (!char.IsWhiteSpace(s[k])) return false;

        var j = i + Word.Length;
        while (j < s.Length && s[j] is ' ' or '\t') j++;
        var from = j;
        while (j < s.Length && !char.IsWhiteSpace(s[j])) j++;
        if (j == from) return false;
        delimiter = s[from..j];
        while (j < s.Length && s[j] is not ('\r' or '\n')) j++;
        end = j;
        return true;
    }

    /// <summary>Skips the comment starting at <paramref name="i"/>, if any. An executable comment is not one.</summary>
    internal static bool TrySkipComment(string s, int i, SqlLexOptions o, out int end)
    {
        end = i;
        var c = s[i];
        if (o.MySqlComments)
        {
            // "1--1" is a subtraction: MySQL wants an ASCII blank or control character (or nothing) after "--"; a
            // non-ASCII one (U+0085, U+00A0) does not count.
            if (c == '#' || c == '-' && Peek(s, i + 1) == '-' && Peek(s, i + 2) is <= ' ' or '\u007f')
            {
                var eol = s.IndexOf('\n', i);
                end = eol < 0 ? s.Length : eol;
                return true;
            }
            if (IsExecutableComment(s, i)) return false;
        }
        else if (c == '-' && Peek(s, i + 1) == '-') { end = SkipLineComment(s, i); return true; }

        if (c != '/' || Peek(s, i + 1) != '*') return false;
        end = SkipBlockComment(s, i, o.NestedBlockComments);
        return true;
    }

    private static bool IsExecutableComment(string s, int i) =>
        s[i] == '/' && Peek(s, i + 1) == '*' && (Peek(s, i + 2) == '!' || Peek(s, i + 2) is 'M' or 'm' && Peek(s, i + 3) == '!');

    // Its content is SQL: a "*/" inside a string or a comment there does not end it.
    private static int SkipExecutableComment(string s, int i, SqlLexOptions o)
    {
        i += 3;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '*' && Peek(s, i + 1) == '/') return i + 2;
            if (c is '\'' or '"' or '`')
                i = SkipQuoted(s, i, c, c == '\'' ? o.BackslashEscapesInSingleQuotes : c == '"' && o.BackslashEscapesInDoubleQuotes);
            else if (TrySkipComment(s, i, o, out var end)) i = end;
            else i++;
        }
        return s.Length;
    }

    internal static int SkipLineComment(string s, int i)
    {
        while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
        return i;
    }

    internal static int SkipBlockComment(string s, int i, bool nested)
    {
        var depth = 1;
        i += 2;
        while (i < s.Length)
        {
            if (s[i] == '*' && Peek(s, i + 1) == '/')
            {
                i += 2;
                if (--depth == 0) return i;
            }
            else if (nested && s[i] == '/' && Peek(s, i + 1) == '*')
            {
                depth++;
                i += 2;
            }
            else i++;
        }
        return s.Length;
    }

    /// <summary>Skips a quoted run starting at the opening quote; a doubled quote is an escaped quote.</summary>
    internal static int SkipQuoted(string s, int i, char quote, bool backslashEscapes)
    {
        i++;
        while (i < s.Length)
        {
            if (backslashEscapes && s[i] == '\\') { i += 2; continue; }
            if (s[i] == quote)
            {
                if (Peek(s, i + 1) == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    /// <summary>Skips [identifier]; "]]" is an escaped bracket. The caller guarantees a closing bracket exists.</summary>
    internal static int SkipBracketIdentifier(string s, int i)
    {
        i++;
        while (i < s.Length)
        {
            if (s[i] == ']')
            {
                if (Peek(s, i + 1) == ']') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    /// <summary>
    /// Recognises a PostgreSQL dollar-quoted string ($$...$$ or $tag$...$tag$) starting at <paramref name="i"/>.
    /// Requires a closing delimiter; $1-style parameters and $ inside identifiers are not dollar quotes.
    /// </summary>
    internal static bool TryDollarQuote(string s, int i, out int end)
    {
        end = i;
        if (i > 0 && IsWordChar(s[i - 1])) return false;

        var j = i + 1;
        if (j < s.Length && IsWordStart(s[j]))
        {
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
        }
        if (j >= s.Length || s[j] != '$') return false;

        var delimiter = s.Substring(i, j - i + 1);
        var close = s.IndexOf(delimiter, j + 1, StringComparison.Ordinal);
        if (close < 0) return false;

        end = close + delimiter.Length;
        return true;
    }
}

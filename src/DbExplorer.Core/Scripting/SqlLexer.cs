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
}

/// <param name="End">Exclusive end offset.</param>
internal readonly record struct SqlToken(SqlTokenKind Kind, int Start, int End);

/// <summary>Dialect switches for <see cref="SqlLexer"/>.</summary>
internal readonly record struct SqlLexOptions(
    bool DollarQuotes = false,
    bool NestedBlockComments = false,
    bool BackslashEscapesInEStrings = false,
    bool BracketIdentifiers = false)
{
    /// <summary>The lexing rules of each database; shared by the splitter and the read-only guard.</summary>
    public static SqlLexOptions For(DbKind kind) => kind switch
    {
        DbKind.Postgres => new SqlLexOptions(DollarQuotes: true, NestedBlockComments: true, BackslashEscapesInEStrings: true),
        DbKind.SqlServer => new SqlLexOptions(NestedBlockComments: true, BracketIdentifiers: true),
        _ => new SqlLexOptions(BracketIdentifiers: true),
    };
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
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '-' && Peek(s, i + 1) == '-') { i = SkipLineComment(s, i); continue; }
            if (c == '/' && Peek(s, i + 1) == '*') { i = SkipBlockComment(s, i, o.NestedBlockComments); continue; }

            var start = i;
            if (c == ';')
            {
                i++;
                tokens.Add(new SqlToken(SqlTokenKind.Semicolon, start, i));
            }
            else if (c == '\'')
            {
                var escapes = o.BackslashEscapesInEStrings && start > 0 && s[start - 1] is 'E' or 'e'
                              && (start < 2 || !IsWordChar(s[start - 2]));
                i = SkipQuoted(s, i, '\'', escapes);
                tokens.Add(new SqlToken(SqlTokenKind.Quoted, start, i));
            }
            else if (c is '"' or '`')
            {
                i = SkipQuoted(s, i, c, backslashEscapes: false);
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
                while (i < s.Length && IsWordChar(s[i])) i++;
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

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

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
    private static int SkipQuoted(string s, int i, char quote, bool backslashEscapes)
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
    private static int SkipBracketIdentifier(string s, int i)
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
    private static bool TryDollarQuote(string s, int i, out int end)
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

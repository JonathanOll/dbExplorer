using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

/// <summary>The brackets and quotes the script editor pairs, and when typing an opener also adds its closer.</summary>
public static class AutoPairs
{
    /// <summary>The closer of an opening bracket or quote; null for any other character.</summary>
    public static char? CloserOf(char opener) => opener switch
    {
        '(' => ')',
        '[' => ']',
        '{' => '}',
        '\'' => '\'',
        '"' => '"',
        _ => null,
    };

    /// <summary>Whether <paramref name="opener"/> typed at <paramref name="caret"/> (not yet in the text) gets its closer.</summary>
    public static bool ShouldClose(string text, int caret, char opener, DbKind kind)
    {
        if (CloserOf(opener) is null) return false;
        var options = SqlLexOptions.For(kind);
        if (opener == '[' && !options.BracketIdentifiers) return false;

        // Only before a space or what ends an expression: before a word, a string or an opening bracket the closer is in the way.
        if (caret < text.Length && !char.IsWhiteSpace(text[caret]) && !")]},;:.=<>+-*/|%!".Contains(text[caret])) return false;
        if (opener is not ('\'' or '"')) return true;

        if (caret > 0)
        {
            var previous = text[caret - 1];
            if (previous == opener) return false;
            if (SqlLexer.IsWordChar(previous) && !(opener == '\'' && IsStringPrefix(text, caret, kind))) return false;
        }
        return IsInCode(text[..caret], options);
    }

    // N'...' (SQL Server), E'...' (PostgreSQL) and X'...' open a string although a letter comes first.
    private static bool IsStringPrefix(string text, int caret, DbKind kind)
    {
        if (caret >= 2 && SqlLexer.IsWordChar(text[caret - 2])) return false;
        return char.ToUpperInvariant(text[caret - 1]) switch
        {
            'N' => kind == DbKind.SqlServer,
            'E' => kind == DbKind.Postgres,
            'X' => true,
            _ => false,
        };
    }

    // A sentinel after the prefix is its own token, unless a string, a comment or a quoted identifier swallows it. The lexer
    // only sees a bracket identifier once it is closed, hence the second pass with a ']' after the sentinel.
    private static bool IsInCode(string before, SqlLexOptions options) =>
        HasSentinel(before + "\0", before.Length, options)
        && (!options.BracketIdentifiers || HasSentinel(before + "\0]", before.Length, options));

    private static bool HasSentinel(string text, int at, SqlLexOptions options) =>
        SqlLexer.Tokenize(text, options).Any(t => t.Kind == SqlTokenKind.Other && t.Start == at);
}

using DbExplorer.Core.Model;

namespace DbExplorer.Core.Scripting;

internal enum FKind
{
    /// <summary>Identifier, keyword, variable (@x, :x, $1, ?, #temp).</summary>
    Word,
    Number,
    /// <summary>String literal, quoted identifier, dollar-quoted body: never touched.</summary>
    Quoted,
    Op,
    /// <summary>"::".</summary>
    Cast,
    Dot,
    Comma,
    Semicolon,
    /// <summary>"(" or "[".</summary>
    Open,
    /// <summary>")" or "]".</summary>
    Close,
    /// <summary>Any other single character kept next to its neighbours as the source had it.</summary>
    Punct,
    LineComment,
    BlockComment,
    /// <summary>A character the formatter does not understand: the statement is left as it is.</summary>
    Unknown,
}

/// <summary>A token with the whitespace that preceded it, so layout can keep what must be kept (comment placement, blank lines).</summary>
internal sealed class FTok
{
    public FKind Kind;
    public string Raw = "";
    public int Start;
    public int End;

    /// <summary>The whitespace between the previous token and this one.</summary>
    public string Gap = "";
    public int NewlinesBefore;
    public bool SpaceBefore => Gap.Length > 0;

    /// <summary>Lower-cased text of a word (empty for other kinds).</summary>
    public string Lower = "";

    /// <summary>Text to write: the raw text, or the keyword in the chosen case.</summary>
    public string Out = "";

    /// <summary>A word the formatter treats as a keyword (its case may change).</summary>
    public bool Keyword;

    public bool IsComment => Kind is FKind.LineComment or FKind.BlockComment;

    /// <summary>The comment starts its own line in the source (or starts the text).</summary>
    public bool OwnLine => NewlinesBefore > 0 || Start == 0;

    public bool Is(string lowerWord) => Kind == FKind.Word && Lower == lowerWord;

    public override string ToString() => Raw;
}

internal abstract class FNode
{
}

internal sealed class FLeaf(FTok tok) : FNode
{
    public FTok Tok { get; } = tok;
    public bool IsWord(string lower) => Tok.Kind == FKind.Word && Tok.Lower == lower;
    public bool IsComment => Tok.IsComment;
}

/// <summary>A parenthesised or bracketed run, with its tokens as children.</summary>
internal sealed class FGroup(FTok open, FTok close, List<FNode> kids) : FNode
{
    public FTok Open { get; } = open;
    public FTok Close { get; } = close;
    public List<FNode> Kids { get; } = kids;

    /// <summary>Always one item per line (the column list of CREATE TABLE).</summary>
    public bool ForceBreak { get; set; }

    internal bool FlatDone;
    internal string? FlatText;
}

internal static class FTokenizer
{
    private static readonly string[] KnownOps =
    [
        "!~*", "->>", "#>>", "<->", "<=>", "<@", "@>", "@@", "#>", "#-", "->", "||", ":=", "=>", "<>", "<=", ">=", "!=", "!<", "!>",
        "&&", "<<", ">>", "~*", "!~", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "?|", "?&", "@?",
    ];

    private const string OpChars = "+-*/%<>=!~|&^";

    /// <summary>Splits text into tokens (comments included, whitespace kept as the gap of the next token). Never throws.</summary>
    public static List<FTok> Tokenize(string s, SqlLexOptions o)
    {
        var toks = new List<FTok>();
        var i = 0;
        var gapStart = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            var start = i;
            FKind kind;
            if (SqlLexer.TrySkipComment(s, i, o, out var commentEnd))
            {
                kind = c == '/' ? FKind.BlockComment : FKind.LineComment;
                i = commentEnd;
            }
            else if (c == ';') { i++; kind = FKind.Semicolon; }
            else if (c == '\'')
            {
                var escapes = o.BackslashEscapesInSingleQuotes
                              || o.BackslashEscapesInEStrings && start > 0 && s[start - 1] is 'E' or 'e'
                              && (start < 2 || !SqlLexer.IsWordChar(s[start - 2]));
                i = SqlLexer.SkipQuoted(s, i, '\'', escapes);
                kind = FKind.Quoted;
            }
            else if (c is '"' or '`') { i = SqlLexer.SkipQuoted(s, i, c, c == '"' && o.BackslashEscapesInDoubleQuotes); kind = FKind.Quoted; }
            else if (c == '[' && o.BracketIdentifiers && s.IndexOf(']', i + 1) >= 0) { i = SqlLexer.SkipBracketIdentifier(s, i); kind = FKind.Quoted; }
            else if (c == '$' && o.DollarQuotes && SqlLexer.TryDollarQuote(s, i, out var dEnd)) { i = dEnd; kind = FKind.Quoted; }
            else if (IsNumberStart(s, i, toks)) { i = SkipNumber(s, i); kind = FKind.Number; }
            // An executable comment is left exactly as it is: the whole statement is then not laid out.
            else if (o.MySqlComments && c == '/' && SqlLexer.Peek(s, i + 1) == '*') { i = s.Length; kind = FKind.Unknown; }
            else if (SqlLexer.IsWordStart(c) || IsVariableStart(s, i))
            {
                i++;
                while (i < s.Length && SqlLexer.IsWordChar(s[i]) && (!o.DollarQuotes || s[i] is not ('@' or '#'))
                       && !(o.MySqlComments && s[i] == '#')) i++;
                kind = FKind.Word;
                var word = s.Substring(start, i - start);
                // U&'..' and U&".." (unicode escapes) change meaning with spacing: do not reformat them.
                if (word is "U" or "u" && SqlLexer.Peek(s, i) == '&' && SqlLexer.Peek(s, i + 1) is '\'' or '"') kind = FKind.Unknown;
            }
            else if (c == '$' && char.IsDigit(SqlLexer.Peek(s, i + 1)))
            {
                i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                kind = FKind.Word;
            }
            else if (c == '?' && SqlLexer.Peek(s, i + 1) is not ('|' or '&'))
            {
                i++;
                kind = FKind.Word;
            }
            else if (c == ':' && SqlLexer.Peek(s, i + 1) == ':') { i += 2; kind = FKind.Cast; }
            else if (c == ':' && SqlLexer.Peek(s, i + 1) == '=') { i += 2; kind = FKind.Op; }
            else if (c == ':' && (SqlLexer.IsWordStart(SqlLexer.Peek(s, i + 1))))
            {
                i++;
                while (i < s.Length && SqlLexer.IsWordChar(s[i])) i++;
                kind = FKind.Word;
            }
            else if (c == ',') { i++; kind = FKind.Comma; }
            else if (c == '.') { i++; kind = FKind.Dot; }
            else if (c is '(' or '[') { i++; kind = FKind.Open; }
            else if (c is ')' or ']') { i++; kind = FKind.Close; }
            else if (TryOperator(s, i, out var opEnd)) { i = opEnd; kind = FKind.Op; }
            else if (c < 128 && char.IsPunctuation(c) || c < 128 && char.IsSymbol(c))
            {
                i++;
                kind = FKind.Punct;
            }
            else
            {
                i++;
                kind = FKind.Unknown;
            }

            if (kind == FKind.LineComment)
            {
                // Trailing blanks are not part of the comment: they must not survive as trailing whitespace.
                while (i > start + 2 && s[i - 1] is ' ' or '\t') i--;
            }
            var raw = s.Substring(start, i - start);
            var gap = s.Substring(gapStart, start - gapStart);
            var tok = new FTok { Kind = kind, Raw = raw, Start = start, End = i, Gap = gap, NewlinesBefore = CountNewlines(gap), Out = raw };
            if (kind == FKind.Word) tok.Lower = raw.ToLowerInvariant();
            toks.Add(tok);
            gapStart = i;
        }
        return toks;
    }

    private static int CountNewlines(string gap)
    {
        var n = 0;
        for (var i = 0; i < gap.Length; i++)
        {
            if (gap[i] == '\n') n++;
            else if (gap[i] == '\r' && (i + 1 >= gap.Length || gap[i + 1] != '\n')) n++;
        }
        return n;
    }

    private static bool IsVariableStart(string s, int i)
    {
        var c = s[i];
        if (c is not ('@' or '#')) return false;
        var j = i + 1;
        if (j < s.Length && s[j] == c) j++;
        return j < s.Length && SqlLexer.IsWordStart(s[j]);
    }

    private static bool IsNumberStart(string s, int i, List<FTok> toks)
    {
        var c = s[i];
        if (char.IsDigit(c)) return true;
        if (c != '.' || !char.IsDigit(SqlLexer.Peek(s, i + 1))) return false;
        if (toks.Count == 0) return true;
        var prev = toks[^1];
        // "t.1" or "x[1].5" are not numbers; ".5" after an operator or a keyword is.
        return !(prev.Kind is FKind.Word or FKind.Quoted or FKind.Close or FKind.Number) || prev.Raw.Length > 0 && prev.Kind == FKind.Word && IsClauseWord(prev.Raw);
    }

    private static bool IsClauseWord(string w) => w.Equals("select", StringComparison.OrdinalIgnoreCase)
        || w.Equals("and", StringComparison.OrdinalIgnoreCase) || w.Equals("or", StringComparison.OrdinalIgnoreCase)
        || w.Equals("then", StringComparison.OrdinalIgnoreCase) || w.Equals("else", StringComparison.OrdinalIgnoreCase)
        || w.Equals("when", StringComparison.OrdinalIgnoreCase) || w.Equals("by", StringComparison.OrdinalIgnoreCase);

    private static int SkipNumber(string s, int i)
    {
        if (s[i] == '0' && SqlLexer.Peek(s, i + 1) is 'x' or 'X')
        {
            i += 2;
            while (i < s.Length && Uri.IsHexDigit(s[i])) i++;
            return i;
        }
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (SqlLexer.Peek(s, i) == '.' && SqlLexer.Peek(s, i + 1) != '.')
        {
            i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }
        if (SqlLexer.Peek(s, i) is 'e' or 'E')
        {
            var j = i + 1;
            if (SqlLexer.Peek(s, j) is '+' or '-') j++;
            if (char.IsDigit(SqlLexer.Peek(s, j)))
            {
                while (j < s.Length && char.IsDigit(s[j])) j++;
                i = j;
            }
        }
        return i;
    }

    private static bool TryOperator(string s, int i, out int end)
    {
        end = i;
        foreach (var op in KnownOps)
        {
            if (string.CompareOrdinal(s, i, op, 0, op.Length) == 0)
            {
                end = i + op.Length;
                return true;
            }
        }
        var c = s[i];
        if (OpChars.IndexOf(c) < 0 && c is not ('@' or '#' or '?')) return false;

        var j = i;
        while (j < s.Length && OpChars.IndexOf(s[j]) >= 0)
        {
            // A comment start ends the operator.
            if (s[j] == '-' && SqlLexer.Peek(s, j + 1) == '-') break;
            if (s[j] == '/' && SqlLexer.Peek(s, j + 1) == '*') break;
            j++;
        }
        if (j == i)
        {
            end = i + 1; // lone @ # ?
            return true;
        }
        // PostgreSQL: an operator cannot end in + or - unless it also has one of ~ ! @ # % ^ & | ` ?
        var run = s.AsSpan(i, j - i);
        if (run.IndexOfAny("~!@#%^&|`?") < 0)
        {
            while (j - i > 1 && s[j - 1] is '+' or '-') j--;
        }
        end = j;
        return true;
    }

    /// <summary>Builds the paren/bracket tree of the tokens, or null when they do not nest properly.</summary>
    public static List<FNode>? BuildTree(IReadOnlyList<FTok> toks)
    {
        var root = new List<FNode>();
        var lists = new Stack<List<FNode>>();
        var opens = new Stack<FTok>();
        var cur = root;
        foreach (var t in toks)
        {
            switch (t.Kind)
            {
                case FKind.Open:
                    opens.Push(t);
                    lists.Push(cur);
                    cur = new List<FNode>();
                    break;
                case FKind.Close:
                    if (opens.Count == 0) return null;
                    var open = opens.Pop();
                    if ((open.Raw == "(") != (t.Raw == ")")) return null;
                    var kids = cur;
                    cur = lists.Pop();
                    cur.Add(new FGroup(open, t, kids));
                    break;
                case FKind.Unknown:
                case FKind.Semicolon:
                    return null;
                default:
                    cur.Add(new FLeaf(t));
                    break;
            }
        }
        return opens.Count == 0 ? root : null;
    }
}

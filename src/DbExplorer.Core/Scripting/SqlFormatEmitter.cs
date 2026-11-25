using System.Text;

namespace DbExplorer.Core.Scripting;

/// <summary>
/// Line-oriented text writer of the formatter. In flat mode it only builds one line and records <see cref="Failed"/> when the
/// content cannot live on one line (a comment that needs its own line, a subquery).
/// </summary>
internal sealed class SqlFormatEmitter(SqlFormatOptions options, string eol, bool flat)
{
    private readonly StringBuilder sb = new();
    private int lineStart;
    private string indentText = "";

    public bool Flat { get; } = flat;
    public bool Failed { get; set; }
    public int LineIndent { get; private set; }
    public bool LineHasContent { get; private set; }

    /// <summary>The last token written on the current line (null at the start of a line).</summary>
    public FTok? Last { get; set; }

    /// <summary>The last token was a unary operator: no space after it.</summary>
    public bool LastUnary { get; set; }

    /// <summary>A line comment was written: the next token must start a new line.</summary>
    public bool BreakPending { get; set; }

    public int Length => sb.Length;

    public int Column
    {
        get
        {
            var col = 0;
            for (var i = lineStart; i < sb.Length; i++) col += sb[i] == '\t' ? options.IndentSize : 1;
            return col;
        }
    }

    public void NewLine(int indent)
    {
        if (Flat) { Failed = true; return; }
        if (LineHasContent)
        {
            while (sb.Length > lineStart && sb[^1] is ' ' or '\t') sb.Length--;
            sb.Append(eol);
            lineStart = sb.Length;
        }
        else
        {
            sb.Length = lineStart;
        }
        indentText = options.UseTabs ? new string('\t', indent) : new string(' ', indent * options.IndentSize);
        sb.Append(indentText);
        LineIndent = indent;
        LineHasContent = false;
        Last = null;
        LastUnary = false;
        BreakPending = false;
    }

    public void Write(string text)
    {
        if (text.Length == 0) return;
        sb.Append(text);
        LineHasContent = true;
    }

    public override string ToString()
    {
        while (sb.Length > 0 && sb[^1] is ' ' or '\t') sb.Length--;
        return sb.ToString();
    }
}

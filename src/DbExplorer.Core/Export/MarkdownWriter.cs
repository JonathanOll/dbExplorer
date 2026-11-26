namespace DbExplorer.Core.Export;

/// <summary>A GitHub-style Markdown table. Cells are not padded, so rows can be written as they come.</summary>
public sealed class MarkdownWriter(TextWriter output, string nullText = "", string dateFormat = "") : RowWriter
{
    private int _columns;

    public override void Begin(IReadOnlyList<ExportColumn> columns)
    {
        _columns = columns.Count;
        Line(columns.Select(c => Escape(c.Name)));
        Line(columns.Select(c => c.ClrType is { } t && IsNumericType(Nullable.GetUnderlyingType(t) ?? t) ? "---:" : "---"));
    }

    public override void Write(object?[] row) =>
        Line(Enumerable.Range(0, _columns).Select(i => Escape(Value(row, i) is { } v ? ValueText.Format(v, dateFormat) : nullText)));

    private void Line(IEnumerable<string> cells)
    {
        output.Write("| ");
        output.Write(string.Join(" | ", cells));
        output.Write(" |\n");
    }

    private static bool IsNumericType(Type t) =>
        t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) || t == typeof(uint)
        || t == typeof(long) || t == typeof(ulong) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("|", "\\|").Replace("<", "&lt;").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
}

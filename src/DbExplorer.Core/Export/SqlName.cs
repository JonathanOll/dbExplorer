using System.Text;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Export;

/// <summary>Reads a table name typed by the user ("schema.table", quoted or not) into a <see cref="TableRef"/>.</summary>
public static class SqlName
{
    /// <summary>Splits at dots outside quotes; the last part is the table, what precedes it the schema. Null for an empty name.</summary>
    public static TableRef? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = new List<string>();
        var part = new StringBuilder();
        var close = '\0';   // the character that ends the quoted section we are in
        var s = text.Trim();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (close != '\0')
            {
                if (c != close) part.Append(c);
                else if (i + 1 < s.Length && s[i + 1] == close) { part.Append(c); i++; }
                else close = '\0';
            }
            else if (part.Length == 0 && c is '"' or '[' or '`') close = c == '[' ? ']' : c;
            else if (c == '.') { parts.Add(part.ToString()); part.Clear(); }
            else part.Append(c);
        }
        parts.Add(part.ToString());
        var name = parts[^1];
        if (name.Length == 0) return null;
        var schema = string.Join('.', parts.Take(parts.Count - 1));
        return new TableRef(schema.Length == 0 ? null : schema, name);
    }
}

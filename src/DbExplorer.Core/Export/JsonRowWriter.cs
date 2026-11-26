using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DbExplorer.Core.Export;

/// <summary>An array with one object per row, keyed by column name. Numbers and booleans stay JSON numbers and booleans.</summary>
public sealed class JsonRowWriter(TextWriter output) : RowWriter
{
    private static readonly JsonWriterOptions Options = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n" };

    private readonly ArrayBufferWriter<byte> _buffer = new();
    private string[] _names = [];
    private bool _any;

    public override void Begin(IReadOnlyList<ExportColumn> columns)
    {
        // Two columns of the same name would give an object with a key twice, which most readers drop silently.
        var used = new HashSet<string>(StringComparer.Ordinal);
        _names = columns.Select(c =>
        {
            var name = c.Name;
            for (var n = 2; !used.Add(name); n++) name = $"{c.Name}_{n}";
            return name;
        }).ToArray();
        output.Write('[');
    }

    public override void Write(object?[] row)
    {
        _buffer.ResetWrittenCount();
        using (var writer = new Utf8JsonWriter(_buffer, Options))
        {
            writer.WriteStartObject();
            for (var i = 0; i < _names.Length; i++) WriteValue(writer, _names[i], Value(row, i));
            writer.WriteEndObject();
        }
        output.Write(_any ? ",\n  " : "\n  ");
        _any = true;
        output.Write(Encoding.UTF8.GetString(_buffer.WrittenSpan).Replace("\n", "\n  "));   // line breaks inside strings are escaped
    }

    public override void End() => output.Write(_any ? "\n]\n" : "]\n");

    private static void WriteValue(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNull(name); break;
            case bool b: writer.WriteBoolean(name, b); break;
            case sbyte or byte or short or ushort or int or uint or long: writer.WriteNumber(name, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case ulong u: writer.WriteNumber(name, u); break;
            case decimal m: writer.WriteNumber(name, m); break;
            case double d when double.IsFinite(d): writer.WriteNumber(name, d); break;
            case float f when float.IsFinite(f): writer.WriteNumber(name, f); break;
            default: writer.WriteString(name, ValueText.Format(value)); break;
        }
    }
}

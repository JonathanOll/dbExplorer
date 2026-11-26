using System.Collections;
using System.Globalization;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Export;

/// <summary>
/// A value as a SQL literal of one engine, for generated INSERT / UPDATE / IN text. Every engine difference of literals is
/// here. PostgreSQL literals assume <c>standard_conforming_strings</c> (the default since 9.1): a backslash is an ordinary
/// character.
/// </summary>
public static class SqlLiterals
{
    public static string Format(DbKind kind, object? value)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (value)
        {
            case null or DBNull: return "NULL";
            case string s: return Text(kind, s);
            case char c: return Text(kind, c.ToString());
            case bool b: return kind == DbKind.Postgres ? (b ? "TRUE" : "FALSE") : (b ? "1" : "0");
            case double d: return double.IsFinite(d) ? d.ToString("R", inv) : NotFinite(kind, d);
            case float f: return float.IsFinite(f) ? f.ToString("R", inv) : NotFinite(kind, f);
            case sbyte or byte or short or ushort or int or uint or long or ulong or decimal or System.Numerics.BigInteger:
                return ((IFormattable)value).ToString(null, inv);
            case DateTime dt: return Quoted(Timestamp(kind, dt));
            case DateTimeOffset dto:
                return Quoted(dto.ToString(kind == DbKind.SqlServer ? "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz" : "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", inv));
            case DateOnly date: return Quoted(date.ToString("yyyy-MM-dd", inv));
            case TimeOnly time: return Quoted(time.ToString("HH:mm:ss.FFFFFFF", inv));
            case TimeSpan span: return Quoted(kind == DbKind.Postgres ? Interval(span) : span.ToString("c", inv));
            case Guid g: return Quoted(g.ToString("D"));
            case byte[] bytes:
                var hex = Convert.ToHexString(bytes);
                return kind switch
                {
                    DbKind.SqlServer => "0x" + hex,
                    DbKind.Postgres => $"'\\x{hex}'",
                    _ => $"X'{hex}'",
                };
            case BitArray bits: return "B" + Quoted(ValueText.Bits(bits));
            case Array array when kind == DbKind.Postgres:
                // '{}' takes the type of its column; ARRAY[] alone is refused.
                return array.Length == 0 ? "'{}'" : "ARRAY[" + string.Join(", ", array.Cast<object?>().Select(v => Format(kind, v))) + "]";
            default: return Text(kind, ValueText.Format(value));
        }
    }

    private static string Text(DbKind kind, string s)
    {
        // A NUL character would end the statement text early in SQLite; PostgreSQL text cannot hold one at all.
        if (s.Contains('\0'))
        {
            switch (kind)
            {
                case DbKind.Sqlite:
                    return $"CAST(X'{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(s))}' AS TEXT)";
                case DbKind.SqlServer:
                    return string.Join(" + NCHAR(0) + ", s.Split('\0').Select(part => "N" + Quoted(part)));
                default:
                    throw new ExportException("A text value holds a NUL character, which PostgreSQL cannot store.");
            }
        }
        // SQL Server: without N, non-ASCII text turns into '?'.
        return kind == DbKind.SqlServer ? "N" + Quoted(s) : Quoted(s);
    }

    private static string Timestamp(DbKind kind, DateTime dt)
    {
        var inv = CultureInfo.InvariantCulture;
        if (kind != DbKind.SqlServer) return dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", inv);
        // The "T" form does not depend on SET DATEFORMAT. A datetime column refuses more than 3 decimals, so more are
        // written only when the value has them (it then comes from a datetime2 column).
        return dt.ToString(dt.Ticks % TimeSpan.TicksPerMillisecond == 0 ? "yyyy-MM-ddTHH:mm:ss.fff" : "yyyy-MM-ddTHH:mm:ss.fffffff", inv);
    }

    private static string Interval(TimeSpan span)
    {
        var sign = span < TimeSpan.Zero ? "-" : "";
        var abs = span.Duration();
        var time = $"{abs.Hours:00}:{abs.Minutes:00}:{abs.Seconds:00}";
        var micro = abs.Ticks % TimeSpan.TicksPerSecond / 10;
        if (micro != 0) time += "." + micro.ToString("000000", CultureInfo.InvariantCulture).TrimEnd('0');
        return abs.Days == 0 ? sign + time : $"{sign}{abs.Days} days {sign}{time}";
    }

    private static string NotFinite(DbKind kind, double d) => kind switch
    {
        DbKind.Postgres => double.IsNaN(d) ? "'NaN'" : d > 0 ? "'Infinity'" : "'-Infinity'",
        DbKind.Sqlite => double.IsNaN(d) ? "NULL" : d > 0 ? "9e999" : "-9e999",
        _ => "NULL",   // SQL Server float cannot hold them
    };

    private static string Quoted(string s) => "'" + s.Replace("'", "''") + "'";
}

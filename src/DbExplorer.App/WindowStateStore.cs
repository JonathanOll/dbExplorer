using System.Text.Json;

namespace DbExplorer.App;

/// <summary>
/// Where the main window was last time. <see cref="X"/>/<see cref="Y"/> are physical screen pixels (null = let the OS
/// place it), <see cref="Width"/>/<see cref="Height"/> are device-independent units, all of the NORMAL (restored)
/// window; <see cref="Maximized"/> says whether it was maximised when the app closed.
/// </summary>
public sealed record WindowPlacement(int? X, int? Y, double Width, double Height, bool Maximized, double? NavWidth = null);

/// <summary>The working area of one monitor in physical pixels, with its DPI scale factor.</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height, double Scaling);

/// <summary>
/// Reads and writes the window placement as a tiny JSON file and fits it onto the screens that exist now. Pure and
/// cheap on purpose: it runs synchronously before the first paint, so it uses <see cref="Utf8JsonReader"/> directly
/// (no reflection serializer, nothing to warm up) and never throws: a missing or corrupt file is simply "no placement".
/// </summary>
public static class WindowStateStore
{
    /// <summary>A window overlapping a monitor by less than this (pixels) is considered off-screen.</summary>
    private const long MinVisibleArea = 200 * 100;

    private const double MaxExtent = 100_000;

    public static WindowPlacement? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var reader = new Utf8JsonReader(File.ReadAllBytes(path));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            int? x = null, y = null;
            double width = 0, height = 0;
            var maximized = false;
            double? navWidth = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "x": x = ReadInt(ref reader); break;
                    case "y": y = ReadInt(ref reader); break;
                    case "width": width = reader.GetDouble(); break;
                    case "height": height = reader.GetDouble(); break;
                    case "maximized": maximized = reader.GetBoolean(); break;
                    case "navWidth": navWidth = reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : null; break;
                    default: reader.Skip(); break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject) return null;

            if (!(width > 0 && width < MaxExtent && height > 0 && height < MaxExtent)) return null;   // also rejects NaN
            if ((x is null) != (y is null)) x = y = null;
            if (navWidth is { } nav && !(nav >= 100 && nav <= 2000)) navWidth = null;   // also rejects NaN
            return new WindowPlacement(x, y, width, height, maximized, navWidth);
        }
        catch (Exception)
        {
            return null;   // corrupt, unreadable or of an unexpected shape: start with the default placement
        }
    }

    private static int? ReadInt(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Null ? null : Math.Abs(reader.GetInt32()) < MaxExtent ? reader.GetInt32() : null;

    /// <summary>Best effort (temp file + move): losing the placement must never get in the way of closing.</summary>
    public static void TrySave(string path, WindowPlacement placement)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                if (placement.X is { } x && placement.Y is { } y)
                {
                    writer.WriteNumber("x", x);
                    writer.WriteNumber("y", y);
                }
                writer.WriteNumber("width", Math.Round(placement.Width, 1));
                writer.WriteNumber("height", Math.Round(placement.Height, 1));
                writer.WriteBoolean("maximized", placement.Maximized);
                if (placement.NavWidth is { } nav) writer.WriteNumber("navWidth", Math.Round(nav, 1));
                writer.WriteEndObject();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, stream.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    /// <summary>
    /// Makes a saved placement usable on the monitors present now (one may have been unplugged or the resolution
    /// changed). The monitor with the biggest overlap wins; the window is shrunk to fit it and moved fully onto it.
    /// A window that overlaps no monitor enough, or has no position, keeps only its (clamped) size and goes without a
    /// position, so the OS places it. <paramref name="screens"/> lists the primary monitor first.
    /// </summary>
    public static WindowPlacement Fit(WindowPlacement saved, IReadOnlyList<ScreenArea> screens, double minWidth, double minHeight)
    {
        var width = Math.Max(saved.Width, minWidth);
        var height = Math.Max(saved.Height, minHeight);
        if (screens.Count == 0) return saved with { X = null, Y = null, Width = width, Height = height };

        if (saved.X is { } x && saved.Y is { } y)
        {
            ScreenArea? best = null;
            long bestOverlap = 0;
            foreach (var screen in screens)
            {
                var overlap = Overlap(x, y, width * screen.Scaling, height * screen.Scaling, screen);
                if (overlap > bestOverlap)
                {
                    best = screen;
                    bestOverlap = overlap;
                }
            }

            if (best is { } target && bestOverlap >= MinVisibleArea)
            {
                var (w, h) = Shrink(width, height, target, minWidth, minHeight);
                var pixelsW = (int)Math.Round(w * target.Scaling);
                var pixelsH = (int)Math.Round(h * target.Scaling);
                var left = Math.Clamp(x, target.X, Math.Max(target.X, target.X + target.Width - pixelsW));
                var top = Math.Clamp(y, target.Y, Math.Max(target.Y, target.Y + target.Height - pixelsH));
                return saved with { X = left, Y = top, Width = w, Height = h };
            }
        }

        var (fw, fh) = Shrink(width, height, screens[0], minWidth, minHeight);
        return saved with { X = null, Y = null, Width = fw, Height = fh };
    }

    private static long Overlap(int x, int y, double width, double height, ScreenArea screen)
    {
        var w = Math.Min(x + width, screen.X + screen.Width) - Math.Max(x, screen.X);
        var h = Math.Min(y + height, screen.Y + screen.Height) - Math.Max(y, screen.Y);
        return w <= 0 || h <= 0 ? 0 : (long)(w * h);
    }

    /// <summary>Size in device-independent units, no bigger than the monitor (but never below the window's minimum).</summary>
    private static (double Width, double Height) Shrink(double width, double height, ScreenArea screen, double minWidth, double minHeight) =>
        (Math.Max(Math.Min(width, screen.Width / screen.Scaling), minWidth),
         Math.Max(Math.Min(height, screen.Height / screen.Scaling), minHeight));
}

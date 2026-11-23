using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UiShots;

/// <summary>Draws the application icon (accent tile with the database glyph) at several sizes and writes them as one PNG-compressed .ico.</summary>
internal static class IconMaker
{
    public static void Write(string path)
    {
        var glyph = (Geometry)Application.Current!.FindResource("IconDatabase")!;
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var images = new List<(int Size, byte[] Png)>();
        foreach (var size in sizes)
        {
            var visual = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.2),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#4F8FD0"), 0), new GradientStop(Color.Parse("#2D5887"), 1) },
                },
                Child = new PathIcon { Data = glyph, Width = size * 0.62, Height = size * 0.62, Foreground = Brushes.White, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
            };
            visual.Measure(new Size(size, size));
            visual.Arrange(new Rect(0, 0, size, size));
            using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            bitmap.Render(visual);
            using var ms = new MemoryStream();
            bitmap.Save(ms);
            images.Add((size, ms.ToArray()));
        }

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size == 256 ? 0 : size)); w.Write((byte)(size == 256 ? 0 : size)); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32); w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }
}
// Draws the app icon and writes it as a multi-size .ico file.
//
//   dotnet run tools/make-icon.cs [output.ico] [preview.png]
//
// The icon is a blue tile with a waveform in the app's colours and the yellow playhead.
// Sizes up to 64 px have their own layouts on whole pixels, so they stay sharp in the title
// bar, taskbar and Explorer; the larger sizes are scaled from the 256 px design.
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

string output = args.Length > 0 ? args[0] : Path.Combine("src", "AudioEditor.App", "Assets", "AudioEditor.ico");
string? preview = args.Length > 1 ? args[1] : null;

// WPF has to draw on an STA thread.
var thread = new Thread(() => IconMaker.Run(output, preview));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static class IconMaker
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>Each size is drawn this many times larger and then averaged down.</summary>
    private const int Supersample = 8;

    private static readonly Color TileTop = Color.FromRgb(0x2C, 0x4A, 0x70);
    private static readonly Color TileBottom = Color.FromRgb(0x13, 0x21, 0x35);
    private static readonly Color WaveTop = Color.FromRgb(0x9B, 0xE7, 0xFF);
    private static readonly Color WaveBottom = Color.FromRgb(0x33, 0xAE, 0xEF);
    private static readonly Color Playhead = Color.FromRgb(0xFF, 0xD5, 0x4F);

    /// <summary>Bar heights (1 = tallest) by bar count, and how many bars come before the playhead.</summary>
    private static readonly Dictionary<int, (double[] Heights, int BeforePlayhead)> Waves = new()
    {
        [3] = ([0.55, 1.0, 0.75], 2),
        [4] = ([0.45, 1.0, 0.6, 0.8], 3),
        [5] = ([0.4, 0.75, 1.0, 0.6, 0.75], 4),
        [6] = ([0.4, 0.7, 1.0, 0.6, 0.8, 0.45], 4),
        [7] = ([0.35, 0.6, 0.9, 0.55, 1.0, 0.75, 0.45], 5),
        [9] = ([0.28, 0.5, 0.78, 0.55, 1.0, 0.68, 0.82, 0.45, 0.3], 6),
    };

    /// <param name="Inset">Space around the tile.</param>
    /// <param name="Snap">Round heights to whole pixels (small sizes).</param>
    private sealed record Layout(int Size, double Inset, double BarWidth, double Gap, double PlayheadWidth, int Bars, bool Snap);

    public static void Run(string output, string? preview)
    {
        var images = Sizes.Select(size => (Size: size, Pixels: Render(LayoutFor(size)))).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        WriteIco(output, images);
        Console.WriteLine($"Wrote {output} ({string.Join(", ", Sizes)} px)");

        if (preview is not null)
        {
            WritePreview(preview, images);
            Console.WriteLine($"Wrote {preview}");
        }
    }

    private static Layout LayoutFor(int size) => size switch
    {
        16 => new(size, 0, 2, 1, 1, 3, true),
        20 => new(size, 0, 2, 1, 2, 4, true),
        24 => new(size, 1, 2, 1, 1, 5, true),
        32 => new(size, 2, 2, 1, 2, 6, true),
        40 => new(size, 2, 3, 1, 2, 6, true),
        48 => new(size, 3, 3, 2, 2, 6, true),
        64 => new(size, 4, 4, 2, 2, 7, true),
        _ => new(size, 16 * size / 256.0, 12 * size / 256.0, 7 * size / 256.0, 6 * size / 256.0, 9, false),
    };

    private static void Draw(DrawingContext dc, Layout layout)
    {
        double size = layout.Size;
        double tile = size - 2 * layout.Inset;
        var tileRect = new Rect(layout.Inset, layout.Inset, tile, tile);
        double radius = tile * 0.22;

        // Tile, with a faint light edge along the top.
        var tileBrush = new LinearGradientBrush(TileTop, TileBottom, 90);
        dc.DrawRoundedRectangle(tileBrush, null, tileRect, radius, radius);
        if (size >= 32)
        {
            double edge = Math.Max(1, size / 128);
            var highlight = new LinearGradientBrush(
                [new GradientStop(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.45)], 90);
            var inner = new Rect(tileRect.X + edge / 2, tileRect.Y + edge / 2, tile - edge, tile - edge);
            dc.DrawRoundedRectangle(null, new Pen(highlight, edge), inner, radius - edge / 2, radius - edge / 2);
        }

        var (heights, beforePlayhead) = Waves[layout.Bars];
        double width = layout.Bars * layout.BarWidth + (layout.Bars - 1) * layout.Gap + layout.PlayheadWidth + layout.Gap;
        double x = (size - width) / 2;
        double middle = size / 2;
        double tallest = layout.Snap ? Even(0.62 * tile) : 0.62 * tile;

        var waveBrush = new LinearGradientBrush(WaveTop, WaveBottom, new Point(0, middle - tallest / 2), new Point(0, middle + tallest / 2))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        double playheadX = 0;
        for (int i = 0; i < layout.Bars; i++)
        {
            if (i == beforePlayhead)
            {
                playheadX = x;
                x += layout.PlayheadWidth + layout.Gap;
            }
            double height = layout.Snap ? Math.Max(2, Even(heights[i] * tallest)) : heights[i] * tallest;
            double corner = layout.BarWidth / 2;
            dc.DrawRoundedRectangle(waveBrush, null, new Rect(x, middle - height / 2, layout.BarWidth, height), corner, corner);
            x += layout.BarWidth + layout.Gap;
        }

        // The playhead: a line a little taller than the waveform, with a marker on top that
        // narrows down into the line, like the one in the app.
        var playheadBrush = new SolidColorBrush(Playhead);
        double reach = Math.Max(1, Math.Round(0.06 * tile));
        double top = middle - tallest / 2 - reach;
        double bottom = middle + tallest / 2 + reach;
        double flare = Math.Max(1, Math.Round(0.07 * tile));
        double line = layout.PlayheadWidth;
        if (layout.Size <= 32)
        {
            // Whole-pixel steps, which stay sharp where a slanted edge would blur.
            for (int step = 0; step <= flare; step++)
                dc.DrawRectangle(playheadBrush, null, new Rect(playheadX - flare + step, top + step, line + 2 * (flare - step), 1));
            dc.DrawRectangle(playheadBrush, null, new Rect(playheadX, top, line, bottom - top));
        }
        else
        {
            double centre = playheadX + line / 2;
            double markerWidth = line + 2 * flare;
            double markerHeight = flare + line / 2;
            var marker = new StreamGeometry();
            using (var context = marker.Open())
            {
                context.BeginFigure(new Point(centre - markerWidth / 2, top), isFilled: true, isClosed: true);
                context.LineTo(new Point(centre + markerWidth / 2, top), false, false);
                context.LineTo(new Point(centre + line / 2, top + markerHeight), false, false);
                context.LineTo(new Point(centre - line / 2, top + markerHeight), false, false);
            }
            dc.DrawGeometry(playheadBrush, null, marker);
            dc.DrawRoundedRectangle(playheadBrush, null, new Rect(playheadX, top, line, bottom - top), line / 2, line / 2);
        }
    }

    private static double Even(double value) => 2 * Math.Round(value / 2);

    /// <summary>Renders one size as straight (not premultiplied) BGRA pixels.</summary>
    private static byte[] Render(Layout layout)
    {
        int size = layout.Size;
        int large = size * Supersample;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Supersample, Supersample));
            Draw(dc, layout);
        }
        var bitmap = new RenderTargetBitmap(large, large, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var source = new byte[large * large * 4];
        bitmap.CopyPixels(source, large * 4, 0);

        // Average each block of pixels (premultiplied, so edges blend correctly), then undo the
        // premultiplication, which is what .ico files expect.
        var pixels = new byte[size * size * 4];
        int samples = Supersample * Supersample;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int b = 0, g = 0, r = 0, a = 0;
                for (int sy = 0; sy < Supersample; sy++)
                {
                    int row = ((y * Supersample + sy) * large + x * Supersample) * 4;
                    for (int sx = 0; sx < Supersample; sx++)
                    {
                        int i = row + sx * 4;
                        b += source[i];
                        g += source[i + 1];
                        r += source[i + 2];
                        a += source[i + 3];
                    }
                }
                int o = (y * size + x) * 4;
                a = (a + samples / 2) / samples;
                pixels[o + 3] = (byte)a;
                if (a > 0)
                {
                    pixels[o] = (byte)Math.Min(255, (b * 255 + a * samples / 2) / (a * samples));
                    pixels[o + 1] = (byte)Math.Min(255, (g * 255 + a * samples / 2) / (a * samples));
                    pixels[o + 2] = (byte)Math.Min(255, (r * 255 + a * samples / 2) / (a * samples));
                }
            }
        }
        return pixels;
    }

    private static BitmapSource ToBitmap(int size, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Writes an .ico file: classic 32-bit bitmaps up to 64 px, which every Windows component
    /// reads, and PNG for the larger sizes, which keeps the file small.
    /// </summary>
    private static void WriteIco(string path, List<(int Size, byte[] Pixels)> images)
    {
        var data = images.Select(image => image.Size <= 64
            ? EncodeDib(image.Size, image.Pixels)
            : EncodePng(ToBitmap(image.Size, image.Pixels))).ToList();

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            int size = images[i].Size;
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(data[i].Length);
            writer.Write(offset);
            offset += data[i].Length;
        }
        foreach (var bytes in data)
            writer.Write(bytes);
    }

    private static byte[] EncodeDib(int size, byte[] pixels)
    {
        int maskStride = (size + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(size * size * 4 + maskStride * size);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // Rows go bottom to top.
        for (int y = size - 1; y >= 0; y--)
            writer.Write(pixels, y * size * 4, size * 4);

        // The 1-bit mask marks fully transparent pixels, for anything that ignores alpha.
        for (int y = size - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (int x = 0; x < size; x++)
            {
                if (pixels[(y * size + x) * 4 + 3] == 0)
                    row[x / 8] |= (byte)(0x80 >> (x % 8));
            }
            writer.Write(row);
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Every size at 1:1 and enlarged, on a light and a dark background.</summary>
    private static void WritePreview(string path, List<(int Size, byte[] Pixels)> images)
    {
        const int Zoom = 4;
        const int Gap = 16;
        int width = images.Sum(i => i.Size + Gap) + Gap;
        int zoomedWidth = images.Where(i => i.Size <= 64).Sum(i => i.Size * Zoom + Gap) + Gap;
        int rowHeight = 256 + 2 * Gap;
        int zoomedHeight = 64 * Zoom + 2 * Gap;
        int canvasWidth = Math.Max(width, zoomedWidth);
        int canvasHeight = 2 * rowHeight + 2 * zoomedHeight;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var backgrounds = new[] { Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromRgb(0x20, 0x20, 0x20) };
            double y = 0;
            foreach (var background in backgrounds)
            {
                dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, y, canvasWidth, rowHeight + zoomedHeight));
                double x = Gap;
                foreach (var (size, pixels) in images)
                {
                    dc.DrawImage(ToBitmap(size, pixels), new Rect(x, y + Gap + 256 - size, size, size));
                    x += size + Gap;
                }
                y += rowHeight;
                x = Gap;
                foreach (var (size, pixels) in images.Where(i => i.Size <= 64))
                {
                    int zoomed = size * Zoom;
                    dc.DrawImage(ToBitmap(zoomed, Enlarge(size, pixels, Zoom)), new Rect(x, y + Gap + 64 * Zoom - zoomed, zoomed, zoomed));
                    x += zoomed + Gap;
                }
                y += zoomedHeight;
            }
        }
        var bitmap = new RenderTargetBitmap(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Scales pixels up by a whole factor without smoothing, to inspect small sizes.</summary>
    private static byte[] Enlarge(int size, byte[] pixels, int factor)
    {
        int zoomed = size * factor;
        var result = new byte[zoomed * zoomed * 4];
        for (int y = 0; y < zoomed; y++)
        {
            for (int x = 0; x < zoomed; x++)
                Array.Copy(pixels, ((y / factor) * size + x / factor) * 4, result, (y * zoomed + x) * 4, 4);
        }
        return result;
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;

namespace Mkwmp.UpdateHelper;

/// <summary>One frame of the window.</summary>
/// <param name="Progress">0..1; negative: a segment that slides (<paramref name="Phase"/> 0..1).</param>
/// <param name="Rounded">Rounded corners on a transparent ground (a screenshot); false: the window's own rectangle with a border.</param>
internal readonly record struct Scene(bool Dark, string Title, string Text, double Progress, double Phase, string Caption, bool Failed, bool Rounded, bool Border = true);

/// <summary>
/// Draws the helper's window with GDI+ into a 32-bit bitmap: the launcher's mark, the title, the
/// step, a progress bar and the version, in the launcher's palette (Themes/Colors.*.xaml) and font
/// (Nunito, embedded; Segoe UI or the system's sans-serif when it cannot be loaded). The same
/// picture goes to the screen and to <c>--shot</c>.
/// </summary>
internal static unsafe class Painter
{
    /// <summary>The window in device-independent pixels.</summary>
    public const int Width = 440;
    public const int Height = 150;

    private const int PixelFormat32bppPArgb = 0xE200B;
    private const int UnitPixel = 2;
    private static nint _mark;
    private static nint _family;
    private static bool _started;

    private readonly record struct Palette(uint Ground, uint Border, uint Text, uint Muted, uint Faint, uint Accent, uint Track, uint Bad);

    private static readonly Palette Light = new(0xFFF0F0F0, 0xFFC6C6C6, 0xFF2B2B2B, 0xFF6C6C6C, 0xFFAEAEAE, 0xFF1E64E6, 0xFFCFCFCF, 0xFFE0352B);
    private static readonly Palette Dark = new(0xFF37373B, 0xFF56565C, 0xFFF3F3F3, 0xFFB1B1B6, 0xFF737378, 0xFF5CB8FF, 0xFF5C5C62, 0xFFFF6B61);

    /// <summary>False when GDI+ is not there (the helper then works without a window).</summary>
    public static bool Start()
    {
        if (_started)
        {
            return true;
        }
        try
        {
            var input = new Native.GdiplusStartupInput { Version = 1 };
            if (Native.GdiplusStartup(out _, &input, 0) != 0)
            {
                return false;
            }
            _started = true;
            LoadMark();
            LoadFont();
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static byte[]? Resource(string name)
    {
        using var stream = typeof(Painter).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void LoadMark()
    {
        try
        {
            if (Resource("mark.png") is not { } png)
            {
                return;
            }
            fixed (byte* data = png)
            {
                var stream = Native.SHCreateMemStream(data, (uint)png.Length); // copies; lives as long as the image
                if (stream != 0 && Native.GdipCreateBitmapFromStream(stream, out var image) == 0)
                {
                    _mark = image;
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no mark: the text is enough
        }
    }

    private static void LoadFont()
    {
        if (Native.GdipNewPrivateFontCollection(out var fonts) == 0)
        {
            foreach (var name in (string[])["Nunito-Regular.ttf", "Nunito-Bold.ttf"])
            {
                if (Resource(name) is { } font)
                {
                    // GDI+ reads the font from this memory for as long as the collection lives: never freed.
                    var memory = NativeMemory.Alloc((nuint)font.Length);
                    font.CopyTo(new Span<byte>(memory, font.Length));
                    _ = Native.GdipPrivateAddMemoryFont(fonts, memory, font.Length);
                }
            }
            if (Native.GdipCreateFontFamilyFromName("Nunito", fonts, out _family) == 0 && _family != 0)
            {
                return;
            }
        }
        // Wine's GDI+ does not find the family in the private collection, has no Segoe UI, and its generic
        // "sans serif" came out as a serif face under Proton 11: Tahoma and Arial are there (docs/LINUX.md).
        foreach (var name in (string[])["Segoe UI", "Tahoma", "Arial"])
        {
            if (Native.GdipCreateFontFamilyFromName(name, 0, out _family) == 0 && _family != 0)
            {
                return;
            }
        }
        _ = Native.GdipGetGenericFontFamilySansSerif(out _family);
    }

    /// <summary>A new bitmap of <see cref="Width"/> × <see cref="Height"/> at <paramref name="scale"/>; the caller disposes it (0: failed).</summary>
    public static nint Render(Scene scene, double scale)
    {
        var (w, h) = ((int)Math.Round(Width * scale), (int)Math.Round(Height * scale));
        if (Native.GdipCreateBitmapFromScan0(w, h, 0, PixelFormat32bppPArgb, 0, out var bitmap) != 0)
        {
            return 0;
        }
        if (Native.GdipGetImageGraphicsContext(bitmap, out var g) != 0)
        {
            _ = Native.GdipDisposeImage(bitmap);
            return 0;
        }
        var s = (float)scale;
        var colors = scene.Dark ? Dark : Light;
        _ = Native.GdipSetSmoothingMode(g, 4); // anti-alias
        _ = Native.GdipSetTextRenderingHint(g, 4); // anti-alias, no hinting: the same at every scale
        _ = Native.GdipSetInterpolationMode(g, 7); // high-quality bicubic
        _ = Native.GdipSetPixelOffsetMode(g, 4); // half

        if (scene.Rounded)
        {
            _ = Native.GdipGraphicsClear(g, 0x00000000);
            Rounded(g, 0.5f * s, 0.5f * s, w - s, h - s, 10 * s, colors.Ground, colors.Border, s);
        }
        else
        {
            _ = Native.GdipGraphicsClear(g, colors.Ground);
            if (scene.Border)
            {
                _ = Native.GdipCreatePen1(colors.Border, 1, UnitPixel, out var pen);
                _ = Native.GdipDrawRectangle(g, pen, 0.5f, 0.5f, w - 1, h - 1);
                _ = Native.GdipDeletePen(pen);
            }
        }

        const float Pad = 24, Mark = 56;
        var left = Pad;
        if (_mark != 0)
        {
            _ = Native.GdipDrawImageRect(g, _mark, Pad * s, Pad * s, Mark * s, Mark * s);
            left = Pad + Mark + 18;
        }
        var wide = Width - left - Pad;
        Text(g, scene.Title, 20, bold: true, colors.Text, left, 25, wide, 30, s, wrap: false);
        if (scene.Failed)
        {
            Text(g, scene.Text, 14.5f, bold: false, colors.Muted, left, 56, wide, 42, s, wrap: true);
            Rounded(g, Pad * s, 104 * s, (Width - 2 * Pad) * s, 4 * s, 2 * s, colors.Bad, 0, 0);
        }
        else
        {
            Text(g, scene.Text, 15.5f, bold: false, colors.Muted, left, 55, wide, 24, s, wrap: false);
            const float BarY = 104, BarH = 6;
            var barW = Width - 2 * Pad;
            Rounded(g, Pad * s, BarY * s, barW * s, BarH * s, BarH / 2 * s, colors.Track, 0, 0);
            var (from, to) = scene.Progress >= 0
                ? (0d, Math.Clamp(scene.Progress, 0, 1))
                : (Math.Max(0, scene.Phase * 1.3 - 0.3), Math.Min(1, scene.Phase * 1.3));
            var fill = (float)(to - from) * barW;
            if (fill >= BarH)
            {
                Rounded(g, (Pad + (float)from * barW) * s, BarY * s, fill * s, BarH * s, BarH / 2 * s, colors.Accent, 0, 0);
            }
            Text(g, scene.Caption, 12.5f, bold: false, colors.Faint, Pad, 119, barW, 20, s, wrap: false);
        }
        _ = Native.GdipDeleteGraphics(g);
        return bitmap;
    }

    private static void Text(nint g, string text, float size, bool bold, uint color, float x, float y, float width, float height, float s, bool wrap)
    {
        if (text.Length == 0 || _family == 0 || Native.GdipCreateFont(_family, size * s, bold ? 1 : 0, UnitPixel, out var font) != 0)
        {
            return;
        }
        _ = Native.GdipCreateSolidFill(color, out var brush);
        _ = Native.GdipCreateStringFormat(0, 0, out var format);
        _ = Native.GdipSetStringFormatFlags(format, wrap ? 0x2000 : 0x1000 | 0x2000); // line limit; no wrap
        _ = Native.GdipSetStringFormatTrimming(format, 3); // ellipsis
        var box = new Native.RectF { X = x * s, Y = y * s, Width = width * s, Height = height * s };
        _ = Native.GdipDrawString(g, text, text.Length, font, &box, format, brush);
        _ = Native.GdipDeleteStringFormat(format);
        _ = Native.GdipDeleteBrush(brush);
        _ = Native.GdipDeleteFont(font);
    }

    /// <summary>A filled rounded rectangle, with an outline when <paramref name="stroke"/> is not 0.</summary>
    private static void Rounded(nint g, float x, float y, float width, float height, float radius, uint fill, uint stroke, float strokeWidth)
    {
        var d = Math.Min(radius * 2, Math.Min(width, height));
        _ = Native.GdipCreatePath(0, out var path);
        _ = Native.GdipAddPathArc(path, x, y, d, d, 180, 90);
        _ = Native.GdipAddPathArc(path, x + width - d, y, d, d, 270, 90);
        _ = Native.GdipAddPathArc(path, x + width - d, y + height - d, d, d, 0, 90);
        _ = Native.GdipAddPathArc(path, x, y + height - d, d, d, 90, 90);
        _ = Native.GdipClosePathFigure(path);
        _ = Native.GdipCreateSolidFill(fill, out var brush);
        _ = Native.GdipFillPath(g, brush, path);
        _ = Native.GdipDeleteBrush(brush);
        if (stroke != 0)
        {
            _ = Native.GdipCreatePen1(stroke, strokeWidth, UnitPixel, out var pen);
            _ = Native.GdipDrawPath(g, pen, path);
            _ = Native.GdipDeletePen(pen);
        }
        _ = Native.GdipDeletePath(path);
    }

    /// <summary>Writes a frame as a PNG (<c>--shot</c>).</summary>
    public static bool Save(Scene scene, double scale, string path)
    {
        var bitmap = Render(scene, scale);
        if (bitmap == 0)
        {
            return false;
        }
        var png = new Guid("557CF406-1A04-11D3-9A73-0000F81EF32E");
        var saved = Native.GdipSaveImageToFile(bitmap, path, &png, 0) == 0;
        _ = Native.GdipDisposeImage(bitmap);
        return saved;
    }
}

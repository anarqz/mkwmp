// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mkwmp.Core.Overlay;

namespace Mkwmp.App.Overlay;

/// <summary>
/// Pictures the overlay draws itself: the card that stands in for the game's picture when the
/// game sent none (plugin API 3, or no screen copy under Wine), so the shrink still reads; and a
/// game-like frame for the demo and the screenshot harness. 1280 x 720, as the mod's picture.
/// </summary>
internal static class OverlayArt
{
    private const int Width = 1280;
    private const int Height = 720;

    private static readonly Typeface Face = new(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Nunito"),
        FontStyles.Normal, FontWeights.ExtraBold, FontStretches.Normal);

    /// <summary>A dark card with a kart and the game's name: what shrinks into the Play tile when there is no picture.</summary>
    public static BitmapSource Placeholder(string title, string caption)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var all = new Rect(0, 0, Width, Height);
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1D, 0x22, 0x3A), Color.FromRgb(0x0C, 0x0E, 0x18), new Point(0, 0), new Point(1, 1)), null, all);
            dc.PushClip(new RectangleGeometry(all));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x30, 0x2F, 0x6B, 0xFF)), null, new Point(1040, 90), 420, 420);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x24, 0x7A, 0x4D, 0xFF)), null, new Point(150, 720), 360, 360);
            dc.Pop();

            // the kart of Themes/Icons.xaml (a 24 x 24 line icon), large and centred
            if (Application.Current?.TryFindResource("Icon.Kart") is Geometry kart)
            {
                const double Scale = 13;
                dc.PushTransform(new TranslateTransform(Width / 2.0 - 12 * Scale, 300 - 12.5 * Scale));
                dc.PushTransform(new ScaleTransform(Scale, Scale));
                dc.DrawGeometry(null, new Pen(Brushes.White, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, kart);
                dc.Pop();
                dc.Pop();
            }
            var name = Text(title.ToUpperInvariant(), 34, Brushes.White);
            dc.DrawText(name, new Point(64, 52));
            var note = Text(caption, 30, new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
            dc.DrawText(note, new Point(64, 100));
        }
        return Render(visual);
    }

    /// <summary>A frame that looks like a race (sky, hills, a road, a kart, a little HUD): the demo's game.</summary>
    public static BitmapSource SyntheticGame()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var all = new Rect(0, 0, Width, Height);
            dc.PushClip(new RectangleGeometry(all));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2E, 0x8B, 0xF0), Color.FromRgb(0xBF, 0xE6, 0xFF), 90), null, all);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xF3, 0xB0)), null, new Point(1010, 150), 62, 62);
            foreach (var (x, y, r) in new[] { (240.0, 150.0, 60.0), (330.0, 170.0, 44.0), (720.0, 110.0, 50.0), (790.0, 126.0, 36.0) })
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), null, new Point(x, y), r * 1.5, r * 0.62);
            }
            // hills, then the ground
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x3F, 0xA6, 0x5B)), null,
                Geometry.Parse("M0,420 C180,300 330,310 520,400 C700,310 900,280 1280,400 L1280,720 L0,720 Z"));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x58, 0xC0, 0x62), Color.FromRgb(0x2F, 0x8F, 0x4A), 90), null, new Rect(0, 400, Width, 320));
            // the road, narrowing to the horizon, its kerbs and its centre line
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xE9, 0x4B, 0x4B)), null, Geometry.Parse("M560,400 L720,400 L1230,720 L50,720 Z"));
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x4A, 0x4F, 0x5C)), null, Geometry.Parse("M572,400 L708,400 L1150,720 L130,720 Z"));
            foreach (var (top, bottom, half) in new[] { (420.0, 446.0, 5.0), (474.0, 514.0, 9.0), (552.0, 610.0, 14.0), (660.0, 720.0, 20.0) })
            {
                dc.DrawGeometry(Brushes.White, null, Geometry.Parse(FormattableString.Invariant(
                    $"M{640 - half * 0.7},{top} L{640 + half * 0.7},{top} L{640 + half},{bottom} L{640 - half},{bottom} Z")));
            }
            // the kart, seen from behind
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00)), null, new Point(640, 640), 150, 22);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2C)), null, new Rect(500, 560, 64, 84), 16, 16);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2C)), null, new Rect(716, 560, 64, 84), 16, 16);
            dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x5A, 0x4D), Color.FromRgb(0xC9, 0x22, 0x2E), 90), null, new Rect(540, 520, 200, 112), 30, 30);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)), null, new Point(640, 500), 46, 46);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x6B, 0xFF)), null, new Rect(604, 456, 72, 34), 17, 17);
            // the HUD
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xA0, 0x10, 0x14, 0x20)), null, new Rect(40, 36, 190, 96), 22, 22);
            dc.DrawText(Text("1st", 64, Brushes.White), new Point(72, 44));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x14, 0x20)), new Pen(Brushes.White, 4), new Point(1150, 126), 86, 86);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)), 7) { LineJoin = PenLineJoin.Round },
                Geometry.Parse("M1108,150 C1100,100 1150,80 1180,104 C1212,128 1196,170 1160,168 C1136,166 1124,180 1108,150 Z"));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xA0, 0x10, 0x14, 0x20)), null, new Rect(1010, 610, 230, 74), 22, 22);
            dc.DrawText(Text("Lap 2/3", 40, Brushes.White), new Point(1046, 622));
            dc.Pop();
        }
        return Render(visual);
    }

    /// <summary>A PNG (or any picture WPF reads) as the frame; null when it cannot be read.</summary>
    public static BitmapSource? Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A picture as the mod would send it (the demo's request).</summary>
    public static OverlayFrame ToFrame(BitmapSource picture)
    {
        var bgr = new FormatConvertedBitmap(picture, PixelFormats.Bgr32, null, 0);
        var pixels = new byte[bgr.PixelWidth * bgr.PixelHeight * 4];
        bgr.CopyPixels(pixels, bgr.PixelWidth * 4, 0);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }
        return new OverlayFrame(bgr.PixelWidth, bgr.PixelHeight, pixels);
    }

    private static FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, 1.0);

    private static RenderTargetBitmap Render(DrawingVisual visual)
    {
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

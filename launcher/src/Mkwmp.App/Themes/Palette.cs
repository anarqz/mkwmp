// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.App.Themes;

/// <summary>A colour as the palette files write it (#AARRGGBB). Plain data, no WPF: the tests link this file.</summary>
public readonly record struct Rgba(byte A, byte R, byte G, byte B)
{
    public static Rgba Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var h = hex.TrimStart('#');
        var v = Convert.ToUInt32(h.Length == 6 ? "FF" + h : h, 16);
        return new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Settings › Appearance › Contrast: how far the raised surfaces (cards, rows, the dock, the hint
/// bar, panels, modals), the rules and the outlines stand from the page's background. The palette
/// files (Colors.Light.xaml, Colors.Dark.xaml) are the "normal" step; every other step scales each
/// of those colours' difference from the background by <see cref="Factor"/>. Where the scaled
/// colours would leave 0..255 (the light theme's white cards), the background moves instead. Text
/// that would fall under WCAG AA (4.5:1) on any surface is pushed toward black or white until it does not.
/// </summary>
public static class Palette
{
    public const string Normal = "normal";

    /// <summary>The steps, in their order on screen (client.toml <c>[ui] contrast</c>).</summary>
    public static readonly IReadOnlyList<string> Steps = ["low", Normal, "high", "higher"];

    /// <summary>The page's background: what the others are measured from.</summary>
    public const string Background = "Brush.Background";

    /// <summary>Raised surfaces: their difference from the background is scaled.</summary>
    public static readonly IReadOnlyList<string> Surfaces = ["Brush.Surface", "Brush.SurfaceAlt", "Brush.Modal", "Brush.Focus.Fill", "Brush.Toast"];

    /// <summary>Lines and tracks: scaled too.</summary>
    public static readonly IReadOnlyList<string> Lines = ["Brush.Rule", "Brush.Track", "Brush.Outline"];

    /// <summary>Translucent washes over a surface (hover, pressed): their opacity is scaled.</summary>
    public static readonly IReadOnlyList<string> Washes = ["Brush.Hover", "Brush.Pressed"];

    /// <summary>Colours that are the background's own (the letter on a hint's disc).</summary>
    public static readonly IReadOnlyList<string> SameAsBackground = ["Brush.OnGlyph"];

    /// <summary>
    /// Body text, kept readable on every surface (AA). The accent is not moved: it is also a fill
    /// (the Play button, dots), and it stays at least 3:1 (AA for large text and controls) at every step.
    /// </summary>
    public static readonly IReadOnlyList<string> Texts = ["Brush.Text", "Brush.TextMuted"];

    public const double MinTextContrast = 4.5;

    /// <summary>An unknown value is the normal step.</summary>
    public static string Normalize(string? step) => Steps.FirstOrDefault(s => string.Equals(s, step?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Normal;

    public static double Factor(string? step) => Normalize(step) switch
    {
        "low" => 0.6,
        "high" => 1.5,
        "higher" => 2.0,
        _ => 1.0,
    };

    /// <summary>The palette for <paramref name="factor"/>: only the colours that change, by key.</summary>
    public static Dictionary<string, Rgba> Derive(IReadOnlyDictionary<string, Rgba> palette, double factor)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var result = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        if (!palette.TryGetValue(Background, out var bg))
        {
            return result;
        }
        var scaled = Surfaces.Concat(Lines).Where(palette.ContainsKey).ToList();

        // where would the scaled colours land; the background moves by what overflows
        int Shift(Func<Rgba, byte> channel)
        {
            double over = 0, under = 0;
            foreach (var key in scaled)
            {
                var v = channel(bg) + (channel(palette[key]) - channel(bg)) * factor;
                over = Math.Max(over, v - 255);
                under = Math.Max(under, -v);
            }
            return (int)Math.Ceiling(under) - (int)Math.Ceiling(over);
        }
        var (sr, sg, sb) = (Shift(c => c.R), Shift(c => c.G), Shift(c => c.B));
        var newBg = new Rgba(bg.A, Clamp(bg.R + sr), Clamp(bg.G + sg), Clamp(bg.B + sb));
        result[Background] = newBg;
        foreach (var key in scaled)
        {
            var c = palette[key];
            result[key] = new Rgba(c.A,
                Clamp(newBg.R + (c.R - bg.R) * factor),
                Clamp(newBg.G + (c.G - bg.G) * factor),
                Clamp(newBg.B + (c.B - bg.B) * factor));
        }
        foreach (var key in Washes.Where(palette.ContainsKey))
        {
            var c = palette[key];
            result[key] = c with { A = Clamp(c.A * factor) };
        }
        foreach (var key in SameAsBackground.Where(palette.ContainsKey))
        {
            result[key] = newBg;
        }

        var grounds = Surfaces.Where(result.ContainsKey).Select(k => result[k]).Append(newBg).ToList();
        var dark = Luminance(newBg) < 0.5;
        foreach (var key in Texts.Where(palette.ContainsKey))
        {
            var text = palette[key];
            var toward = dark ? new Rgba(255, 255, 255, 255) : new Rgba(255, 0, 0, 0);
            for (var t = 0.0; t <= 1.0 && grounds.Any(g => ContrastRatio(text, g) < MinTextContrast); t += 0.02)
            {
                text = Mix(palette[key], toward, t);
            }
            if (text != palette[key])
            {
                result[key] = text;
            }
        }
        return result;
    }

    /// <summary>WCAG 2 relative luminance (sRGB).</summary>
    public static double Luminance(Rgba c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>WCAG 2 contrast ratio, 1 to 21.</summary>
    public static double ContrastRatio(Rgba a, Rgba b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static Rgba Mix(Rgba a, Rgba b, double t) => new(a.A, Clamp(a.R + (b.R - a.R) * t), Clamp(a.G + (b.G - a.G) * t), Clamp(a.B + (b.B - a.B) * t));

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}

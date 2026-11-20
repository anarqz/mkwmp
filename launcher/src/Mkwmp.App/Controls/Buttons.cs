// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Mkwmp.App.Controls;

internal static class Dp
{
    public static DependencyProperty Register<TOwner, T>(string name, T fallback, PropertyChangedCallback? changed = null,
        FrameworkPropertyMetadataOptions options = FrameworkPropertyMetadataOptions.None) =>
        DependencyProperty.Register(name, typeof(T), typeof(TOwner), new FrameworkPropertyMetadata(fallback, options, changed));
}

/// <summary>How a <see cref="ConsoleButton"/> looks at rest.</summary>
public enum ButtonKind
{
    /// <summary>Accent outline and text (a choice inside a modal).</summary>
    Outline,
    /// <summary>Filled with the accent: the one main action of a page.</summary>
    Primary,
    /// <summary>Grey outline, normal text.</summary>
    Secondary,
    /// <summary>No outline at all (a quiet action in a row of buttons).</summary>
    Ghost,
    /// <summary>The full-width button at the bottom of a modal card.</summary>
    Bottom,
}

/// <summary>The launcher's button. <c>&lt;c:ConsoleButton Kind="Primary" Content="Next" Command="…"/&gt;</c>, optional <see cref="Icon"/>.</summary>
public sealed class ConsoleButton : Button
{
    public static readonly DependencyProperty KindProperty = Dp.Register<ConsoleButton, ButtonKind>(nameof(Kind), ButtonKind.Outline);
    public static readonly DependencyProperty IconProperty = Dp.Register<ConsoleButton, Geometry?>(nameof(Icon), null);

    public ButtonKind Kind
    {
        get => (ButtonKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}

/// <summary>A round icon button of the dock (or of a pill of toggles). The label shows under it while focused.</summary>
public sealed class DockButton : Button
{
    public static readonly DependencyProperty IconProperty = Dp.Register<DockButton, Geometry?>(nameof(Icon), null);
    public static readonly DependencyProperty LabelProperty = Dp.Register<DockButton, string>(nameof(Label), "");
    public static readonly DependencyProperty TintProperty = Dp.Register<DockButton, Brush?>(nameof(Tint), null);
    public static readonly DependencyProperty FillProperty = Dp.Register<DockButton, Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty HasDotProperty = Dp.Register<DockButton, bool>(nameof(HasDot), false);
    public static readonly DependencyProperty IsSolidProperty = Dp.Register<DockButton, bool>(nameof(IsSolid), false);

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The icon's colour (default: the text colour).</summary>
    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <summary>The disc behind the icon (default: none). With a fill, set a contrasting <see cref="Tint"/>.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The small accent dot at the top right ("something new here").</summary>
    public bool HasDot
    {
        get => (bool)GetValue(HasDotProperty);
        set => SetValue(HasDotProperty, value);
    }

    /// <summary>The icon is an <c>IconFill.*</c> one.</summary>
    public bool IsSolid
    {
        get => (bool)GetValue(IsSolidProperty);
        set => SetValue(IsSolidProperty, value);
    }
}

/// <summary>
/// A card of the home carousel: an <see cref="Image"/>, or generated gradient art (from
/// <see cref="Seed"/>, else the title) with an <see cref="Icon"/>; a title and subtitle on it, an
/// optional <see cref="Badge"/> (top right) and <see cref="Progress"/> strip (0..1, NaN hides it).
/// </summary>
public sealed class Tile : Button
{
    public static readonly DependencyProperty TitleProperty = Dp.Register<Tile, string>(nameof(Title), "", (d, _) => ((Tile)d).UpdateArt());
    public static readonly DependencyProperty SubtitleProperty = Dp.Register<Tile, string>(nameof(Subtitle), "");
    public static readonly DependencyProperty ImageProperty = Dp.Register<Tile, ImageSource?>(nameof(Image), null);
    public static readonly DependencyProperty IconProperty = Dp.Register<Tile, Geometry?>(nameof(Icon), null);
    public static readonly DependencyProperty SeedProperty = Dp.Register<Tile, string>(nameof(Seed), "", (d, _) => ((Tile)d).UpdateArt());
    public static readonly DependencyProperty BadgeProperty = Dp.Register<Tile, string>(nameof(Badge), "");
    public static readonly DependencyProperty ProgressProperty = Dp.Register<Tile, double>(nameof(Progress), double.NaN);
    public static readonly DependencyProperty ArtProperty = Dp.Register<Tile, Brush?>(nameof(Art), null);
    public static readonly DependencyProperty CardClipProperty = Dp.Register<Tile, Geometry?>(nameof(CardClip), null);

    public Tile() => UpdateArt();

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>What the generated art is derived from; the same seed always gives the same colours.</summary>
    public string Seed
    {
        get => (string)GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    public string Badge
    {
        get => (string)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>The brush behind the icon; generated unless set.</summary>
    public Brush? Art
    {
        get => (Brush?)GetValue(ArtProperty);
        set => SetValue(ArtProperty, value);
    }

    /// <summary>The rounded outline of the card at its current size (the template clips the art with it).</summary>
    public Geometry? CardClip
    {
        get => (Geometry?)GetValue(CardClipProperty);
        private set => SetValue(CardClipProperty, value);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var clip = new RectangleGeometry(new Rect(RenderSize), 20, 20);
        clip.Freeze();
        CardClip = clip;
    }

    private void UpdateArt() => SetCurrentValue(ArtProperty, GeneratedArt.For(string.IsNullOrEmpty(Seed) ? Title : Seed));
}

/// <summary>Stable colours from a name: the tile gradients and the avatar discs.</summary>
public static class GeneratedArt
{
    private static readonly (uint From, uint To)[] Gradients =
    [
        (0xFF2F6BFF, 0xFF19C3F2), (0xFF7A4DFF, 0xFFFF6FB5), (0xFFFF7A1A, 0xFFFFC233), (0xFF10B981, 0xFF22D3EE),
        (0xFFF0443A, 0xFFFF9A3D), (0xFF4338CA, 0xFF3B9BFF), (0xFFEC4899, 0xFFFFA24D), (0xFF0EA5A0, 0xFF9BDB3A),
    ];

    private static readonly uint[] Discs =
    [
        0xFF2F6BFF, 0xFF7A4DFF, 0xFFE2498A, 0xFFF0443A, 0xFFF08A1A, 0xFF12A86B, 0xFF0E9DB8, 0xFF5B5BD6, 0xFFC0399F, 0xFF4E8F2D,
    ];

    /// <summary>FNV-1a: the same on every run and machine (string.GetHashCode is not).</summary>
    public static uint Hash(string? text)
    {
        var hash = 2166136261u;
        foreach (var c in text ?? "")
        {
            hash = (hash ^ c) * 16777619u;
        }
        return hash;
    }

    /// <summary>The gradient of a seed; "#0" … "#7" picks one of the eight directly.</summary>
    public static Brush For(string? seed)
    {
        var index = seed is ['#', var digit] && char.IsAsciiDigit(digit) ? (uint)(digit - '0') : Hash(seed);
        var (from, to) = Gradients[index % (uint)Gradients.Length];
        var brush = new LinearGradientBrush(ColorOf(from), ColorOf(to), new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    public static Brush Disc(string? name)
    {
        var brush = new SolidColorBrush(ColorOf(Discs[Hash(name) % (uint)Discs.Length]));
        brush.Freeze();
        return brush;
    }

    private static Color ColorOf(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}

/// <summary>What a <see cref="SettingsRow"/> shows at its right edge.</summary>
public enum RowKind
{
    /// <summary>Nothing (an action), or the row's own content.</summary>
    Plain,
    /// <summary><see cref="SettingsRow.Value"/> in the accent colour.</summary>
    Value,
    /// <summary>A switch bound to <see cref="SettingsRow.IsOn"/>; activating the row flips it.</summary>
    Toggle,
    /// <summary>The value (if any) and a chevron: opens something.</summary>
    Chevron,
}

/// <summary>
/// One line of a settings pane: a label, at the right a value / switch / chevron, a rule below and
/// an optional caption under the rule. It is a button: <c>Command</c> or <c>Click</c>.
/// </summary>
public sealed class SettingsRow : Button
{
    public static readonly DependencyProperty LabelProperty = Dp.Register<SettingsRow, string>(nameof(Label), "");
    public static readonly DependencyProperty ValueProperty = Dp.Register<SettingsRow, string>(nameof(Value), "");
    public static readonly DependencyProperty CaptionProperty = Dp.Register<SettingsRow, string>(nameof(Caption), "");
    public static readonly DependencyProperty KindProperty = Dp.Register<SettingsRow, RowKind>(nameof(Kind), RowKind.Plain);
    public static readonly DependencyProperty IconProperty = Dp.Register<SettingsRow, Geometry?>(nameof(Icon), null);
    public static readonly DependencyProperty ShowRuleProperty = Dp.Register<SettingsRow, bool>(nameof(ShowRule), true);
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(SettingsRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public RowKind Kind
    {
        get => (RowKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool ShowRule
    {
        get => (bool)GetValue(ShowRuleProperty);
        set => SetValue(ShowRuleProperty, value);
    }

    /// <summary>The switch of a <see cref="RowKind.Toggle"/> row (binds two-way by default).</summary>
    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    protected override void OnClick()
    {
        if (Kind == RowKind.Toggle)
        {
            SetCurrentValue(IsOnProperty, !IsOn);
        }
        base.OnClick();
    }
}

/// <summary>The pill switch. Inside a <see cref="SettingsRow"/> it is only a picture; alone it is a focus stop.</summary>
public sealed class ToggleSwitch : ToggleButton
{
}

/// <summary>A tick box with its text beside it ("I am 18 or older"): <c>IsChecked</c>, <c>Content</c>.</summary>
public sealed class ConsoleCheck : ToggleButton
{
}

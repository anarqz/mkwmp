// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Mkwmp.App.Input;

namespace Mkwmp.App.Controls;

/// <summary>The small dot at the bottom right of an <see cref="Avatar"/>.</summary>
public enum AvatarPresence { None, Online, Playing, Offline }

/// <summary>
/// A player as a disc: initials on a colour hashed from <see cref="DisplayName"/> (or an
/// <see cref="Image"/>), a presence dot, and an optional <see cref="Count"/> above it. Size it with
/// Width/Height. Not a focus stop: wrap it in a <c>Button Style="{StaticResource AvatarButton}"</c>.
/// <para>
/// On Nextendo Network a player has a profile picture: <see cref="Picture"/> is its file (decoded off this thread,
/// <see cref="AvatarImages"/>), and <see cref="Color"/> the colour Nextendo puts behind it, which the disc takes
/// while the picture is not there. Without either it is the disc it always was.
/// </para>
/// </summary>
public sealed class Avatar : Control
{
    public static readonly DependencyProperty DisplayNameProperty = Dp.Register<Avatar, string>(nameof(DisplayName), "", (d, _) => ((Avatar)d).Update());
    public static readonly DependencyProperty PresenceProperty = Dp.Register<Avatar, AvatarPresence>(nameof(Presence), AvatarPresence.None);
    public static readonly DependencyProperty CountProperty = Dp.Register<Avatar, string>(nameof(Count), "");
    public static readonly DependencyProperty ImageProperty = Dp.Register<Avatar, ImageSource?>(nameof(Image), null);
    public static readonly DependencyProperty InitialsProperty = Dp.Register<Avatar, string>(nameof(Initials), "");
    public static readonly DependencyProperty DiscProperty = Dp.Register<Avatar, Brush?>(nameof(Disc), null);
    public static readonly DependencyProperty PictureProperty = Dp.Register<Avatar, string?>(nameof(Picture), null, (d, _) => ((Avatar)d).LoadPicture());
    public static readonly DependencyProperty ColorProperty = Dp.Register<Avatar, string>(nameof(Color), "", (d, _) => ((Avatar)d).Update());

    public Avatar()
    {
        Focusable = false;
        Update();
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public AvatarPresence Presence
    {
        get => (AvatarPresence)GetValue(PresenceProperty);
        set => SetValue(PresenceProperty, value);
    }

    /// <summary>A short text above the disc (a count), "" for none.</summary>
    public string Count
    {
        get => (string)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public ImageSource? Image
    {
        get => (ImageSource?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    /// <summary>A picture file (JPEG or PNG) shown instead of the initials once it is decoded; null or one that cannot be read: the initials.</summary>
    public string? Picture
    {
        get => (string?)GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    /// <summary>"#rrggbb" for the disc behind the initials; "": the colour of the name.</summary>
    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value ?? "");
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == WidthProperty && !string.IsNullOrEmpty(Picture))
        {
            LoadPicture(); // a larger disc takes a sharper picture
        }
    }

    private async void LoadPicture()
    {
        var path = Picture;
        if (string.IsNullOrEmpty(path))
        {
            Image = null;
            return;
        }
        var pixels = !double.IsNaN(Width) && Width > 100 ? AvatarImages.Large : AvatarImages.Small;
        if (AvatarImages.Cached(path, pixels) is { } ready)
        {
            Image = ready;
            return;
        }
        // what is shown stays meanwhile: the initials, or the picture this one replaces
        var image = await AvatarImages.LoadAsync(path, pixels).ConfigureAwait(true);
        if (Picture == path)
        {
            Image = image;
        }
    }

    /// <summary>Derived from the name (one or two letters).</summary>
    public string Initials
    {
        get => (string)GetValue(InitialsProperty);
        private set => SetValue(InitialsProperty, value);
    }

    /// <summary>Derived from the name.</summary>
    public Brush? Disc
    {
        get => (Brush?)GetValue(DiscProperty);
        private set => SetValue(DiscProperty, value);
    }

    private void Update()
    {
        var name = DisplayName.Trim();
        Disc = AvatarImages.Disc(Color) ?? GeneratedArt.Disc(name);
        var parts = name.Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        Initials = parts.Length switch
        {
            0 => "?",
            1 => First(parts[0]) + Second(parts[0]),
            _ => First(parts[0]) + First(parts[^1]),
        };

        static string First(string s) => StringInfo.GetNextTextElement(s).ToUpperInvariant();

        static string Second(string s)
        {
            // "KartFan" → "KF"; "luna" → "L"
            for (var i = 1; i < s.Length; i++)
            {
                if (char.IsUpper(s[i]) || char.IsDigit(s[i]))
                {
                    return s[i].ToString();
                }
            }
            return "";
        }
    }
}

/// <summary>"▍Title" above a group of rows, with the rule under it.</summary>
public sealed class SectionHeader : Control
{
    public static readonly DependencyProperty TextProperty = Dp.Register<SectionHeader, string>(nameof(Text), "");
    public static readonly DependencyProperty ShowRuleProperty = Dp.Register<SectionHeader, bool>(nameof(ShowRule), true);

    public SectionHeader() => Focusable = false;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool ShowRule
    {
        get => (bool)GetValue(ShowRuleProperty);
        set => SetValue(ShowRuleProperty, value);
    }
}

/// <summary>
/// The header of a full page: an optional icon, the title, the rule; its content sits at the right
/// (page dots, a status). Home has the top bar instead.
/// </summary>
public sealed class PageHeader : ContentControl
{
    public static readonly DependencyProperty TitleProperty = Dp.Register<PageHeader, string>(nameof(Title), "");
    public static readonly DependencyProperty IconProperty = Dp.Register<PageHeader, Geometry?>(nameof(Icon), null);

    public PageHeader() => Focusable = false;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}

/// <summary>The busy ring. Size it with Width/Height (44 by default).</summary>
public sealed class Spinner : Control
{
    public Spinner() => Focusable = false;
}

/// <summary>The dots of a paged flow: <see cref="Count"/> pages, the current one (<see cref="Index"/>) larger and in the accent colour, the done ones solid.</summary>
public sealed class PageDots : FrameworkElement
{
    private const double Step = 46;
    private const double Box = 26;

    public static readonly DependencyProperty CountProperty = Dp.Register<PageDots, int>(nameof(Count), 0, null,
        FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty IndexProperty = Dp.Register<PageDots, int>(nameof(Index), 0, null, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty AccentProperty = Dp.Register<PageDots, Brush>(nameof(Accent), Brushes.DodgerBlue, null, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(PageDots), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public int Index
    {
        get => (int)GetValue(IndexProperty);
        set => SetValue(IndexProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Count <= 0 ? 0 : (Count - 1) * Step + Box, Box);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var outline = new Pen(Foreground, 2.5);
        for (var i = 0; i < Count; i++)
        {
            var center = new Point(Box / 2 + i * Step, Box / 2);
            if (i == Index)
            {
                drawingContext.DrawEllipse(Accent, null, center, 11, 11);
            }
            else if (i < Index)
            {
                drawingContext.DrawEllipse(Foreground, null, center, 7.5, 7.5);
            }
            else
            {
                drawingContext.DrawEllipse(null, outline, center, 6.5, 6.5);
            }
        }
    }
}

/// <summary>
/// A battery drawn from <see cref="Percent"/> (the fill follows the level; red at 15 % or less), with
/// a bolt and a green fill while <see cref="Charging"/>, and a plug while on the charger without
/// filling (<see cref="PluggedIn"/>: full, or held by the system). 52 x 28.
/// </summary>
public sealed class BatteryIcon : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = Dp.Register<BatteryIcon, int>(nameof(Percent), 100, null, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty ChargingProperty = Dp.Register<BatteryIcon, bool>(nameof(Charging), false, null, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty PluggedInProperty = Dp.Register<BatteryIcon, bool>(nameof(PluggedIn), false, null, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty LowProperty = Dp.Register<BatteryIcon, Brush>(nameof(Low), Brushes.OrangeRed, null, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty GoodProperty = Dp.Register<BatteryIcon, Brush>(nameof(Good), Brushes.LimeGreen, null, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(BatteryIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Geometry Bolt = Geometry.Parse("M26,4.5 L17,15 H23.5 L21,23.5 L30,13 H23.5 Z");

    /// <summary>A two-prong plug, centred like the bolt.</summary>
    private static readonly Geometry Plug = Geometry.Parse("M19.5,5 H21.5 V9 H19.5 Z M25.5,5 H27.5 V9 H25.5 Z M17,9 H30 V14 C30,17.5 27.5,19 25,19 V23 H22 V19 C19.5,19 17,17.5 17,14 Z");

    /// <summary>On the charger, not filling.</summary>
    public bool PluggedIn
    {
        get => (bool)GetValue(PluggedInProperty);
        set => SetValue(PluggedInProperty, value);
    }

    public int Percent
    {
        get => (int)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public bool Charging
    {
        get => (bool)GetValue(ChargingProperty);
        set => SetValue(ChargingProperty, value);
    }

    /// <summary>The fill at 15 % or less.</summary>
    public Brush Low
    {
        get => (Brush)GetValue(LowProperty);
        set => SetValue(LowProperty, value);
    }

    /// <summary>The fill while charging.</summary>
    public Brush Good
    {
        get => (Brush)GetValue(GoodProperty);
        set => SetValue(GoodProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(52, 28);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var pen = new Pen(Foreground, 2.6);
        drawingContext.DrawRoundedRectangle(null, pen, new Rect(1.3, 1.3, 44.4, 25.4), 6, 6);
        drawingContext.DrawRoundedRectangle(Foreground, null, new Rect(47.5, 9, 4, 10), 1.5, 1.5);
        var level = Math.Clamp(Percent, 0, 100) / 100.0;
        var fill = Charging ? Good : Percent <= 15 ? Low : Foreground;
        drawingContext.DrawRoundedRectangle(fill, null, new Rect(5.5, 5.5, Math.Max(4, 36 * level), 17), 2.5, 2.5);
        if (Charging)
        {
            // outlined in the frame colour so the bolt reads over the fill and over the empty part alike
            drawingContext.DrawGeometry(Brushes.White, new Pen(Foreground, 1.4) { LineJoin = PenLineJoin.Round }, Bolt);
        }
        else if (PluggedIn)
        {
            drawingContext.DrawGeometry(Brushes.White, new Pen(fill, 1.2), Plug);
        }
    }
}

/// <summary>
/// The picture of a button in the hint bar: a plain disc with the letter for a controller (A B X
/// Y), a pill for LB / RB, a disc with lines for Start / Back; a key cap for the keyboard. Generic
/// shapes only, no console's glyphs.
/// </summary>
public sealed class HintGlyph : FrameworkElement
{
    private const double Size = 42;
    private static readonly Typeface Face = new(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Nunito"),
        FontStyles.Normal, FontWeights.ExtraBold, FontStretches.Normal);

    public static readonly DependencyProperty ButtonProperty = Dp.Register<HintGlyph, PadButton>(nameof(Button), PadButton.A, null,
        FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty InputProperty = Dp.Register<HintGlyph, InputKind>(nameof(Input), InputKind.Gamepad, null,
        FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty FillProperty = Dp.Register<HintGlyph, Brush>(nameof(Fill), Brushes.Black, null, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty InkProperty = Dp.Register<HintGlyph, Brush>(nameof(Ink), Brushes.White, null, FrameworkPropertyMetadataOptions.AffectsRender);

    public PadButton Button
    {
        get => (PadButton)GetValue(ButtonProperty);
        set => SetValue(ButtonProperty, value);
    }

    /// <summary>Controller glyphs, or key caps for the keyboard and the mouse.</summary>
    public InputKind Input
    {
        get => (InputKind)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The letter on the disc.</summary>
    public Brush Ink
    {
        get => (Brush)GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    /// <summary>The key that does what the button does (the window's key handling uses the same table).</summary>
    public static string KeyName(PadButton button) => button switch
    {
        PadButton.A => "Enter",
        PadButton.B => "Esc",
        PadButton.X => "X",
        PadButton.Y => "F",
        PadButton.LB => "Q",
        PadButton.RB => "E",
        PadButton.Start => "F1",
        PadButton.Back => "F2",
        PadButton.Up => "↑",
        PadButton.Down => "↓",
        PadButton.Left => "←",
        _ => "→",
    };

    private bool Keys => Input != InputKind.Gamepad;

    private string Text => Keys ? KeyName(Button) : Button switch
    {
        PadButton.A => "A",
        PadButton.B => "B",
        PadButton.X => "X",
        PadButton.Y => "Y",
        PadButton.LB => "LB",
        PadButton.RB => "RB",
        _ => "",
    };

    private FormattedText Format(string text, double size, Brush brush) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Keys)
        {
            return new Size(Math.Max(Size, Format(Text, 22, Fill).Width + 26), Size);
        }
        return new Size(Button is PadButton.LB or PadButton.RB ? 68 : Size, Size);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var width = ActualWidth;
        var box = new Rect(0, 0, width, Size);
        if (Keys)
        {
            drawingContext.DrawRoundedRectangle(null, new Pen(Fill, 2.5), new Rect(1.25, 1.25, width - 2.5, Size - 2.5), 9, 9);
            var key = Format(Text, 22, Fill);
            drawingContext.DrawText(key, new Point((width - key.Width) / 2, (Size - key.Height) / 2));
            return;
        }
        drawingContext.DrawRoundedRectangle(Fill, null, box, Size / 2, Size / 2);
        var center = new Point(width / 2, Size / 2);
        switch (Button)
        {
            case PadButton.Start:
            {
                var pen = new Pen(Ink, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (var i = -1; i <= 1; i++)
                {
                    drawingContext.DrawLine(pen, new Point(center.X - 9, center.Y + i * 7), new Point(center.X + 9, center.Y + i * 7));
                }
                break;
            }
            case PadButton.Back:
            {
                var pen = new Pen(Ink, 2.6);
                drawingContext.DrawRoundedRectangle(null, pen, new Rect(center.X - 4, center.Y - 10, 13, 11), 2, 2);
                drawingContext.DrawRoundedRectangle(Fill, pen, new Rect(center.X - 9, center.Y - 3, 13, 11), 2, 2);
                break;
            }
            case PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right:
            {
                var angle = Button switch { PadButton.Up => 0, PadButton.Right => 90, PadButton.Down => 180, _ => 270 };
                drawingContext.PushTransform(new RotateTransform(angle, center.X, center.Y));
                drawingContext.DrawGeometry(Ink, null, Geometry.Parse(FormattableString.Invariant(
                    $"M{center.X},{center.Y - 10} L{center.X + 10},{center.Y + 7} H{center.X - 10} Z")));
                drawingContext.Pop();
                break;
            }
            default:
            {
                var letter = Format(Text, Text.Length > 1 ? 22 : 26, Ink);
                drawingContext.DrawText(letter, new Point(center.X - letter.Width / 2, center.Y - letter.Height / 2));
                break;
            }
        }
    }
}

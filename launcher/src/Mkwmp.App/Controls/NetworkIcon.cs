// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Mkwmp.App.Platform;

namespace Mkwmp.App.Controls;

/// <summary>
/// The top bar's link icon, drawn: Wi-Fi as a dot and three arcs (the arcs above
/// <see cref="Bars"/> faded), wired as a small network jack, offline as faded Wi-Fi with a
/// slash. 44 x 36.
/// </summary>
public sealed class NetworkIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(NetworkKind), typeof(NetworkIcon),
        new FrameworkPropertyMetadata(NetworkKind.Wifi, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(nameof(Bars), typeof(int), typeof(NetworkIcon),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(NetworkIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public NetworkKind Kind
    {
        get => (NetworkKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public int Bars
    {
        get => (int)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(44, 36);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var faded = Foreground.CloneCurrentValue();
        faded.Opacity = 0.28;
        if (Kind == NetworkKind.Wired)
        {
            DrawJack(drawingContext);
            return;
        }
        var offline = Kind == NetworkKind.Offline;
        var bars = offline ? 0 : Math.Clamp(Bars, 0, 3);
        var centre = new Point(22, 31);
        drawingContext.DrawEllipse(offline ? faded : Foreground, null, centre, 3.2, 3.2);
        for (var i = 1; i <= 3; i++)
        {
            var pen = new Pen(i <= bars ? Foreground : faded, 3.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawingContext.DrawGeometry(null, pen, Arc(centre, 7.5 * i));
        }
        if (offline)
        {
            var slash = new Pen(Foreground, 3.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawingContext.DrawLine(slash, new Point(8, 4), new Point(36, 33));
        }
    }

    /// <summary>A 90° arc opening upwards around <paramref name="c"/>.</summary>
    private static StreamGeometry Arc(Point c, double r)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            var a = Math.PI / 4;
            ctx.BeginFigure(new Point(c.X - (r * Math.Sin(a)), c.Y - (r * Math.Cos(a))), false, false);
            ctx.ArcTo(new Point(c.X + (r * Math.Sin(a)), c.Y - (r * Math.Cos(a))), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private void DrawJack(DrawingContext dc)
    {
        var pen = new Pen(Foreground, 3) { LineJoin = PenLineJoin.Round };
        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(9, 9), true, true);
            ctx.PolyLineTo([new Point(35, 9), new Point(35, 26), new Point(28, 26), new Point(28, 31), new Point(16, 31), new Point(16, 26), new Point(9, 26)], true, true);
        }
        body.Freeze();
        dc.DrawGeometry(null, pen, body);
        for (var x = 15; x <= 29; x += 7)
        {
            // three contact pins
            dc.DrawRectangle(Foreground, null, new Rect(x - 1, 13, 2.5, 5));
        }
    }
}

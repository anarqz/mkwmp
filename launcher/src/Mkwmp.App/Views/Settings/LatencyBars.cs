// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Media;

namespace Mkwmp.App.Views.Settings;

/// <summary>
/// A server's latency as three rising bars: 3 lit (green) under 60 ms, 2 (amber) under 150 ms,
/// 1 (red) above, none when it did not answer. Drawn here: no font has to carry the glyphs.
/// </summary>
public sealed class LatencyBars : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(int), typeof(LatencyBars), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public LatencyBars()
    {
        Width = 46;
        Height = 34;
        // the palette is swapped at runtime (light / dark): draw again with the new brushes
        Loaded += (_, _) => InvalidateVisual();
    }

    public int Bars
    {
        get => (int)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var lit = Bars switch { >= 3 => "Brush.Good", 2 => "Brush.Warn", _ => "Brush.Bad" };
        var (w, h) = (ActualWidth, ActualHeight);
        var bar = w / 4.2;
        for (var i = 0; i < 3; i++)
        {
            var height = h * (0.42 + (0.29 * i));
            var brush = TryFindResource(i < Bars ? lit : "Brush.Track") as Brush ?? Brushes.Gray;
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(i * (bar + (bar * 0.6)), h - height, bar, height), 2.5, 2.5);
        }
    }
}

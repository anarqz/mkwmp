// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Mkwmp.App.Controls;

/// <summary>
/// One of the vector icons of Themes/Icons.xaml: <c>&lt;c:Icon Data="{StaticResource Icon.Mic}" Width="40" Height="40"/&gt;</c>.
/// Line icons are stroked in the inherited text colour (<see cref="Foreground"/>); the
/// <c>IconFill.*</c> ones need <see cref="Filled"/>. Geometry is on a 24 x 24 grid.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(Icon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Icon() => IsHitTestVisible = false;

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    /// <summary>In grid units (the icon is 24 wide): 2 by default.</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 32 : Width, double.IsNaN(Height) ? 32 : Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (Data is not { } data || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        var scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        if (Filled)
        {
            drawingContext.DrawGeometry(Foreground, null, data);
        }
        else
        {
            var pen = new Pen(Foreground, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            drawingContext.DrawGeometry(null, pen, data);
        }
        drawingContext.Pop();
        drawingContext.Pop();
    }
}

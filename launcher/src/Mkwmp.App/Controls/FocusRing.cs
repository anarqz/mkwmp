// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Mkwmp.App.Controls;

/// <summary>
/// THE focus indicator: a card (<see cref="Fill"/>) with a soft multi-colour gradient border that
/// slowly turns, and a faint halo. Put it behind the content in a control template and bind
/// <see cref="IsActive"/> to the control's <c>IsFocused</c>; it draws nothing while inactive.
/// A negative <c>Margin</c> makes the card larger than the control (rows do that).
/// </summary>
public sealed class FocusRing : FrameworkElement
{
    private static readonly Color Blue = Color.FromRgb(0x2F, 0x74, 0xFF);
    private static readonly Color Violet = Color.FromRgb(0x9A, 0x6B, 0xFF);
    private static readonly Color Pink = Color.FromRgb(0xFF, 0x8B, 0xC2);
    private static readonly Color Cyan = Color.FromRgb(0x58, 0xD3, 0xFF);

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(FocusRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FocusRing)d).OnActiveChanged()));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(FocusRing),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(FocusRing),
        new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(FocusRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private LinearGradientBrush? _brush;
    private RotateTransform? _turn;

    public FocusRing()
    {
        IsHitTestVisible = false;
        Unloaded += (_, _) => _turn?.BeginAnimation(RotateTransform.AngleProperty, null);
        Loaded += (_, _) => OnActiveChanged();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The card's corner radius; anything at least half the size gives a circle.</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>The card under the content (<c>Brush.Focus.Fill</c>); null for the ring alone.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    private void OnActiveChanged()
    {
        if (IsActive && IsLoaded)
        {
            EnsureBrush();
            var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(5)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(turn, 30); // a slow turn: half the frames look the same and cost half
            _turn!.BeginAnimation(RotateTransform.AngleProperty, turn);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(140)));
        }
        else
        {
            _turn?.BeginAnimation(RotateTransform.AngleProperty, null);
            BeginAnimation(OpacityProperty, null);
        }
    }

    private void EnsureBrush()
    {
        if (_brush is not null)
        {
            return;
        }
        _turn = new RotateTransform(0, 0.5, 0.5);
        _brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            RelativeTransform = _turn,
            GradientStops =
            {
                new GradientStop(Blue, 0), new GradientStop(Cyan, 0.22), new GradientStop(Blue, 0.42),
                new GradientStop(Violet, 0.62), new GradientStop(Pink, 0.82), new GradientStop(Blue, 1),
            },
        };
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (!IsActive || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        EnsureBrush();
        var thickness = Thickness;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var radius = Math.Min(CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2);

        // the halo: two wide, faint strokes of the same gradient (no blur effect: cheap under Wine too)
        drawingContext.PushOpacity(0.13);
        drawingContext.DrawRoundedRectangle(null, new Pen(_brush, thickness + 14), bounds, radius, radius);
        drawingContext.Pop();
        drawingContext.PushOpacity(0.22);
        drawingContext.DrawRoundedRectangle(null, new Pen(_brush, thickness + 6), bounds, radius, radius);
        drawingContext.Pop();

        if (Fill is { } fill)
        {
            drawingContext.DrawRoundedRectangle(fill, null, bounds, radius, radius);
        }
        var inset = thickness / 2;
        var ring = new Rect(inset, inset, Math.Max(0, bounds.Width - thickness), Math.Max(0, bounds.Height - thickness));
        var inner = Math.Max(0, radius - inset);
        drawingContext.DrawRoundedRectangle(null, new Pen(_brush, thickness), ring, inner, inner);
    }
}

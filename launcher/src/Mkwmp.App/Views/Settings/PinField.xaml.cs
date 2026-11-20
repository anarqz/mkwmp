// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Mkwmp.App.Views.Settings;

/// <summary>
/// A PIN as it is shown anywhere in the launcher: masked (six dots) until the eye button, or
/// whatever is bound to <see cref="IsRevealed"/>, shows it; masked again as soon as it is no
/// longer on screen. The one control for it: never bind a PIN to a TextBlock.
/// </summary>
public partial class PinField : UserControl
{
    public static readonly DependencyProperty PinProperty = DependencyProperty.Register(
        nameof(Pin), typeof(string), typeof(PinField), new PropertyMetadata("", (d, _) => ((PinField)d).Draw()));

    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(PinField),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PinField)d).Draw()));

    public static readonly DependencyProperty DigitSizeProperty = DependencyProperty.Register(
        nameof(DigitSize), typeof(double), typeof(PinField), new PropertyMetadata(46.0, (d, _) => ((PinField)d).Draw()));

    public static readonly DependencyProperty ShowEyeProperty = DependencyProperty.Register(
        nameof(ShowEye), typeof(bool), typeof(PinField), new PropertyMetadata(true));

    public PinField()
    {
        InitializeComponent();
        // Leaving the screen in any way hides it again: the next time it is seen, it is dots.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                Hide();
            }
        };
        Unloaded += (_, _) => Hide();
        Draw();
    }

    /// <summary>The PIN's digits ("482913").</summary>
    public string Pin
    {
        get => (string)GetValue(PinProperty);
        set => SetValue(PinProperty, value);
    }

    /// <summary>The digits are shown. False by default, and again after the control left the screen.</summary>
    public bool IsRevealed
    {
        get => (bool)GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    /// <summary>The font size of the digits; the dots follow it.</summary>
    public double DigitSize
    {
        get => (double)GetValue(DigitSizeProperty);
        set => SetValue(DigitSizeProperty, value);
    }

    /// <summary>The eye button beside the PIN (false where a button of the page does it instead).</summary>
    public bool ShowEye
    {
        get => (bool)GetValue(ShowEyeProperty);
        set => SetValue(ShowEyeProperty, value);
    }

    /// <summary>"482 913": two groups, as a person reads a PIN out.</summary>
    public static string Spaced(string? pin) => pin is { Length: 6 } ? $"{pin[..3]} {pin[3..]}" : pin ?? "";

    private void Hide()
    {
        if (IsRevealed)
        {
            SetCurrentValue(IsRevealedProperty, false);
        }
    }

    private void OnEye(object sender, RoutedEventArgs e) => SetCurrentValue(IsRevealedProperty, !IsRevealed);

    private void Draw()
    {
        var pin = Pin ?? "";
        var shown = IsRevealed && pin.Length > 0;
        // The digits are laid out only while shown: hidden, their text is a stand-in of the same width (no PIN in the tree).
        Digits.Text = shown ? Spaced(pin) : "000 000";
        Digits.Visibility = shown ? Visibility.Visible : Visibility.Hidden;
        Dots.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        EyeIcon.Data = (Geometry)FindResource(IsRevealed ? "Icon.EyeOff" : "Icon.Eye");
        if (Dots.Children.Count != 6 || Dots.Tag is not double size || Math.Abs(size - DigitSize) > 0.01)
        {
            Dots.Children.Clear();
            Dots.Tag = DigitSize;
            var dot = Math.Round(DigitSize * 0.3);
            for (var i = 0; i < 6; i++)
            {
                var ellipse = new Ellipse { Width = dot, Height = dot, Margin = new Thickness(i == 3 ? dot * 1.3 : i == 0 ? 0 : dot * 0.62, 0, 0, 0) };
                ellipse.SetResourceReference(Shape.FillProperty, "Brush.Accent");
                Dots.Children.Add(ellipse);
            }
        }
    }
}

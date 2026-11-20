// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mkwmp.App.Input;

namespace Mkwmp.App.Views.Settings;

/// <summary>
/// The box a PIN is typed into: a password box (dots, also on the on-screen keyboard) and, while
/// the eye is on, a plain box with the same digits. See <see cref="PinField"/> for a PIN that is shown.
/// </summary>
public partial class PinEntry : UserControl
{
    public static readonly DependencyProperty PinProperty = DependencyProperty.Register(
        nameof(Pin), typeof(string), typeof(PinEntry),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PinEntry)d).FromPin()));

    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(PinEntry),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PinEntry)d).Swap()));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(PinEntry), new PropertyMetadata(""));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PinEntry), new PropertyMetadata(""));

    private bool _syncing;

    public PinEntry()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false && IsRevealed)
            {
                SetCurrentValue(IsRevealedProperty, false);
            }
        };
        Swap();
    }

    /// <summary>The digits typed so far (at most six).</summary>
    public string Pin
    {
        get => (string)GetValue(PinProperty);
        set => SetValue(PinProperty, value);
    }

    public bool IsRevealed
    {
        get => (bool)GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    /// <summary>What the empty box says ("PIN (6 digits)").</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>The on-screen keyboard's title.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static string Clean(string? text) => new([.. (text ?? "").Where(char.IsAsciiDigit).Take(6)]);

    private void OnMasked(object sender, RoutedEventArgs e) => FromBox(Masked.Password);

    private void OnPlain(object sender, TextChangedEventArgs e) => FromBox(Plain.Text);

    private void FromBox(string typed)
    {
        if (_syncing)
        {
            return;
        }
        SetCurrentValue(PinProperty, Clean(typed));
        FromPin(); // the box shows what was kept (a letter typed on a real keyboard is dropped), and the other box follows
    }

    private void FromPin()
    {
        var pin = Clean(Pin);
        _syncing = true;
        try
        {
            if (Masked.Password != pin)
            {
                Masked.Password = pin;
            }
            if (Plain.Text != pin)
            {
                Plain.Text = pin;
                Plain.CaretIndex = pin.Length;
            }
        }
        finally
        {
            _syncing = false;
        }
        HintText.Visibility = pin.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEye(object sender, RoutedEventArgs e) => SetCurrentValue(IsRevealedProperty, !IsRevealed);

    /// <summary>The box that is shown takes the other's place, and its focus when it had it.</summary>
    private void Swap()
    {
        var revealed = IsRevealed;
        UIElement from = revealed ? Masked : Plain;
        UIElement to = revealed ? Plain : Masked;
        var focused = from.IsKeyboardFocusWithin || ReferenceEquals(FocusNav.Current(this), from);
        to.Visibility = Visibility.Visible;
        from.Visibility = Visibility.Collapsed;
        EyeIcon.Data = (Geometry)FindResource(revealed ? "Icon.EyeOff" : "Icon.Eye");
        if (focused)
        {
            FocusNav.Focus(to);
        }
    }
}

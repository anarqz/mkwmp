// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Input;

/// <summary>
/// Text entry without a keyboard. Every <c>TextBox</c> and <c>PasswordBox</c> of the launcher gets
/// the on-screen keyboard by itself: A on the focused box (controller), or a touch on it, opens
/// it; OK writes the text back (through the box's binding) and raises <see cref="SubmittedEvent"/>.
/// With a real keyboard the box is typed into directly and Enter raises the same event.
/// Attached properties (all optional):
/// <list type="bullet">
/// <item><c>input:TextEntry.Title</c>: what is being typed, shown above the keyboard's text.</item>
/// <item><c>input:TextEntry.Charset</c>: <c>Any</c> / <c>Gamertag</c> / <c>Digits</c>; the other keys are greyed out.</item>
/// </list>
/// The box's own <c>MaxLength</c> is the keyboard's limit.
/// </summary>
public static class TextEntry
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.RegisterAttached("Title", typeof(string), typeof(TextEntry), new PropertyMetadata(""));

    public static readonly DependencyProperty CharsetProperty =
        DependencyProperty.RegisterAttached("Charset", typeof(KeyboardCharset), typeof(TextEntry), new PropertyMetadata(KeyboardCharset.Any));

    /// <summary>Enter in the box, or OK on the on-screen keyboard. Bubbles: <c>input:TextEntry.Submitted="OnSubmitted"</c>.</summary>
    public static readonly RoutedEvent SubmittedEvent =
        EventManager.RegisterRoutedEvent("Submitted", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TextEntry));

    private static bool s_registered;

    public static string GetTitle(DependencyObject o) => (string)(o ?? throw new ArgumentNullException(nameof(o))).GetValue(TitleProperty);
    public static void SetTitle(DependencyObject o, string value) => (o ?? throw new ArgumentNullException(nameof(o))).SetValue(TitleProperty, value);
    public static KeyboardCharset GetCharset(DependencyObject o) => (KeyboardCharset)(o ?? throw new ArgumentNullException(nameof(o))).GetValue(CharsetProperty);
    public static void SetCharset(DependencyObject o, KeyboardCharset value) => (o ?? throw new ArgumentNullException(nameof(o))).SetValue(CharsetProperty, value);

    public static void AddSubmittedHandler(DependencyObject o, RoutedEventHandler handler) => (o as UIElement)?.AddHandler(SubmittedEvent, handler);
    public static void RemoveSubmittedHandler(DependencyObject o, RoutedEventHandler handler) => (o as UIElement)?.RemoveHandler(SubmittedEvent, handler);

    /// <summary>
    /// Once per process (the on-screen keyboard control does it): a touch on any text box opens the
    /// keyboard, and so does a click where touch arrives as the mouse (the Steam Deck's Game Mode).
    /// </summary>
    public static void RegisterClassHandlers()
    {
        if (s_registered)
        {
            return;
        }
        s_registered = true;
        foreach (var type in (Type[])[typeof(TextBox), typeof(PasswordBox)])
        {
            EventManager.RegisterClassHandler(type, UIElement.TouchUpEvent,
                new EventHandler<System.Windows.Input.TouchEventArgs>((sender, _) => OpenFor(sender, touch: true)), true);
            EventManager.RegisterClassHandler(type, UIElement.MouseLeftButtonUpEvent,
                new System.Windows.Input.MouseButtonEventHandler((sender, e) =>
                {
                    if (e.StylusDevice is null) // a real touch was handled above
                    {
                        OpenFor(sender, touch: false);
                    }
                }), true);
        }

        static void OpenFor(object? sender, bool touch)
        {
            if (Tap.Dragged || sender is not UIElement { IsEnabled: true } box || Window.GetWindow(box)?.DataContext is not ShellViewModel shell || shell.IsKeyboardOpen)
            {
                return;
            }
            // gamescope and Wine deliver a touch as the mouse: there a click counts as one (in Game Mode
            // always; on a Linux desktop only while a controller is connected, a sign of a handheld)
            if (touch || Desktop.DesktopEnvironment.IsGameMode || (Desktop.DesktopEnvironment.IsWine && shell.GamepadConnected))
            {
                Open(box, shell);
            }
        }
    }

    /// <summary>
    /// A / Enter on a focused stop (the window calls this): for a text box, Enter on a real keyboard
    /// submits it, A on a controller opens the on-screen keyboard. False when it is not a text box.
    /// </summary>
    public static bool Activate(UIElement? element, ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (element is not (TextBox or PasswordBox))
        {
            return false;
        }
        if (shell.InputKind == InputKind.Keyboard)
        {
            element.RaiseEvent(new RoutedEventArgs(SubmittedEvent, element));
        }
        else
        {
            Open(element, shell);
        }
        return true;
    }

    /// <summary>Opens the on-screen keyboard for a text box (nothing for a read-only one).</summary>
    public static void Open(UIElement element, ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(shell);
        var (text, max, masked) = element switch
        {
            TextBox { IsReadOnly: false } box => (box.Text, box.MaxLength, false),
            PasswordBox password => (password.Password, password.MaxLength, true),
            _ => (null, 0, false),
        };
        if (text is null)
        {
            return;
        }
        shell.OpenKeyboard(new KeyboardViewModel(shell, GetTitle(element), text, max, GetCharset(element), masked, typed =>
        {
            switch (element)
            {
                case TextBox box:
                    box.SetCurrentValue(TextBox.TextProperty, typed);
                    BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource(); // also for a LostFocus binding
                    box.CaretIndex = typed.Length;
                    break;
                case PasswordBox password:
                    password.Password = typed;
                    break;
            }
            element.RaiseEvent(new RoutedEventArgs(SubmittedEvent, element));
        }));
    }
}

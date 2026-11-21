// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Mkwmp.App.Controls;
using Mkwmp.App.Input;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App;

/// <summary>
/// The one window: the stage (scaled to 1080 units of height), its layers, and all input. The
/// controller, the keyboard, the mouse and touch end in the same three things: move the focus,
/// activate the focused stop, or a button for the shell (<see cref="ShellViewModel.HandleButton"/>).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The gamepad timer is stopped when the window closes.")]
public partial class MainWindow : Window
{
    private const double StageHeight = 1080;
    private const double MinStageWidth = 1600;

    private readonly ShellViewModel _vm;
    private readonly GamepadInput _gamepad = new();
    private readonly DispatcherTimer _focusGuard;
    private readonly Dictionary<FrameworkElement, WeakReference<UIElement>?> _focusBelow = [];
    private FrameworkElement? _lastRoot;
    private Point? _lastMouse;
    private readonly bool _windowed;
    private bool _harness;

    public MainWindow(ShellViewModel shell, string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        _vm = shell ?? throw new ArgumentNullException(nameof(shell));
        _windowed = args.Contains("--windowed", StringComparer.OrdinalIgnoreCase); // this run only; the setting stays
        InitializeComponent();
        DataContext = shell;

        Root.SizeChanged += (_, _) => FitStage();
        Stage.SizeChanged += (_, _) => ApplyFx(); // the overlay's picture follows the stage (its first frame is drawn at the first layout)
        AddHandler(GotFocusEvent, new RoutedEventHandler(OnAnyGotFocus), true);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        PreviewMouseDown += (_, _) => _vm.InputKind = InputKind.Mouse;
        PreviewTouchDown += (_, _) => _vm.InputKind = InputKind.Mouse;
        PreviewMouseMove += OnPreviewMouseMove;
        Activated += (_, _) =>
        {
            _gamepad.Enabled = true;
            _vm.WindowActivated();
        };
        // Another program in front, the overlay given back to the game, the tray: a mic or camera test stops.
        Deactivated += (_, _) =>
        {
            _gamepad.Enabled = false;
            _vm.WindowDeactivated();
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                _vm.WindowDeactivated();
            }
        };

        // xml:lang: how bindings format, and which glyph variants the system's fallback fonts give
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(shell.S.Culture.IetfLanguageTag);
        shell.LanguageChanged += (_, _) => Language = System.Windows.Markup.XmlLanguage.GetLanguage(shell.S.Culture.IetfLanguageTag);
        shell.PropertyChanged += OnShellPropertyChanged;
        shell.QuitRequested += (_, _) => Close();
        shell.ButtonSink = button =>
        {
            OnButton(button);
            return true;
        };
        _gamepad.Pressed += (_, button) =>
        {
            _vm.InputKind = InputKind.Gamepad;
            OnButton(button);
        };
        _gamepad.ConnectedChanged += (_, _) => _vm.GamepadConnected = _gamepad.Connected;
        _gamepad.Scrolled += (_, step) =>
        {
            _vm.InputKind = InputKind.Gamepad;
            ScrollWithStick(step);
        };
        // A / B follow the game's button layout (MainWindow.Pad.cs); the first read off this thread (it reads the game's files)
        shell.Launcher.Changed += OnLauncherChangedForPad;
        Task.Run(FollowPadLayout);

        // the focus is never lost: whatever happened to the focused control, a stop of the top layer has it again
        _focusGuard = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _focusGuard.Tick += (_, _) => SyncFocus();

        Closed += (_, _) =>
        {
            _focusGuard.Stop();
            _vm.Launcher.Changed -= OnLauncherChangedForPad;
            _gamepad.Dispose();
            _vm.Dispose();
        };

        var handled = false;
        ConfigureHarness(args, ref handled);
        _harness = handled;
        if (handled)
        {
            return;
        }

        ApplyFullscreen();
        // The welcome flow until it was finished once (or was never needed: Core marks a working online profile as done).
        // Offline is a complete state: no consent is asked for to reach home.
        shell.Nav.ResetTo(StartRoute(shell));
        if (App.Option(args, "--page") is { } page)
        {
            shell.Navigate(page);
        }
        Loaded += (_, _) =>
        {
            _gamepad.Start();
            _focusGuard.Start();
            SyncFocus();
        };
    }

    partial void ConfigureHarness(string[] args, ref bool handled);

    /// <summary>The page the window opens on: the welcome flow until it was finished (or was never needed), else home.</summary>
    internal static string StartRoute(ShellViewModel shell) => shell.Settings.Ui.SetupDone ? Routes.Home : Routes.Setup;

    // ── stage ──

    private void FitStage()
    {
        if (Root.ActualHeight <= 0 || Root.ActualWidth <= 0)
        {
            return;
        }
        // 1080 units of height; a screen narrower than 1600 units (4:3, a tall window) scales by its width instead
        var scale = Math.Min(Root.ActualHeight / StageHeight, Root.ActualWidth / MinStageWidth);
        StageScale.ScaleX = scale;
        StageScale.ScaleY = scale;
        Stage.Width = Root.ActualWidth / scale;
        Stage.Height = Root.ActualHeight / scale;
    }

    private void ApplyFullscreen()
    {
        if (_harness)
        {
            return;
        }
        if (_vm.IsFullscreen && !_windowed)
        {
            WindowState = WindowState.Normal; // so that maximizing covers the task bar
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
        }
    }

    // ── layers and focus ──

    /// <summary>The layer that has the input now: modal, sheet, friends panel, else the page.</summary>
    private FrameworkElement ActiveRoot =>
        _vm.IsKeyboardOpen ? KeyboardLayer : _vm.IsPickerOpen ? PickerLayer
        : _vm.IsModalOpen ? ModalLayer : _vm.IsSheetOpen ? SheetLayer : _vm.IsFriendsOpen ? FriendsLayer : BaseLayer;

    private int DepthOf(FrameworkElement? root) =>
        ReferenceEquals(root, KeyboardLayer) ? 5
        : ReferenceEquals(root, PickerLayer) ? 4
        : ReferenceEquals(root, ModalLayer) ? 3 : ReferenceEquals(root, SheetLayer) ? 2 : ReferenceEquals(root, FriendsLayer) ? 1 : 0;

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.IsFullscreen):
                ApplyFullscreen();
                break;
            case nameof(ShellViewModel.Modal) or nameof(ShellViewModel.Sheet) or nameof(ShellViewModel.IsFriendsOpen) or nameof(ShellViewModel.Keyboard)
                or nameof(ShellViewModel.IsPickerOpen):
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SyncFocus);
                break;
            case nameof(ShellViewModel.InputKind):
                Mouse.OverrideCursor = _vm.InputKind == InputKind.Gamepad ? Cursors.None : null;
                if (_vm.IsKeyboardOpen)
                {
                    _vm.RefreshHints(); // the on-screen keyboard's hints follow what types into it
                }
                break;
        }
    }

    /// <summary>
    /// Puts the focus where it belongs: into a layer that just opened (remembering where it was),
    /// back to that place when the layer closed, and onto a default stop whenever the focused
    /// control is gone.
    /// </summary>
    private void SyncFocus()
    {
        var root = ActiveRoot;
        var current = FocusNav.Current(this);
        _lastRoot ??= BaseLayer;
        if (!ReferenceEquals(root, _lastRoot))
        {
            var opened = DepthOf(root) > DepthOf(_lastRoot);
            var previous = _lastRoot;
            _lastRoot = root;
            if (opened)
            {
                _focusBelow[root] = current is null ? null : new WeakReference<UIElement>(current);
            }
            else if (_focusBelow.Remove(previous, out var saved) && saved is not null && saved.TryGetTarget(out var back)
                     && FocusNav.IsAlive(back, root) && FocusNav.Focus(back))
            {
                return;
            }
        }
        if (!FocusNav.IsAlive(FocusNav.Current(this), root))
        {
            root.UpdateLayout();
            FocusNav.FocusDefault(root);
        }
    }

    private void OnAnyGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is UIElement element)
        {
            FocusNav.Remember(element);
            if (_vm.InputKind != InputKind.Mouse)
            {
                (element as FrameworkElement)?.BringIntoView();
            }
        }
    }

    private void OnFriendsScrim(object sender, MouseButtonEventArgs e) => _vm.IsFriendsOpen = false;

    private void OnPickerScrim(object sender, MouseButtonEventArgs e) => _vm.CancelPicker();

    private void OnSheetScrim(object sender, MouseButtonEventArgs e) => _vm.CloseSheet();

    // ── input ──

    /// <summary>A button, from the controller, the keyboard or a tap on a hint.</summary>
    private void OnButton(PadButton button)
    {
        if (IsOverlayAnimating)
        {
            return; // the game's picture is on its way in or out
        }
        switch (button)
        {
            case PadButton.Up:
                Move(NavDirection.Up);
                break;
            case PadButton.Down:
                Move(NavDirection.Down);
                break;
            case PadButton.Left:
                Move(NavDirection.Left);
                break;
            case PadButton.Right:
                Move(NavDirection.Right);
                break;
            case PadButton.A:
                ActivateFocused();
                break;
            default:
                _vm.HandleButton(button);
                break;
        }
    }

    private void Move(NavDirection direction)
    {
        var root = ActiveRoot;
        var current = FocusNav.Current(this);
        if (current is ConsoleSlider slider && FocusNav.IsAlive(slider, root) && direction is NavDirection.Left or NavDirection.Right)
        {
            slider.Nudge(direction == NavDirection.Right ? 1 : -1);
            return;
        }
        // a virtualized list (the file picker's) goes line by line by itself: the lines off screen do not exist as stops
        if (direction is NavDirection.Up or NavDirection.Down && current is ListBoxItem line && FocusNav.IsAlive(line, root)
            && ItemsControl.ItemsControlFromItemContainer(line) is EntryList entries && entries.Step(line, direction == NavDirection.Down ? 1 : -1))
        {
            return;
        }
        FocusNav.Move(root, direction);
    }

    /// <summary>A / Enter on the focused stop: a click for a button, "go in" for a list entry.</summary>
    private void ActivateFocused()
    {
        var root = ActiveRoot;
        var current = FocusNav.Current(this);
        if (!FocusNav.IsAlive(current, root))
        {
            FocusNav.FocusDefault(root);
            return;
        }
        switch (current)
        {
            case ButtonBase button when PresentationSource.FromVisual(button) is { } source:
                // the button's own key handling does the click: Command, Click, toggling, all as with a real key
                button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
                button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
                break;
            case ListBoxItem line when ItemsControl.ItemsControlFromItemContainer(line) is EntryList entries:
                entries.Activate(line); // a line of the file picker: open the folder, pick the file
                break;
            case ListBoxItem:
                FocusNav.Move(root, NavDirection.Right);
                break;
            case TextBox or PasswordBox:
                TextEntry.Activate(current, _vm); // the on-screen keyboard for a controller; Enter submits
                break;
        }
    }

    /// <summary>
    /// A physical keyboard while the on-screen one is open: it types there. Under Wine a click on a text box opens the
    /// on-screen keyboard whenever a pad is connected (the Steam Deck's own always is), and its keys used to be the
    /// window's shortcuts (Q shift, F space, X backspace, Backspace and Esc cancel): a path could not be typed.
    /// Characters come as text (<see cref="OnPreviewTextInput"/>); here only the keys that are not text. Esc still
    /// cancels, the arrows still move over the keys.
    /// </summary>
    private bool OnKeyboardKey(KeyEventArgs e)
    {
        if (_vm.Keyboard is not { } keyboard || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            return false;
        }
        switch (e.Key)
        {
            case Key.Back:
                keyboard.Erase();
                break;
            case Key.Enter:
                keyboard.Accept();
                break;
            case Key.Space:
                keyboard.TypeText(" "); // handled here, or the focused key's button would take it as a press
                break;
            case Key.F or Key.X or Key.Q or Key.E:
                return true; // letters: not the shortcuts; their text comes as TextInput
            default:
                return false;
        }
        e.Handled = true;
        return true;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_vm.Keyboard is { } keyboard && e.Text is { Length: > 0 } text && !text.Any(char.IsControl))
        {
            _vm.InputKind = InputKind.Keyboard;
            keyboard.TypeText(text);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (OnKeyboardKey(e))
        {
            _vm.InputKind = InputKind.Keyboard;
            return;
        }
        var typing = Keyboard.FocusedElement is TextBoxBase or PasswordBox;
        PadButton? button = e.Key switch
        {
            Key.Up => PadButton.Up,
            Key.Down => PadButton.Down,
            Key.Left when !typing => PadButton.Left,
            Key.Right when !typing => PadButton.Right,
            Key.Enter => PadButton.A,
            Key.Escape => PadButton.B,
            Key.Back when !typing => PadButton.B,
            Key.F when !typing => PadButton.Y,
            Key.X when !typing => PadButton.X,
            Key.Q when !typing => PadButton.LB,
            Key.E when !typing => PadButton.RB,
            Key.F1 => PadButton.Start,
            Key.F2 => PadButton.Back,
            _ => null,
        };
        if (e.Key == Key.F9 && _vm.Launcher is Design.DesignLauncher && OverlayControl is { } overlay)
        {
            overlay.Simulate(); // the demo's game asks for the overlay (and, the second time, gets the screen back)
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11)
        {
            _ = _vm.SetFullscreenAsync(!_vm.IsFullscreen);
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.System))
        {
            _vm.InputKind = InputKind.Keyboard;
        }
        if (button is { } pressed && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            OnButton(pressed);
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // only a real movement counts: WPF also raises this when the layout under a resting cursor changes
        var at = e.GetPosition(this);
        if (_lastMouse is not { } last)
        {
            _lastMouse = at;
        }
        else if (Math.Abs(at.X - last.X) + Math.Abs(at.Y - last.Y) > 6)
        {
            _lastMouse = at;
            if (_vm.InputKind == InputKind.Gamepad)
            {
                _vm.InputKind = InputKind.Mouse; // the cursor comes back
            }
        }
    }
}

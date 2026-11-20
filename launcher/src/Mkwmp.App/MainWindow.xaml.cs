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
        AddHandler(GotFocusEvent, new RoutedEventHandler(OnAnyGotFocus), true);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, _) => _vm.InputKind = InputKind.Mouse;
        PreviewTouchDown += (_, _) => _vm.InputKind = InputKind.Mouse;
        PreviewMouseMove += OnPreviewMouseMove;
        Activated += (_, _) => _gamepad.Enabled = true;
        Deactivated += (_, _) => _gamepad.Enabled = false;

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

        // the focus is never lost: whatever happened to the focused control, a stop of the top layer has it again
        _focusGuard = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _focusGuard.Tick += (_, _) => SyncFocus();

        Closed += (_, _) =>
        {
            _focusGuard.Stop();
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
        shell.Nav.ResetTo(shell.Settings.Ui.SetupDone ? Routes.Home : Routes.Setup);
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
        _vm.IsModalOpen ? ModalLayer : _vm.IsSheetOpen ? SheetLayer : _vm.IsFriendsOpen ? FriendsLayer : BaseLayer;

    private int DepthOf(FrameworkElement? root) =>
        ReferenceEquals(root, ModalLayer) ? 3 : ReferenceEquals(root, SheetLayer) ? 2 : ReferenceEquals(root, FriendsLayer) ? 1 : 0;

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.IsFullscreen):
                ApplyFullscreen();
                break;
            case nameof(ShellViewModel.Modal) or nameof(ShellViewModel.Sheet) or nameof(ShellViewModel.IsFriendsOpen):
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SyncFocus);
                break;
            case nameof(ShellViewModel.InputKind):
                Mouse.OverrideCursor = _vm.InputKind == InputKind.Gamepad ? Cursors.None : null;
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

    private void OnSheetScrim(object sender, MouseButtonEventArgs e) => _vm.CloseSheet();

    // ── input ──

    /// <summary>A button, from the controller, the keyboard or a tap on a hint.</summary>
    private void OnButton(PadButton button)
    {
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
            case ListBoxItem:
                FocusNav.Move(root, NavDirection.Right);
                break;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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

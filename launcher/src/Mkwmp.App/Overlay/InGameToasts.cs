// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mkwmp.App.Controls;
using Mkwmp.App.ViewModels;
using Mkwmp.Core.Overlay;
using Mkwmp.Core.Platform;

namespace Mkwmp.App.Overlay;

/// <summary>
/// The launcher's toasts over a running game, while the launcher's window is hidden behind it: the same
/// <see cref="ToastHost"/> list (<see cref="ShellViewModel.Toasts"/>), the same cards, the same times, no sound. Ported
/// from Infinity's v4 overlay ("the game draws, the launcher decides").
/// <list type="number">
/// <item><b>Drawn by the game</b> (docs/contracts/LAUNCHER_API.md "In-game toasts"): the MKWMP mod asks
/// GET /local/v1/toasts and draws them in the game's own frame, at the top right, on Windows and under Proton, also over
/// an exclusive full screen; the game keeps running and keeps the focus. This class publishes what the ToastHost shows
/// to the <see cref="ToastBridge"/> (none while the launcher's window is visible: its own ToastHost has them), and
/// carries out what the game asks (a dismiss).</item>
/// <item><b>The window over the game</b>, the fallback while no mod polls (<see cref="ToastBridge.GameDraws"/>: none for 15 s):
/// a small window at the top right of the game's monitor, borderless, transparent, topmost and click-through, never taking the
/// focus or the foreground (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED, shown without activation, no
/// task bar entry). Windows only: under Wine / Proton (gamescope shows the window mapped last; a desktop's window manager may give
/// it the focus) it is off unless <c>MKWMP_INGAME_TOASTS=1</c>. An exclusive full-screen game is drawn over it.</item>
/// </list>
/// It also gives the game's overlay page the window's live theme and its clipboard (<see cref="GameOverlay"/>).
/// UI thread only.
/// </summary>
internal sealed class InGameToasts : IDisposable
{
    /// <summary>The window's content in stage units (1080 high on the monitor, as the launcher's stage): four toasts fit.</summary>
    private const double StageWidth = 700, StageHeight = 1080;

    /// <summary>The theme's colours the game draws the toasts with, by the role names of the route.</summary>
    private static readonly (string Role, string Key)[] PaletteKeys =
    [
        ("toast", "Brush.Toast"), ("rule", "Brush.Rule"), ("accent", "Brush.Accent"), ("good", "Brush.Good"), ("warn", "Brush.Warn"),
        ("bad", "Brush.Bad"), ("text", "Brush.Text"), ("textMuted", "Brush.TextMuted"), ("track", "Brush.Track"), ("glyph", "Brush.Glyph"),
        ("onGlyph", "Brush.OnGlyph"), ("onAccent", "Brush.OnAccent"), ("surface", "Brush.Surface"),
    ];

    private readonly ShellViewModel _shell;
    private readonly Window _main;
    private readonly Func<IntPtr> _gameWindow;
    private readonly Window _window;
    private readonly ToastBridge _bridge;
    private readonly GameOverlay _page;
    private readonly DispatcherTimer _timer;
    private bool _shown;
    private bool _updateQueued;
    private bool _pendingAdd;
    private bool _disposed;
    private bool _gameDrew;

    /// <param name="gameWindow">The game's window, for its monitor (zero: the foreground window's).</param>
    public InGameToasts(ShellViewModel shell, Window main, Func<IntPtr> gameWindow)
    {
        _shell = shell;
        _main = main;
        _gameWindow = gameWindow;
        _bridge = shell.Launcher.ToastBridge;
        _page = shell.Launcher.GameOverlay;
        _bridge.AttachWindow();
        _bridge.Requested += OnGameRequest;
        _page.CopyText = text => main.Dispatcher.Invoke(() => ShellViewModel.CopyText(text)); // the clipboard is the UI thread's
        shell.ToastsOverGame = true;
        _window = new Window
        {
            Title = "MKWMP notifications",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            IsHitTestVisible = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = StageWidth,
            Height = StageHeight,
            DataContext = shell,
            Content = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = new Grid { Width = StageWidth, Height = StageHeight, Children = { new ToastHost() } },
            },
        };
        _window.SourceInitialized += OnSourceInitialized;
        shell.Toasts.CollectionChanged += OnToastsChanged;
        main.IsVisibleChanged += OnMainChanged;
        main.StateChanged += OnMainChanged;
        // what the game draws follows the ToastHost within a tenth of a second; the bridge sends only changes
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    /// <summary>The window over the game: Windows; under Wine only when asked for (<c>MKWMP_INGAME_TOASTS=1</c>).</summary>
    public static bool Supported => !WineHost.Detected || Environment.GetEnvironmentVariable("MKWMP_INGAME_TOASTS") == "1";

    /// <summary>The demo's preview (<c>--ingame-toast</c>): the window shows with any toast, over the desktop, whatever the launcher's window does.</summary>
    public bool Forced { get; set; }

    /// <summary>The demo's fixture (<c>--ingame-toast</c>): published to the game as if the window were behind a game.</summary>
    public bool ForcedForGame { get; set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Stop();
        _bridge.Requested -= OnGameRequest;
        _bridge.Publish(GameToastSnapshot.Empty);
        _page.CopyText = null;
        _shell.ToastsOverGame = false;
        _shell.Toasts.CollectionChanged -= OnToastsChanged;
        _main.IsVisibleChanged -= OnMainChanged;
        _main.StateChanged -= OnMainChanged;
        _window.Close();
    }

    /// <summary>The launcher's window is hidden (or minimised) while a game runs, and not as the overlay.</summary>
    private bool BehindGame =>
        _shell.GameRuns && !_shell.IsOverlay && (!_main.IsVisible || _main.WindowState == WindowState.Minimized);

    private void OnToastsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Update(e.Action == NotifyCollectionChangedAction.Add);

    private void OnMainChanged(object? sender, EventArgs e) => Update(false);

    private void OnMainChanged(object? sender, DependencyPropertyChangedEventArgs e) => Update(false);

    private void OnTick()
    {
        Publish();
        _page.Palette = Read(GameOverlay.PaletteKeys); // the game's overlay page draws in the player's theme (only a change is sent)
        var draws = _bridge.GameDraws;
        if (draws != _gameDrew)
        {
            _gameDrew = draws;
            App.Log(draws ? "toasts: the game draws them (GET /local/v1/toasts): the window over the game stays hidden"
                          : "toasts: the game did not ask for 15 s: the window over the game shows them again");
            Update(false);
        }
    }

    /// <summary>
    /// Update once the collection's own notification has been delivered to everybody. Showing the window from inside it creates
    /// the window's ToastHost, whose list then reads the collection with the new toast already in it and is told about it
    /// again: the first toast would show twice.
    /// </summary>
    private void Update(bool added)
    {
        _pendingAdd |= added;
        if (_updateQueued || _disposed)
        {
            return;
        }
        _updateQueued = true;
        _window.Dispatcher.BeginInvoke(() =>
        {
            _updateQueued = false;
            var add = _pendingAdd;
            _pendingAdd = false;
            UpdateNow(add);
        });
    }

    private void UpdateNow(bool added)
    {
        if (_disposed)
        {
            return;
        }
        Publish();
        // the game draws them: the window would show every toast twice (and is no use under Proton or full screen)
        var want = Supported && _shell.Toasts.Count > 0 && (Forced || (BehindGame && !_bridge.GameDraws));
        if (want && (!_shown || added))
        {
            Place(); // a new toast: the game may be on another monitor now
            if (!_shown)
            {
                _shown = true;
                _window.Show(); // ShowActivated false: SW_SHOWNA
                Place();        // a first show on another monitor's DPI may have resized it
            }
        }
        else if (!want && _shown)
        {
            _shown = false;
            _window.Hide();
        }
    }

    // ── what the game draws ──

    /// <summary>What the ToastHost shows now, for the game; none while the launcher's own window shows them.</summary>
    internal GameToastSnapshot Snapshot()
    {
        var palette = Read(PaletteKeys);
        if (!(BehindGame || ForcedForGame))
        {
            return new GameToastSnapshot([], palette);
        }
        var toasts = new List<GameToast>();
        foreach (var t in _shell.Toasts.Where(t => !t.Leaving))
        {
            toasts.Add(new GameToast(t.Id, t.Kind, t.Title, t.Body, t.LeavesAtMs, (long)t.Stay.TotalMilliseconds));
        }
        return new GameToastSnapshot(toasts, palette);
    }

    private void Publish()
    {
        if (!_disposed)
        {
            _bridge.Publish(Snapshot());
        }
    }

    /// <summary>The theme's colours by role, contrast included, "#AARRGGBB".</summary>
    private static Dictionary<string, string> Read(IEnumerable<(string Role, string Key)> keys)
    {
        var palette = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (role, key) in keys)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush { Color: var c })
            {
                palette[role] = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            }
        }
        return palette;
    }

    /// <summary>The game asked for an action (the local API's thread): carried out on the UI thread, then published.</summary>
    private void OnGameRequest(object? sender, GameToastRequest request) =>
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }
            if (request.Action == GameToastAction.Dismiss)
            {
                _shell.DismissToast(request.Id);
            }
            Publish();
        });

    private void Place()
    {
        var monitor = GameWindow.MonitorPixels(_gameWindow());
        var scale = monitor.Height / 1080.0;
        var width = (int)Math.Round(StageWidth * scale);
        var height = (int)Math.Round(StageHeight * scale);
        GameWindow.PlaceOnTop(_window, new Int32Rect(monitor.X + monitor.Width - width, monitor.Y, width, height));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        var style = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        style |= WsExNoActivate | WsExToolWindow | WsExTransparent | WsExLayered;
        _ = SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(style));
        HwndSource.FromHwnd(hwnd)?.AddHook(OnMessage);
    }

    /// <summary>A click never activates it (it is click-through besides).</summary>
    private static IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021 /* WM_MOUSEACTIVATE */)
        {
            handled = true;
            return new IntPtr(3 /* MA_NOACTIVATE */);
        }
        return IntPtr.Zero;
    }

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080, WsExTransparent = 0x00000020, WsExLayered = 0x00080000;

#pragma warning disable SYSLIB1054 // plain DllImport, as GameWindow
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
#pragma warning restore SYSLIB1054
}

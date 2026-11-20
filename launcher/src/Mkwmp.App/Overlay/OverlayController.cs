// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Mkwmp.App.ViewModels;
using Mkwmp.Core;
using Mkwmp.Core.Overlay;
using Mkwmp.Core.Platform;

namespace Mkwmp.App.Overlay;

/// <summary>
/// The console-style overlay, wired (launcher/docs/OVERLAY.md §4 and §8): between the launcher's
/// <see cref="OverlayBridge"/> (the game asks, the launcher answers), the shell's overlay mode and
/// the window's animation.
/// <list type="bullet">
/// <item>A request: the game's picture (or a drawn card) over the whole window, the window shown
/// over the game, the picture shrunk into home's Play tile.</item>
/// <item>Back to the game (the tile, B on home, the Guide button): the picture grown back, the
/// window hidden, the game focused, the mod told.</item>
/// <item>One visible window at a time: while a game the launcher started is in front, the
/// launcher's window is hidden (on Windows too); it comes back when the game ends.</item>
/// <item>A game without the mod's overlay: the launcher's own Guide watch opens it.</item>
/// </list>
/// UI thread only; the bridge's events are brought to it.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private readonly MainWindow _window;
    private readonly ShellViewModel _shell;
    private readonly OverlayBridge _bridge;
    private readonly Launcher? _launcher;
    private readonly OverlayStartup? _startup;
    private readonly GuideWatcher _guide = new();
    private readonly DispatcherTimer _watch;
    private readonly Dispatcher _ui;
    private int _gamePid;
    private int _foundPid;
    private bool _looking;
    private int _hidFor;
    private bool _hiddenByUs;
    private bool _returning;
    private bool _opening;
    private bool _foreign;
    private bool _wasRunning;
    private bool _disposed;

    /// <param name="launcher">Null in the demo: no game, only the bridge.</param>
    /// <param name="startup">The single instance and host mode; null in the demo.</param>
    public OverlayController(MainWindow window, ShellViewModel shell, Launcher? launcher, OverlayStartup? startup)
    {
        _window = window;
        _shell = shell;
        _launcher = launcher;
        _startup = startup;
        _bridge = shell.Launcher.OverlayBridge;
        _ui = window.Dispatcher;

        _bridge.Requested += OnBridgeRequested;
        _bridge.Closed += OnBridgeClosed;
        shell.OverlayReturnSink = Return;
        shell.StateChanged += OnShellChanged;
        shell.Nav.PropertyChanged += OnShellChanged;
        _guide.Pressed += (_, _) => OnGuide();
        window.Deactivated += (_, _) => _ui.BeginInvoke(OnDeactivated);
        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                QuickReturn(); // hidden by anything else than the way back (the tray): the game gets its pad back
            }
        };
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized)
            {
                QuickReturn();
            }
        };
        window.Closing += OnWindowClosing;
        window.Closed += (_, _) => Dispose();

        _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _watch.Tick += (_, _) => Watch();
        _watch.Start();
        UpdateEnabled();
    }

    /// <summary>The player asked for the window (the tray, a second start of the launcher): it stays, also beside the game.</summary>
    public void ShownByPlayer() => _hiddenByUs = false;

    /// <summary>The demo's "game": opens the overlay with a drawn frame, or goes back (F9).</summary>
    public void Simulate(ImageSource? picture = null)
    {
        if (_shell.IsOverlay)
        {
            _shell.LeaveOverlay();
            return;
        }
        var frame = picture as System.Windows.Media.Imaging.BitmapSource ?? OverlayArt.SyntheticGame();
        _bridge.Enabled = true;
        _ = _bridge.RequestFromLauncher(0, OverlayArt.ToFrame(frame), gameHasMenu: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _watch.Stop();
        _guide.Dispose();
        _bridge.Enabled = false;
        _bridge.Requested -= OnBridgeRequested;
        _bridge.Closed -= OnBridgeClosed;
        _shell.StateChanged -= OnShellChanged;
        _shell.Nav.PropertyChanged -= OnShellChanged;
        _shell.OverlayReturnSink = null;
    }

    // ── when the overlay may open ──

    private void OnShellChanged(object? sender, EventArgs e) => UpdateEnabled();

    /// <summary>
    /// The mod is told "no overlay" (and leaves every button to the game and mkw-recomp) until the
    /// first-run setup is done, during the setup page, while an update is being applied, and for a
    /// game no window of ours can be put over.
    /// </summary>
    private void UpdateEnabled()
    {
        if (_disposed)
        {
            return;
        }
        var settings = _shell.Settings;
        _bridge.Enabled = !_foreign && settings.Ui.SetupDone && settings.AdultConsent && _shell.Page is not SetupViewModel
            && _shell.Update.State is not (UpdateState.Downloading or UpdateState.Restarting);
    }

    // ── the game the launcher is beside ──

    private int HostedPid => _launcher?.OverlayHost.ParentPid ?? 0;

    /// <summary>Once a second: which game runs, whether its window is up (then ours hides), whether it ended.</summary>
    private void Watch()
    {
        if (_disposed)
        {
            return;
        }
        UpdateEnabled();
        var session = _shell.Session;
        var ours = session.GameRunning && !session.FromSteam; // started by PLAY
        if (ours && _launcher is { } launcher && !_looking && _foundPid == 0)
        {
            // the process list, off the UI thread
            _looking = true;
            _ = Task.Run(launcher.FindGamePid).ContinueWith(found => _ui.BeginInvoke(() =>
            {
                _looking = false;
                _foundPid = found.Status == TaskStatus.RanToCompletion ? found.Result : 0;
            }), TaskScheduler.Default);
        }
        var pid = _bridge.GamePid != 0 ? _bridge.GamePid : HostedPid != 0 ? HostedPid : ours ? _foundPid : 0;
        if (pid != 0 && !Alive(pid))
        {
            pid = 0;
        }
        if (pid == 0 && (_gamePid != 0 || (_wasRunning && !session.GameRunning)))
        {
            GameGone();
        }
        _wasRunning = session.GameRunning;
        _gamePid = pid;
        if (pid == 0)
        {
            if (!_shell.IsOverlay)
            {
                _guide.Stop();
            }
            return;
        }
        // one visible window at a time: the game's is up, ours goes (OVERLAY.md §4)
        if (ours && pid != _hidFor && !_shell.IsOverlay && !_bridge.IsOpen && _window.IsVisible && GameWindow.Find(pid) != IntPtr.Zero)
        {
            _hidFor = pid;
            _hiddenByUs = true;
            _window.Hide();
        }
        // Guide as the way back while the overlay is up, and as the trigger for a game whose mod does not ask
        if (_guide.Available && (_shell.IsOverlay ? !_window.IsOverlayAnimating : !_bridge.GameListening))
        {
            _guide.Start();
        }
        else if (!_shell.IsOverlay)
        {
            _guide.Stop();
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The game ended: an open overlay is just the launcher again, and a window hidden for the game comes back.</summary>
    private void GameGone()
    {
        _bridge.GameExited(); // Closed, when it was open: OnClosed ends overlay mode
        (_gamePid, _foundPid, _hidFor, _foreign) = (0, 0, 0, false);
        var show = _hiddenByUs && _startup?.Hosted != true && !_window.IsVisible;
        _hiddenByUs = false;
        if (show)
        {
            _window.RestoreAfterOverlay();
            _window.Show();
            _ = _window.Activate();
        }
        UpdateEnabled();
    }

    // ── the request ──

    private void OnBridgeRequested(object? sender, OverlayRequest request) => _ui.BeginInvoke(() => Open(request));

    private void Open(OverlayRequest request)
    {
        if (_disposed || _returning)
        {
            return;
        }
        var pid = request.GamePid != 0 ? request.GamePid : _gamePid;
        var game = GameWindow.Find(pid);
        if (WineHost.Detected && request.By != OverlayTrigger.Launcher && game == IntPtr.Zero && pid != HostedPid && _launcher?.IsGameProcess(pid) != true)
        {
            // A game of another Proton prefix (its own Steam shortcut) reaches the local API, but gamescope shows
            // one app's windows: ours would never be seen while the mod holds the pad. No overlay for that game.
            App.Log($"overlay: game {pid} is not of this prefix; no overlay for it");
            _foreign = true;
            UpdateEnabled();
            _ = _bridge.ReturnToGame();
            return;
        }
        if (pid != 0)
        {
            _gamePid = pid;
        }
        var inFront = _window.IsVisible && _window.IsActive;
        if (request.Again && _shell.IsOverlay && inFront)
        {
            if (request.Frame is { } fresh)
            {
                _shell.EnterOverlay(GameWindow.ToBitmap(fresh), again: true);
            }
            return;
        }
        // before our window shows: the mod's picture, else a copy of the screen (Windows desktop only), else a drawn card
        var frame = request.Frame ?? (inFront ? null : GameWindow.Capture(game));
        ImageSource picture = frame is null ? Placeholder() : GameWindow.ToBitmap(frame);
        _guide.Stop(); // the press that opened it is not a press to go back
        _shell.EnterOverlay(picture, request.Again);
        var bounds = GameWindow.ClientPixels(game);
        var cover = bounds is not null || request.By != OverlayTrigger.Launcher; // the demo's request has no game to cover
        if (cover)
        {
            _window.CoverForOverlay(bounds);
        }
        _window.PrepareOverlayOpen(picture);
        _opening = true; // hiding and showing here is not the player putting the launcher away
        try
        {
            if (WineHost.Detected && _window.IsVisible)
            {
                _window.Hide(); // gamescope shows the window mapped last: ours must be mapped now
            }
            GameWindow.ShowOver(_window);
            if (cover && bounds is not null)
            {
                _window.CoverForOverlay(bounds); // a first show places the window itself
            }
        }
        finally
        {
            _opening = false;
        }
        _hiddenByUs = false;
        _window.BeginOverlayOpen(() =>
        {
            if (_guide.Available)
            {
                _guide.Start();
            }
        });
    }

    private System.Windows.Media.Imaging.BitmapSource Placeholder()
    {
        var install = _shell.Launcher.Game.Install;
        var title = string.IsNullOrEmpty(install?.GameVersion) ? "Mario Kart World" : $"Mario Kart World {install.GameVersion}";
        return OverlayArt.Placeholder(title, _shell.S["home.overlay.placeholder"]);
    }

    private void OnBridgeClosed(object? sender, OverlayReturn how) => _ui.BeginInvoke(() =>
    {
        // not by the way back: the game ended. The launcher stays as the launcher.
        if (!_disposed && !_returning && _shell.IsOverlay)
        {
            _window.ResetOverlayFx();
            _shell.EndOverlay();
            _window.RestoreAfterOverlay();
        }
    });

    // ── back to the game ──

    private void OnGuide()
    {
        if (_disposed || _window.IsOverlayAnimating)
        {
            return;
        }
        if (_shell.IsOverlay)
        {
            if (_window.IsActive)
            {
                _shell.LeaveOverlay();
            }
            return;
        }
        // a game without the mod's overlay (the winmm probe, an older mod): the launcher opens it itself
        if (!_bridge.IsOpen && !_bridge.GameListening && _gamePid != 0 && GameWindow.IsForeground(_gamePid))
        {
            _ = _bridge.RequestFromLauncher(_gamePid, GameWindow.Capture(GameWindow.Find(_gamePid)));
        }
    }

    /// <summary>The grow animation, then the game has the screen and the pad again.</summary>
    private void Return(bool toMenu)
    {
        if (_disposed || _returning || !_shell.IsOverlay)
        {
            return;
        }
        _returning = true;
        _guide.Stop();
        _window.BeginOverlayReturn(_shell.OverlayThumb ?? Placeholder(), () => GiveBack(toMenu ? OverlayReturn.Menu : OverlayReturn.Resume, focusGame: true));
    }

    /// <summary>Overlay mode ends without the animation: the window was hidden or the game took the front by itself.</summary>
    private void QuickReturn()
    {
        if (_disposed || _returning || _opening || !_shell.IsOverlay)
        {
            return;
        }
        _returning = true;
        GiveBack(OverlayReturn.Resume, focusGame: false);
    }

    private void GiveBack(OverlayReturn how, bool focusGame)
    {
        var pid = _bridge.GamePid != 0 ? _bridge.GamePid : _gamePid;
        var game = GameWindow.Find(pid);
        var demo = _launcher is null; // no game behind the demo: its window stays
        if (focusGame)
        {
            GameWindow.GiveBack(_window, game, hideLauncher: !demo);
        }
        else if (!demo && _window.IsVisible)
        {
            _window.Hide();
        }
        (_hidFor, _hiddenByUs) = (pid, !demo);
        _ = _bridge.ReturnToGame(how);
        _shell.EndOverlay();
        _window.ResetOverlayFx();
        _window.RestoreAfterOverlay();
        _returning = false;
    }

    private void OnDeactivated()
    {
        // the game took the front by itself (Alt+Tab, a click on it): with the overlay "open" the mod would keep its pad idle
        if (!_disposed && _shell.IsOverlay && !_window.IsOverlayAnimating && _bridge.IsOpen && _gamePid != 0 && GameWindow.IsForeground(_gamePid))
        {
            QuickReturn();
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel || !_bridge.IsOpen)
        {
            return;
        }
        // quitting from the overlay: the game is not left waiting for a launcher that is gone
        var game = GameWindow.Find(_bridge.GamePid != 0 ? _bridge.GamePid : _gamePid);
        _returning = true;
        _ = _bridge.ReturnToGame();
        GameWindow.GiveBack(_window, game, hideLauncher: false);
    }
}

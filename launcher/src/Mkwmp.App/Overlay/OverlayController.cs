// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Mkwmp.App.Desktop;
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
/// <item>A game without the mod's overlay: the launcher's own pad watch opens it on the player's shortcut.</item>
/// <item>"Quit Game": the game is asked to end itself (the mod, then its window's close), and
/// only when it does not, the player may end its process.</item>
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
    private bool _quitting;
    private bool _disposed;
    private IntPtr _asideGame; // the game's window, minimised while the overlay is up (OverlayFocus)
    private int _openSeq;      // the opening a delayed step of ToTheFront belongs to
    private bool _keptBehind;  // the window manager kept the game in front of this opening
    private bool _hinted;      // the hint about it was shown (once per run)
    private bool _settling;    // under Wine, until the check of this opening: a deactivation is the window manager's, not the player's

    /// <summary>How long the game has to end itself after it was asked, and again after its window was asked.</summary>
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);

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
        shell.QuitGameSink = QuitGameAsync;
        shell.GameRunsProbe = () => KnownGamePid != 0;
        shell.StateChanged += OnShellChanged;
        shell.Nav.PropertyChanged += OnShellChanged;
        _guide.Pressed += (_, _) => OnGuide();
        _guide.ShortcutPressed += (_, _) => OnShortcut();
        window.Deactivated += (_, _) => _ui.BeginInvoke(OnDeactivated);
        window.Activated += (_, _) => _ui.BeginInvoke(OnActivated);
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
        _shell.QuitGameSink = null;
        _shell.GameRunsProbe = null;
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
        _bridge.Shortcut = _guide.Shortcut = settings.Ui.OverlayShortcut; // the mod's next poll takes it
        // A launcher the mod started for its game (hosted) opens the overlay even before its welcome
        // is finished: a player who only installed the mod still gets the HOME menu (with a note to
        // finish setting up). A launcher the player opened waits for the welcome as before.
        var ready = settings.Ui.SetupDone || HostedPid != 0;
        _bridge.Enabled = !_foreign && ready && (HostedPid != 0 || _shell.Page is not SetupViewModel)
            && _shell.Update.State is not UpdateState.Restarting; // a download goes on behind a game; only the hand-over to the helper is the end
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
        if (_quitting)
        {
            return; // "Quit Game" is watching the game itself, and says when it is gone
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

    private static bool Alive(int pid) => GameWindow.Alive(pid);

    /// <summary>The game "Quit Game" would end: the one that asked for the overlay, else the one the watch found; 0 for none.</summary>
    private int KnownGamePid => _bridge.GamePid != 0 ? _bridge.GamePid : _gamePid;

    /// <summary>The game ended: an open overlay is just the launcher again, and a window hidden for the game comes back.</summary>
    private void GameGone()
    {
        _bridge.GameExited(); // Closed, when it was open: OnClosed ends overlay mode
        (_gamePid, _foundPid, _hidFor, _foreign) = (0, 0, 0, false);
        (_asideGame, _keptBehind, _settling) = (IntPtr.Zero, false, false);
        _openSeq++;
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
        if (!_shell.Settings.Ui.SetupDone && !request.Again)
        {
            var s = _shell.S;
            _shell.ShowToast(s["overlay.setup_pending"], s["overlay.setup_pending_body"]);
        }
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
        ToTheFront(game, pid);
        _window.BeginOverlayOpen(() =>
        {
            if (_guide.Available)
            {
                _guide.Start();
            }
        });
    }

    /// <summary>
    /// Under Wine on a Linux desktop the window manager may keep the game in front of a window we showed (KWin's
    /// focus-stealing prevention; <see cref="OverlayFocus"/>): once ours is mapped, the game's window is minimised and
    /// the focus goes to ours. Later the launcher looks whether it is in front, for the log and, if not, a hint the
    /// player sees when they reach the window by hand (Alt+Tab). Nothing on Windows.
    /// </summary>
    private void ToTheFront(IntPtr game, int pid)
    {
        if (!WineHost.Detected)
        {
            return;
        }
        var open = ++_openSeq;
        _settling = true;
        var env = Environment.GetEnvironmentVariable(OverlayFocus.EnvName);
        if (game != IntPtr.Zero && OverlayFocus.StepGameAside(WineHost.Detected, DesktopEnvironment.IsGameMode, env))
        {
            After(OverlayFocus.StepAsideDelay, () =>
            {
                if (open != _openSeq || !_shell.IsOverlay || _returning)
                {
                    return;
                }
                if (GameWindow.StepAside(game))
                {
                    _asideGame = game;
                    App.Log("overlay: the game's window is minimised while the overlay is up (a Linux desktop under Wine)");
                }
                _ = _window.Activate();
            });
        }
        After(OverlayFocus.CheckAfter, () =>
        {
            if (open != _openSeq || !_shell.IsOverlay || _returning)
            {
                return;
            }
            _settling = false;
            if (OverlayFocus.KeptBehind(_window.IsActive, GameWindow.IsForeground(pid)))
            {
                _keptBehind = true;
                App.Log($"overlay: not in front after {OverlayFocus.CheckAfter.TotalSeconds:0.#} s (active {_window.IsActive}, game in front {GameWindow.IsForeground(pid)}, minimised {(_asideGame != IntPtr.Zero ? "yes" : "no")}): the window manager kept the game in front");
                _ = _window.Activate();
            }
        });
    }

    /// <summary>The window became active with the overlay up after it had been kept behind the game: the hint, once per run.</summary>
    private void OnActivated()
    {
        if (_disposed || !_keptBehind || !_shell.IsOverlay)
        {
            return;
        }
        _keptBehind = false;
        if (!_hinted)
        {
            _hinted = true;
            var s = _shell.S;
            _shell.ShowToast(s["overlay.kept_behind"], s["overlay.kept_behind_body"]);
        }
    }

    private void After(TimeSpan delay, Action then)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_disposed)
            {
                then();
            }
        };
        timer.Start();
    }

    /// <summary>The game's window minimised for the overlay (<see cref="ToTheFront"/>) is restored, before ours goes.</summary>
    private void BringGameBack()
    {
        _openSeq++; // a pending step aside or check of this opening does nothing now
        _settling = false;
        if (_asideGame != IntPtr.Zero)
        {
            GameWindow.BringBack(_asideGame);
            _asideGame = IntPtr.Zero;
        }
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

    /// <summary>Guide with the overlay up goes back to the game (B on home does too, whatever the shortcut).</summary>
    private void OnGuide()
    {
        if (!_disposed && !_window.IsOverlayAnimating && _shell.IsOverlay && _window.IsActive)
        {
            _shell.LeaveOverlay();
        }
    }

    /// <summary>
    /// The player's shortcut on a game without the mod's overlay (the winmm probe, an older mod): the launcher opens
    /// it itself. It only sees the buttons; the game gets them too.
    /// </summary>
    private void OnShortcut()
    {
        if (_disposed || _window.IsOverlayAnimating || _shell.IsOverlay)
        {
            return;
        }
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
        var game = _asideGame != IntPtr.Zero ? _asideGame : GameWindow.Find(pid);
        var demo = _launcher is null; // no game behind the demo: its window stays
        BringGameBack(); // minimised for the overlay (a Linux desktop): restored while ours still has the focus
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
        if (!_disposed && !_settling && _shell.IsOverlay && !_window.IsOverlayAnimating && _bridge.IsOpen && _gamePid != 0 && GameWindow.IsForeground(_gamePid))
        {
            QuickReturn();
        }
    }

    // ── "Quit Game" ──

    /// <summary>
    /// Ends the game for real (the player confirmed). Gracefully first: the mod is told (the
    /// runtime's own clean end: saves finished, the seat given back), then the game's window is
    /// asked to close, which ends mkw-recomp the same way; only when neither works within a few
    /// seconds is the player offered to end the process. The launcher stays where it is meanwhile;
    /// when the game is gone it is the ordinary launcher again (a host the mod started leaves with
    /// its game, OverlayStartup), or quits too (<paramref name="thenQuit"/>).
    /// </summary>
    private async Task QuitGameAsync(bool thenQuit)
    {
        if (_disposed || _quitting)
        {
            return;
        }
        var pid = KnownGamePid;
        if (pid == 0 || !Alive(pid))
        {
            // nothing behind the picture (the demo), or the game left meanwhile
            _ = _bridge.ReturnToGame(OverlayReturn.Quit);
            Finish(thenQuit);
            return;
        }
        _quitting = true;
        _returning = true; // the picture stays: no way back, no new request, until the game is gone
        _guide.Stop();
        var s = _shell.S;
        var gone = false;
        try
        {
            var progress = _shell.ShowProgress(s["quitgame.closing"], s["quitgame.closing_body"]);
            try
            {
                // the mod ends its wait with "quit"; a runtime without that entry gives the pad back and is closed by its window
                var asked = _bridge.IsOpen && _bridge.GameHasQuit;
                _ = _bridge.ReturnToGame(OverlayReturn.Quit);
                App.Log($"overlay: Quit Game: game {pid} {(asked ? "asked through the mod" : "asked by its window (no quit entry, or not over it)")}");
                if (!asked)
                {
                    _ = GameWindow.RequestClose(pid);
                }
                gone = await GoneAsync(pid, QuitWait).ConfigureAwait(true);
                if (!gone && asked)
                {
                    App.Log($"overlay: Quit Game: game {pid} still runs after {QuitWait.TotalSeconds:0} s: closing its window");
                    _ = GameWindow.RequestClose(pid);
                    gone = await GoneAsync(pid, CloseWait).ConfigureAwait(true);
                }
            }
            finally
            {
                progress.Close();
            }
            while (!gone && !_disposed)
            {
                var force = await _shell.ConfirmAsync(s["quitgame.stuck.title"], s["quitgame.stuck.body"], s["quitgame.force"], s["quitgame.wait"]).ConfigureAwait(true);
                if (!Alive(pid))
                {
                    gone = true;
                }
                else if (force)
                {
                    App.Log($"overlay: Quit Game: ending the process tree of game {pid}");
                    var killed = await Task.Run(() => GameWindow.Kill(pid)).ConfigureAwait(true);
                    gone = await GoneAsync(pid, CloseWait).ConfigureAwait(true);
                    if (!gone)
                    {
                        App.Log($"overlay: Quit Game: game {pid} could not be ended (kill {(killed ? "sent" : "refused")})");
                        _shell.ShowToast(s["quitgame.failed"]);
                        break;
                    }
                }
                else
                {
                    var waiting = _shell.ShowProgress(s["quitgame.closing"], s["quitgame.closing_body"]);
                    try
                    {
                        gone = await GoneAsync(pid, QuitWait).ConfigureAwait(true);
                    }
                    finally
                    {
                        waiting.Close();
                    }
                }
            }
        }
        finally
        {
            _quitting = false;
            _returning = false;
        }
        if (_disposed)
        {
            return;
        }
        if (gone)
        {
            Finish(thenQuit);
        }
        else
        {
            // it would not end: the game is the player's again (the mod already gave the pad back)
            QuickReturn();
        }
    }

    /// <summary>The game is gone: out of overlay mode, the window as the ordinary launcher's; then the launcher too, when asked.</summary>
    private void Finish(bool thenQuit)
    {
        var wasOverlay = _shell.IsOverlay;
        (_asideGame, _keptBehind, _settling) = (IntPtr.Zero, false, false);
        _openSeq++;
        _bridge.GameExited();
        (_gamePid, _foundPid, _hidFor, _foreign, _hiddenByUs) = (0, 0, 0, false, false);
        if (wasOverlay)
        {
            _window.ResetOverlayFx();
            _shell.EndOverlay();
            _window.RestoreAfterOverlay();
        }
        UpdateEnabled();
        if (thenQuit)
        {
            _shell.Quit();
        }
    }

    private async Task<bool> GoneAsync(int pid, TimeSpan within)
    {
        var until = Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * Stopwatch.Frequency);
        while (Alive(pid))
        {
            if (_disposed || Stopwatch.GetTimestamp() >= until)
            {
                return false;
            }
            await Task.Delay(200).ConfigureAwait(true);
        }
        return true;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel || !_bridge.IsOpen)
        {
            return;
        }
        // quitting from the overlay: the game is not left waiting for a launcher that is gone
        var game = _asideGame != IntPtr.Zero ? _asideGame : GameWindow.Find(_bridge.GamePid != 0 ? _bridge.GamePid : _gamePid);
        _returning = true;
        BringGameBack();
        _ = _bridge.ReturnToGame();
        GameWindow.GiveBack(_window, game, hideLauncher: false);
    }
}

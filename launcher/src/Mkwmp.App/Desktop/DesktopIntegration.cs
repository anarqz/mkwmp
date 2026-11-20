// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Mkwmp.App.ViewModels;
using Mkwmp.Core;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.Desktop;

/// <summary>
/// The window on the desktop (port of rust/client's tray.rs, notify.rs and the close handling of
/// gui.rs), outside the shell so the shell stays a plain view model:
/// <list type="bullet">
/// <item>Windows: a tray icon ("MKWMP"; left click opens; menu Open MKWMP / Quit). With "close to
/// tray" on, closing the window hides it there (the launcher keeps the friends list current); the
/// first time a notification says so. <c>--tray</c> starts hidden. News that arrives while the
/// window is hidden is a Windows notification from the icon; while it is visible, the in-app toast.</item>
/// <item>Wine / Proton on a Linux desktop: no tray icon (Wine's may never show, and a hidden window
/// would be lost). Closing quits; "Minimize" in the power menu and <c>--tray</c> minimize the
/// window; every notification is the in-app toast.</item>
/// <item>The Steam Deck's Game Mode: one full-screen window; closing quits, nothing hides.</item>
/// </list>
/// Quitting always goes through the application's exit, which disposes the launcher (the seat is given back).
/// </summary>
internal sealed class DesktopIntegration : IDisposable
{
    private readonly Window _window;
    private readonly ShellViewModel _shell;
    private readonly TrayIcon? _tray;
    private bool _sessionEnding;

    private DesktopIntegration(Window window, ShellViewModel shell, bool startInTray)
    {
        _window = window;
        _shell = shell;
        if (DesktopEnvironment.HasTray)
        {
            _tray = TrayIcon.TryCreate("MKWMP", shell.S["tray.open"], shell.S["tray.quit"]);
        }
        if (_tray is not null)
        {
            _tray.OpenRequested += (_, _) => Open();
            _tray.QuitRequested += (_, _) => _shell.Quit();
            _shell.LanguageChanged += (_, _) => _tray.SetTexts(_shell.S["tray.open"], _shell.S["tray.quit"]);
        }
        shell.TrayAvailable = _tray is not null;
        shell.HideRequested += (_, _) => Hide();
        shell.QuitRequested += (_, _) =>
            // after the window's own handler closed it: also ends a launcher whose window was never shown
            Application.Current?.Dispatcher.BeginInvoke(() => Application.Current?.Shutdown());
        shell.ToastSink = OnToast;
        window.Closing += OnClosing;
        window.Closed += (_, _) => Dispose();
        if (Application.Current is { } app)
        {
            app.SessionEnding += (_, _) => _sessionEnding = true; // logging off: never "close to tray"
        }

        if (startInTray)
        {
            if (_tray is not null)
            {
                StartHidden = true;
            }
            else if (DesktopEnvironment.CanMinimize)
            {
                window.Loaded += (_, _) => window.WindowState = WindowState.Minimized;
            }
        }
    }

    /// <summary><c>--tray</c> and there is a tray: the window is not shown at start.</summary>
    public bool StartHidden { get; }

    /// <summary>Wires the window to the desktop. In the screenshot harness: nothing (null).</summary>
    public static DesktopIntegration? Attach(Window window, ShellViewModel shell, bool startInTray, bool harness)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(shell);
        return harness ? null : new DesktopIntegration(window, shell, startInTray);
    }

    public void Dispose() => _tray?.Dispose();

    private bool IsHidden => !_window.IsVisible || _window.WindowState == WindowState.Minimized;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shell.IsQuitting || _sessionEnding || _tray is null || !_shell.Settings.Background.CloseToTray)
        {
            return; // really closes: the application exits and disposes the launcher
        }
        e.Cancel = true;
        Hide();
    }

    /// <summary>Into the tray; without one (Wine), minimized.</summary>
    private void Hide()
    {
        if (_tray is null)
        {
            if (DesktopEnvironment.CanMinimize)
            {
                _window.WindowState = WindowState.Minimized;
            }
            return;
        }
        _window.Hide();
        // once ever: the player may think the launcher quit, and friends would still see them online
        if (_shell.Launcher is Launcher launcher)
        {
            launcher.ShowTrayHint(); // arrives as a toast: OnToast makes it a notification, the window is hidden
        }
        else if (!_shell.Settings.Background.TrayHintShown)
        {
            _tray.ShowBalloon(CoreStrings.TrayHintTitle, CoreStrings.TrayHintBody);
            _ = SaveHintShownAsync();
        }
    }

    private async Task SaveHintShownAsync()
    {
        try
        {
            await _shell.UpdateSettingsAsync(s => s with { Background = s.Background with { TrayHintShown = true } }).ConfigureAwait(true);
        }
        catch (LauncherException e)
        {
            App.Log("tray hint: " + e.Message);
        }
    }

    private void Open()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = _shell.IsFullscreen ? WindowState.Maximized : WindowState.Normal;
        }
        _window.Activate();
        TrayIcon.BringToFront(new WindowInteropHelper(_window).Handle);
    }

    /// <summary>
    /// A toast of the launcher (a friend is online / playing / asks, the tray hint). True when the
    /// system showed it and the in-app toast is not wanted.
    /// </summary>
    private bool OnToast(Toast toast)
    {
        if (_tray is null)
        {
            return false; // Wine, Game Mode: always the in-app toast
        }
        if (IsHidden)
        {
            return _tray.ShowBalloon(toast.Title, toast.Body); // not shown by the system: the in-app one waits in the window
        }
        if (!_window.IsActive)
        {
            _tray.ShowBalloon(toast.Title, toast.Body); // the game is in front: both
        }
        return false;
    }
}

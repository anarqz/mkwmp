// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.App.Controls;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The update card. Follows <c>Launcher.Update</c> (launcher/docs/UPDATES.md):
/// what is new → the download with its progress, speed and time left → "Install and Restart"
/// (or "Later": Home's What's New tile and Settings › Updates then say "ready to install").
/// While a game runs nothing is installed: the card offers "Install After the Game" instead.
/// <para>
/// A real launcher does not offer: the update is <b>required</b> (<see cref="UpdateView.Required"/>). The shell
/// opens this card by itself and it cannot be closed (<see cref="Blocks"/>): the download runs, then "Install and
/// Restart", pressed by the card itself after <see cref="AutoInstallSeconds"/>; "Quit MKWMP" is always there. If it
/// fails: Try Again, or Continue Without Updating (this run only). With a game running it does not block: it says
/// the update follows the game.
/// </para>
/// </summary>
public sealed class UpdateModal : ModalViewModel, IDisposable
{
    private readonly ShellViewModel _shell;
    /// <summary>How long "Install and Restart" waits before the card presses it itself.</summary>
    public const int AutoInstallSeconds = 10;

    private (UpdateState State, bool Game, bool After, bool Required)? _shown;
    private CancellationTokenSource? _cancel;
    private System.Windows.Threading.DispatcherTimer? _countdown;
    private int _secondsLeft;

    /// <summary>
    /// The launcher must update before anything else: the card is shown and stays. Not while a game runs (the
    /// update follows it) nor over a game as its overlay.
    /// </summary>
    public static bool Blocks(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var launcher = shell.Launcher;
        var update = launcher.Update;
        return update.Required && !update.GameBlocks && !update.AfterGame && !launcher.Session.Busy && !launcher.Session.GameRunning && !shell.IsOverlay;
    }

    public UpdateModal(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        OnStateChanged();
    }

    public override void OnClosed() => Dispose();

    public void Dispose()
    {
        StopCountdown();
        _cancel?.Dispose();
        _cancel = null;
    }

    private void StopCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
    }

    /// <summary>"Installing in 10 seconds", counted down; at zero the card installs. (A screenshot only shows it.)</summary>
    private void StartCountdown()
    {
        StopCountdown();
        _secondsLeft = AutoInstallSeconds;
        ShowCountdown();
        if (!_shell.AutoGameUpdate)
        {
            return;
        }
        _countdown = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            if (--_secondsLeft > 0)
            {
                ShowCountdown();
                return;
            }
            StopCountdown();
            Install();
        };
        _countdown.Start();
    }

    private void ShowCountdown() =>
        Body = _shell.S["update.ready_body"] + "\n\n" + _shell.S.Format("update.installing_in", ("n", _secondsLeft.ToString(CultureInfo.CurrentCulture)));

    public override void OnStateChanged()
    {
        var s = _shell.S;
        var launcher = _shell.Launcher;
        var update = launcher.Update;
        Progress = update.Progress;
        if (update.State == UpdateState.Downloading)
        {
            Detail = DownloadLine(s, update);
        }
        // A game the launcher sees right now counts before the core is asked (and refuses).
        var game = update.GameBlocks || launcher.Session.Busy || launcher.Session.GameRunning;
        var now = (update.State, update.State == UpdateState.ReadyToInstall && game, update.AfterGame, update.Required);
        if (_shown == now)
        {
            return;
        }
        _shown = now;
        StopCountdown();
        Actions.Clear();
        ShowProgress = false;
        ShowSpinner = false;
        CanDismiss = true;
        if (update.State != UpdateState.Downloading)
        {
            Detail = "";
        }
        var close = new ModalAction(s["common.close"], () => _shell.CloseModal(this));
        var later = new ModalAction(s["update.later"], () => _shell.CloseModal(this));
        if (update.Required && Required(s, launcher, update, game, close))
        {
            return;
        }
        switch (update.State)
        {
            case UpdateState.Available:
                Title = s.Format("update.available", ("version", update.AvailableVersion ?? ""));
                Body = update.Notes ?? "";
                Actions.Add(new ModalAction(s["update.download"], Download, ButtonKind.Outline, isDefault: true));
                Bottom = later;
                break;
            case UpdateState.Downloading:
                Title = s["update.downloading"];
                Body = s["update.downloading_body"];
                ShowProgress = true;
                ShowSpinner = true;
                Actions.Add(new ModalAction(s["common.cancel"], () => _cancel?.Cancel()));
                Bottom = close;
                break;
            case UpdateState.ReadyToInstall when update.AfterGame:
                Title = s.Format("update.ready", ("version", update.AvailableVersion ?? ""));
                Body = s["update.after_game_on"];
                Actions.Add(new ModalAction(s["update.after_game_off"], () => launcher.InstallUpdateAfterGame(false)));
                Bottom = close;
                break;
            case UpdateState.ReadyToInstall when game:
                Title = s.Format("update.ready", ("version", update.AvailableVersion ?? ""));
                Body = s["update.game_running"];
                Actions.Add(new ModalAction(s["update.install_after_game"], () => launcher.InstallUpdateAfterGame(true), ButtonKind.Outline, isDefault: true));
                Bottom = later;
                break;
            case UpdateState.ReadyToInstall:
                Title = s.Format("update.ready", ("version", update.AvailableVersion ?? ""));
                Body = s["update.ready_body"];
                Actions.Add(new ModalAction(s["update.install"], Install, ButtonKind.Outline, isDefault: true));
                Bottom = later;
                break;
            case UpdateState.Restarting:
                Title = s["update.installing"];
                Body = s["update.installing_body"];
                ShowSpinner = true;
                CanDismiss = false;
                Bottom = null;
                break;
            case UpdateState.Checking:
                Title = s["update.checking"];
                Body = "";
                ShowSpinner = true;
                Bottom = close;
                break;
            case UpdateState.Failed:
                Title = s["update.failed"];
                Body = update.Status;
                Actions.Add(new ModalAction(s["update.retry"], Download, ButtonKind.Outline, isDefault: true));
                Bottom = close;
                break;
            default:
                Title = s["update.uptodate"];
                Body = s.Format("update.uptodate_body", ("version", launcher.Version));
                Actions.Add(new ModalAction(s["update.check"], () => RelayCommand.Async(launcher.CheckForUpdatesAsync).Execute(null), ButtonKind.Outline, isDefault: true));
                Bottom = close;
                break;
        }
    }

    /// <summary>The card of a required update; false for a state it shows as any other (installing).</summary>
    private bool Required(Strings s, ILauncher launcher, UpdateView update, bool game, ModalAction close)
    {
        var version = update.AvailableVersion ?? "";
        var quit = new ModalAction(s["power.quit"], _shell.Quit);
        switch (update.State)
        {
            case UpdateState.Available or UpdateState.Downloading:
                Title = s["update.required"];
                Body = s.Format("update.required_body", ("version", version));
                ShowProgress = update.State == UpdateState.Downloading;
                ShowSpinner = true;
                CanDismiss = game;
                Bottom = game ? close : quit;
                return true;
            case UpdateState.ReadyToInstall when game || update.AfterGame:
                // never under a game: it follows the game by itself, and the card does not hold the player
                Title = s.Format("update.ready", ("version", version));
                Body = s["update.after_game_on"];
                Bottom = close;
                return true;
            case UpdateState.ReadyToInstall:
                Title = s.Format("update.ready", ("version", version));
                Body = s["update.ready_body"];
                Actions.Add(new ModalAction(s["update.install"], Install, ButtonKind.Outline, isDefault: true));
                CanDismiss = false;
                Bottom = quit;
                StartCountdown();
                return true;
            case UpdateState.Failed:
                Title = s["update.failed"];
                Body = update.Status + "\n\n" + s["update.failed_required"];
                Actions.Add(new ModalAction(s["update.retry"], () => RelayCommand.Async(launcher.CheckForUpdatesAsync).Execute(null), ButtonKind.Outline, isDefault: true));
                Actions.Add(new ModalAction(s["update.continue_without"], () =>
                {
                    launcher.ContinueWithoutUpdate();
                    _shell.CloseModal(this);
                }, ButtonKind.Secondary));
                CanDismiss = false;
                Bottom = quit;
                return true;
            default:
                return false;
        }
    }

    private void Download()
    {
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        RelayCommand.Async(() => _shell.Launcher.DownloadUpdateAsync(token)).Execute(null);
    }

    private void Install()
    {
        StopCountdown();
        RelayCommand.Async(() => _shell.Launcher.ApplyUpdateAsync(CancellationToken.None)).Execute(null);
    }


    /// <summary>"31.2 MB of 78.0 MB · 2.4 MB/s · 20 seconds remaining"; less of it while less is known.</summary>
    internal static string DownloadLine(Strings s, UpdateView update)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(update);
        var left = update.Remaining is { } remaining ? Remaining(s, remaining) : null;
        if (update.Total <= 0 || update.BytesPerSecond <= 0)
        {
            return left ?? update.Status;
        }
        (string, string)[] values = [("done", Megabytes(s, update.Received)), ("total", Megabytes(s, update.Total)), ("speed", Megabytes(s, (long)update.BytesPerSecond)), ("left", left ?? "")];
        return s.Format(left is null ? "update.progress" : "update.progress_left", values);
    }

    private static string Megabytes(Strings s, long bytes) =>
        s.Format("core.megabytes", ("n", (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture)));

    private static string Remaining(Strings s, TimeSpan left) => left.TotalMinutes >= 1.5
        ? s.Format("update.minutes_left", ("n", Math.Round(left.TotalMinutes).ToString(CultureInfo.CurrentCulture)))
        : s.Format("update.seconds_left", ("n", Math.Max(1, Math.Round(left.TotalSeconds)).ToString(CultureInfo.CurrentCulture)));
}

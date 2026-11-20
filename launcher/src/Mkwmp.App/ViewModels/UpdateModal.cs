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
/// </summary>
public sealed class UpdateModal : ModalViewModel, IDisposable
{
    private readonly ShellViewModel _shell;
    private (UpdateState State, bool Game, bool After)? _shown;
    private CancellationTokenSource? _cancel;

    public UpdateModal(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        OnStateChanged();
    }

    public override void OnClosed() => Dispose();

    public void Dispose()
    {
        _cancel?.Dispose();
        _cancel = null;
    }

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
        var now = (update.State, update.State == UpdateState.ReadyToInstall && game, update.AfterGame);
        if (_shown == now)
        {
            return;
        }
        _shown = now;
        Actions.Clear();
        ShowProgress = false;
        ShowSpinner = false;
        CanDismiss = true;
        var close = new ModalAction(s["common.close"], () => _shell.CloseModal(this));
        var later = new ModalAction(s["update.later"], () => _shell.CloseModal(this));
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

    private void Download()
    {
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        RelayCommand.Async(() => _shell.Launcher.DownloadUpdateAsync(token)).Execute(null);
    }

    private void Install() => RelayCommand.Async(() => _shell.Launcher.ApplyUpdateAsync(CancellationToken.None)).Execute(null);

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

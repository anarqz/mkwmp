// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.App.Controls;
using Mkwmp.Core;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>What the launcher is doing to make the game playable, or what it still needs for it.</summary>
public enum GameWorkPhase
{
    /// <summary>Nothing to do and nothing running.</summary>
    None,

    /// <summary>The pinned runtime is newer than the install the launcher keeps: it must be updated before PLAY.</summary>
    UpdateNeeded,

    /// <summary>The runtime is being downloaded, checked or installed.</summary>
    Runtime,
    RuntimeFailed,

    /// <summary>The setup needs the player's copy of the game, or only to be started again.</summary>
    SetupNeeded,
    Setup,
    SetupFailed,

    /// <summary>MKWMP's files are going into the game.</summary>
    Integrating,
}

/// <summary>
/// "Game setup": the one card for everything between an install and a game PLAY can start: the
/// runtime's download and install, the setup (step, progress, time left, Stop; Try Again and the
/// log when it failed), and what is still needed. It follows the launcher, so it is the same
/// wherever it was opened from: the Game tile, PLAY's "not ready", Settings › Game, the indicator
/// at the top, a toast's page. Closing it only hides it: the work goes on.
/// <para>
/// <see cref="Driving"/>: the card runs the whole way by itself (runtime update, then the setup
/// again from the copy it used, then MKWMP's files) and closes when the game is ready: the
/// "Updating the game" flow after a launcher update brought a newer runtime. It cannot be closed
/// while it works; only the download can wait ("Later"), and PLAY stays locked until the end.
/// </para>
/// </summary>
public sealed class GameWorkModal : ModalViewModel
{
    private readonly ShellViewModel _shell;
    private string _shown = "";
    private bool _startedRuntime;
    private bool _startedSetup;
    private bool _startedIntegration;

    public GameWorkModal(ShellViewModel shell, bool driving = false)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Driving = driving;
        OnStateChanged();
    }

    public bool Driving { get; private set; }

    private Strings S => _shell.S;

    private ILauncher Launcher => _shell.Launcher;

    public override void OnStateChanged()
    {
        var runtime = Launcher.Runtime;
        var setup = Launcher.GameSetup;
        var phase = _shell.GameWorkPhase;
        if (Driving)
        {
            Drive(phase);
            phase = _shell.GameWorkPhase;
        }

        // what moves while a phase lasts
        switch (phase)
        {
            case GameWorkPhase.Runtime:
                Progress = runtime.Progress;
                var parts = new List<string>();
                if (runtime.BytesTotal > 0)
                {
                    parts.Add(S.Format("setup.game.bytes", ("done", SetupViewModel.Bytes(runtime.BytesDone)), ("total", SetupViewModel.Bytes(runtime.BytesTotal))));
                }
                if (runtime.State == RuntimeState.Downloading && runtime.BytesPerSecond > 0)
                {
                    parts.Add(SetupViewModel.Bytes((long)runtime.BytesPerSecond) + "/s");
                }
                if (runtime.Remaining is { } left)
                {
                    parts.Add(SetupViewModel.Minutes(S, left));
                }
                Detail = string.Join(" · ", parts);
                break;
            case GameWorkPhase.Setup:
                Progress = setup.Progress;
                Body = setup.StepLabel ?? CoreStrings.SetupStarting;
                var count = setup.StepCount > 0
                    ? S.Format("setup.game.step_of", ("n", (setup.StepIndex + 1).ToString(S.Culture)), ("of", setup.StepCount.ToString(S.Culture)))
                    : "";
                var time = setup.Remaining is { } remaining ? SetupViewModel.Minutes(S, remaining) : S["setup.game.estimating"];
                Detail = count.Length > 0 ? count + " · " + time : time;
                break;
        }

        // the buttons and the title change only with the phase
        var key = $"{phase}|{runtime.State}|{setup.NeedsDump}|{Driving}|{_shell.GameRuns}";
        if (key == _shown)
        {
            return;
        }
        _shown = key;
        Actions.Clear();
        ShowProgress = phase is GameWorkPhase.Runtime or GameWorkPhase.Setup;
        ShowSpinner = phase is GameWorkPhase.Runtime or GameWorkPhase.Setup or GameWorkPhase.Integrating;
        var name = _shell.RuntimeName;
        var close = new ModalAction(S["common.close"], () => _shell.CloseModal(this));
        // The update flow holds the screen while it works: it ends by itself, or with a button that says what stopping means.
        CanDismiss = !(Driving && phase is GameWorkPhase.Runtime or GameWorkPhase.Setup or GameWorkPhase.Integrating);
        Bottom = CanDismiss ? close : null;
        switch (phase)
        {
            case GameWorkPhase.UpdateNeeded:
                Title = S["gamework.update_title"];
                Body = _shell.GameRuns ? S["gamework.update_wait_game"] : S.Format("gamework.update_body", ("name", name));
                Detail = "";
                if (!_shell.GameRuns)
                {
                    Actions.Add(new ModalAction(S["update.now"], () =>
                    {
                        Driving = true;
                        (_startedRuntime, _startedSetup, _startedIntegration) = (false, false, false);
                        OnStateChanged();
                    }, ButtonKind.Outline, isDefault: true));
                }
                Bottom = new ModalAction(S["update.later"], () => _shell.CloseModal(this));
                break;
            case GameWorkPhase.Runtime:
                Title = runtime.State switch
                {
                    RuntimeState.Verifying => S["setup.game.verifying"],
                    RuntimeState.Extracting or RuntimeState.Installing => S.Format("setup.game.installing", ("name", name)),
                    _ => S.Format("setup.game.downloading", ("name", name)),
                };
                Body = runtime.State == RuntimeState.Downloading ? S[Driving ? "gamework.update_downloading_body" : "setup.game.downloading_body"] : "";
                if (runtime.State == RuntimeState.Downloading)
                {
                    // Only the download can wait: what was downloaded is kept, and PLAY stays locked until the update is in.
                    Actions.Add(new ModalAction(S[Driving ? "update.later" : "common.cancel"], () =>
                    {
                        Driving = false;
                        RelayCommand.Async(() => Launcher.CancelRuntimeInstall()).Execute(null);
                        _shell.CloseModal(this);
                    }));
                }
                break;
            case GameWorkPhase.RuntimeFailed:
                Title = S["setup.game.download_failed"];
                Body = runtime.Note ?? "";
                Detail = "";
                Actions.Add(new ModalAction(S["update.retry"], () => RelayCommand.Async(() => Launcher.InstallRuntimeAsync(null, CancellationToken.None)).Execute(null), ButtonKind.Outline, isDefault: true));
                break;
            case GameWorkPhase.SetupNeeded:
                Title = setup.NeedsDump ? S["setup.game.need_dump"] : S["setup.game.need_setup"];
                Body = setup.NeedsDump ? (setup.Message is { Length: > 0 } why ? why + "\n\n" : "") + S["setup.game.dump_legal"] : setup.Message ?? CoreStrings.SetupUnfinished;
                Detail = "";
                if (setup.NeedsDump)
                {
                    Actions.Add(new ModalAction(S["setup.game.pick_file"], () => PickDump(folder: false), ButtonKind.Outline, isDefault: true));
                    Actions.Add(new ModalAction(S["setup.game.pick_folder"], () => PickDump(folder: true)));
                }
                else
                {
                    Actions.Add(new ModalAction(S["setup.game.continue"], () => RunSetup(null), ButtonKind.Outline, isDefault: true));
                }
                break;
            case GameWorkPhase.Setup:
                Title = S["setup.game.setting_up"];
                Actions.Add(new ModalAction(S["setup.game.cancel_setup"], () =>
                {
                    Driving = false; // stopped by the player: it goes on when they say so
                    RelayCommand.Async(() => Launcher.CancelGameSetup()).Execute(null);
                }));
                break;
            case GameWorkPhase.SetupFailed:
                Title = S["setup.game.failed"];
                Body = setup.Message ?? "";
                Detail = "";
                if (setup.NeedsDump && setup.DumpPath is null)
                {
                    Actions.Add(new ModalAction(S["setup.game.pick_file"], () => PickDump(folder: false), ButtonKind.Outline, isDefault: true));
                }
                else
                {
                    Actions.Add(new ModalAction(S["setup.game.retry"], () => RunSetup(null), ButtonKind.Outline, isDefault: true));
                    Actions.Add(new ModalAction(S["setup.game.pick_other"], () => PickDump(folder: false)));
                }
                if (setup.LastLog is { } log)
                {
                    Actions.Add(new ModalAction(S["setup.game.open_log"], () =>
                    {
                        if (!Links.ShowInFolder(log))
                        {
                            _shell.ShowToast(S["setup.game.open_log"], log);
                        }
                    }));
                }
                break;
            case GameWorkPhase.Integrating:
                Title = S["setup.game.integration"];
                Body = Launcher.Game.Integration is { AsMod: true } ? CoreStrings.ModWorking : CoreStrings.PatchWorking;
                Detail = "";
                break;
            default:
                var game = Launcher.Game;
                if (game.Note is { } note && game.NoteIsError)
                {
                    // MKWMP's files did not go in: said here, with the way to try again
                    Title = S["settings.game.install_failed"];
                    Body = note;
                    Actions.Add(new ModalAction(S["setup.game.retry"], () => RelayCommand.Async(() => Launcher.InstallIntegrationAsync()).Execute(null), ButtonKind.Outline, isDefault: true));
                }
                else
                {
                    Title = S["setup.game.ready"];
                    Body = game.Install is { } install
                        ? S.Format("setup.game.ready_text", ("name", _shell.TargetName), ("version", install.Version ?? S["settings.game.unknown"]), ("game", install.GameVersion ?? "?"))
                        : Launcher.PlayProblem ?? "";
                }
                Detail = "";
                break;
        }
        _shell.RefreshHints();
    }

    /// <summary>The update flow's next step, each started once: the runtime, the setup from the copy it used, MKWMP's files; then done.</summary>
    private void Drive(GameWorkPhase phase)
    {
        if (_shell.GameRuns || Launcher.Session.Busy)
        {
            return; // never under a running game: the card says to close it
        }
        switch (phase)
        {
            case GameWorkPhase.UpdateNeeded when !_startedRuntime:
                _startedRuntime = true;
                RelayCommand.Async(() => Launcher.InstallRuntimeAsync(null, CancellationToken.None)).Execute(null);
                break;
            case GameWorkPhase.SetupNeeded when !_startedSetup && !Launcher.GameSetup.NeedsDump:
                _startedSetup = true;
                RunSetup(null);
                break;
            case GameWorkPhase.None:
                var game = Launcher.Game;
                if (game.Integration is { Ok: false } && !game.Working && !_startedIntegration && Launcher.Settings.Patch)
                {
                    _startedIntegration = true;
                    RelayCommand.Async(() => Launcher.InstallIntegrationAsync()).Execute(null);
                }
                else if (!game.Working && !(game.NoteIsError && _startedIntegration))
                {
                    Driving = false;
                    _shell.CloseModal(this);
                    _shell.ShowToast(S["setup.game.ready"], Launcher.PlayProblem ?? "");
                }
                else if (!game.Working)
                {
                    Driving = false; // the files did not go in: the card stays, with the reason and Try Again
                }
                break;
            case GameWorkPhase.RuntimeFailed or GameWorkPhase.SetupFailed:
                Driving = false; // the card stays with the reason and Try Again; it can be closed now
                break;
        }
    }

    private void RunSetup(string? dump) =>
        RelayCommand.Async(() => Launcher.RunGameSetupAsync(dump, CancellationToken.None)).Execute(null);

    /// <summary>The system's file (or folder) window for the player's own copy of the game; the setup starts with what was picked.</summary>
    private void PickDump(bool folder)
    {
        try
        {
            var owner = System.Windows.Application.Current?.MainWindow;
            if (folder)
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = S["setup.game.pick_folder_title"] };
                if (dialog.ShowDialog(owner) == true)
                {
                    RunSetup(dialog.FolderName);
                }
            }
            else
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = S["setup.game.pick_file_title"],
                    Filter = S["setup.game.pick_file_filter"] + "|*.nup;*.nx|*.*|*.*",
                    CheckFileExists = true,
                };
                if (dialog.ShowDialog(owner) == true)
                {
                    RunSetup(dialog.FileName);
                }
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // no file window here (some Wine builds, gamescope): the welcome's game page has a box to type the path
            App.Log("file dialog: " + e.Message);
            _shell.ShowToast(S["settings.game.no_dialog"], S["gamework.no_dialog_body"]);
        }
    }
}

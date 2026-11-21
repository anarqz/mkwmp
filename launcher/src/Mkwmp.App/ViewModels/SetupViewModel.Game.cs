// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using System.Windows;
using System.Windows.Input;
using Mkwmp.Core;
using Mkwmp.Core.Targets;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>Where "Your Game" is: each stage is one thing to do, in the order they must happen.</summary>
public enum GameStage
{
    /// <summary>An install is there and set up, with MKWMP in it: nothing to do.</summary>
    Ready,

    /// <summary>No install (or one MKWMP cannot use): download the pinned mkw-recomp, or point at one.</summary>
    NeedRuntime,

    /// <summary>The download / install is running (its modal shows the bytes).</summary>
    Downloading,

    /// <summary>The player's own copy of the game is needed (a .nup / .nx file, or a folder).</summary>
    NeedDump,

    /// <summary>A setup must run again or go on; the copy is known.</summary>
    NeedSetup,

    SettingUp,
    SetupFailed,

    /// <summary>MKWMP's files are being put into the game.</summary>
    Integrating,
    NeedIntegration,
}

/// <summary>
/// The welcome flow's "Your Game": everything between a bare machine and a game that PLAY can
/// start, done by the launcher. 1. mkw-recomp (the pinned build: <c>InstallRuntimeAsync</c>, with a
/// modal for the download; or "I already have it": the folder picker). 2. The player's own game
/// (a file or a folder; read where it is, never uploaded). 3. The setup (<c>RunGameSetupAsync</c>:
/// step, progress, time left, Cancel; Try Again and the log when it failed). 4. MKWMP's files
/// (<c>InstallIntegrationAsync</c>, by itself). Each can wait: "Later".
/// </summary>
public sealed partial class SetupViewModel
{
    private CancellationTokenSource? _runtimeCancel;
    private CancellationTokenSource? _setupCancel;
    private RuntimeModal? _runtimeModal;
    private bool _integrationTried;
    private string _dumpText = "";

    private void InitGame()
    {
        DownloadCommand = new RelayCommand(StartDownload, () => !Launcher.Runtime.Busy && !Launcher.Session.Busy);
        DumpFileCommand = new RelayCommand(PickDumpFile, () => CanStartSetup);
        DumpFolderCommand = new RelayCommand(PickDumpFolder, () => CanStartSetup);
        ShowDownloadCommand = new RelayCommand(ShowRuntimeModal, () => Launcher.Runtime.Busy);
        CancelSetupCommand = RelayCommand.Async(() => Launcher.CancelGameSetup(), () => Launcher.GameSetup.Busy);
        OpenLogCommand = new RelayCommand(() => Links.ShowInFolder(Launcher.GameSetup.LastLog ?? ""), () => Launcher.GameSetup.LastLog is not null);
        _dumpText = Launcher.GameSetup.DumpPath ?? "";
    }

    public ICommand DownloadCommand { get; private set; } = null!;

    public ICommand DumpFileCommand { get; private set; } = null!;

    public ICommand DumpFolderCommand { get; private set; } = null!;

    public ICommand OpenLogCommand { get; private set; } = null!;

    /// <summary>The download's card again (it was closed; the download went on).</summary>
    public ICommand ShowDownloadCommand { get; private set; } = null!;

    /// <summary>Stops the setup: the next run goes on from its last finished step.</summary>
    public ICommand CancelSetupCommand { get; private set; } = null!;

    public GameStage Stage
    {
        get
        {
            var game = Launcher.Game;
            var setup = Launcher.GameSetup;
            if (Launcher.Runtime.Busy)
            {
                return GameStage.Downloading;
            }
            if (setup.State == GameSetupState.Running)
            {
                return GameStage.SettingUp;
            }
            if (game.Install is null || game.Check is { Status: TargetStatus.Missing or TargetStatus.Unsupported })
            {
                return GameStage.NeedRuntime;
            }
            if (setup.State == GameSetupState.Failed)
            {
                return GameStage.SetupFailed;
            }
            if (setup.State == GameSetupState.NeedsSetup)
            {
                return setup.NeedsDump ? GameStage.NeedDump : GameStage.NeedSetup;
            }
            if (game.Working)
            {
                return GameStage.Integrating;
            }
            if (game.Integration is { Ok: false } || Launcher.PlayProblem == CoreStrings.InstallPatchFirst)
            {
                return GameStage.NeedIntegration;
            }
            return GameStage.Ready;
        }
    }

    /// <summary>"ready", "needruntime"…: which block of the page shows.</summary>
    public string StageName => Stage.ToString().ToLowerInvariant();

    public string RuntimeName => Launcher.Runtime.PinnedLabel is { } version ? $"{Game.TargetName} {version}" : Game.TargetName;

    /// <summary>Whose work the runtime is, wherever it is named.</summary>
    public string RuntimeCredit => Launcher.Runtime.Credit?.Localized().Summary ?? S["setup.welcome.credit"];

    public string GameHeading => Stage switch
    {
        GameStage.Ready => S["setup.game.ready"],
        GameStage.NeedRuntime => Launcher.Game.Searching ? S["setup.game.searching"] : S.Format("setup.game.need_runtime", ("name", Game.TargetName)),
        GameStage.Downloading => S.Format("setup.game.downloading", ("name", RuntimeName)),
        GameStage.NeedDump => S["setup.game.need_dump"],
        GameStage.NeedSetup => S["setup.game.need_setup"],
        GameStage.SettingUp => S["setup.game.setting_up"],
        GameStage.SetupFailed => S["setup.game.failed"],
        GameStage.Integrating or GameStage.NeedIntegration => S["setup.game.integration"],
        _ => "",
    };

    public string GameText => Stage switch
    {
        GameStage.Ready => Launcher.Game.Install is { } install
            ? S.Format("setup.game.ready_text", ("name", Game.TargetName), ("version", install.Version ?? S["settings.game.unknown"]), ("game", install.GameVersion ?? "?"))
            : "",
        GameStage.NeedRuntime => Launcher.Game.Check is { Status: TargetStatus.Missing or TargetStatus.Unsupported, Problem: { } problem }
            ? problem + "\n" + S.Format("setup.game.need_runtime_beside", ("name", RuntimeName))
            : S.Format("setup.game.need_runtime_text", ("name", RuntimeName), ("size", Bytes(Launcher.Runtime.DownloadSize)), ("space", Bytes(Launcher.Runtime.DiskSpace))),
        GameStage.Downloading => S["setup.game.downloading_text"],
        GameStage.NeedDump => S["setup.game.need_dump_text"],
        GameStage.NeedSetup => Launcher.GameSetup.Message ?? CoreStrings.SetupUnfinished,
        GameStage.SettingUp => S["setup.game.setting_up_text"],
        GameStage.SetupFailed => Launcher.GameSetup.Message ?? "",
        GameStage.Integrating => Launcher.Game.Integration is { AsMod: true } ? CoreStrings.ModWorking : CoreStrings.PatchWorking,
        GameStage.NeedIntegration => Launcher.Game.Note is { } note && Launcher.Game.NoteIsError ? note : Launcher.Game.Integration?.Summary ?? S["setup.game.integration_text"],
        _ => "",
    };

    /// <summary>The legal line under "your game": the player's own copy, read here, sent nowhere.</summary>
    public string DumpLegal => S["setup.game.dump_legal"];

    /// <summary>What still blocks the game, for the Done page.</summary>
    public string StageProblem => Stage == GameStage.Ready ? "" : Launcher.PlayProblem ?? GameHeading;

    public string RuntimeNote => Launcher.Runtime.Note is { } note && Launcher.Runtime.State is RuntimeState.Failed or RuntimeState.NotInstalled ? note : "";

    // ── the setup's progress ──

    public double SetupProgress => Launcher.GameSetup.Progress;

    public string SetupStepLabel => Launcher.GameSetup.StepLabel ?? CoreStrings.SetupStarting;

    public string SetupStepCount => Launcher.GameSetup.StepCount > 0
        ? S.Format("setup.game.step_of", ("n", (Launcher.GameSetup.StepIndex + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)), ("of", Launcher.GameSetup.StepCount.ToString(System.Globalization.CultureInfo.CurrentCulture)))
        : "";

    public string SetupRemaining => Launcher.GameSetup.Remaining is { } left ? Minutes(S, left) : S["setup.game.estimating"];

    public bool HasLog => Launcher.GameSetup.LastLog is not null;

    private bool CanStartSetup => !Launcher.GameSetup.Busy && !Launcher.Runtime.Busy && !Launcher.Session.Busy;

    /// <summary>The copy's path typed by hand, for who has it at hand. Enter starts the setup with it.</summary>
    public string DumpText
    {
        get => _dumpText;
        set => Set(ref _dumpText, (value ?? "").Trim().Trim('"'));
    }

    public string DumpTextCaption => Links.IsWine ? S["setup.game.dump_typed_wine"] : S["setup.game.dump_typed"];

    public void SubmitDump()
    {
        if (_dumpText.Length > 0 && CanStartSetup)
        {
            RunSetup(_dumpText);
        }
    }

    // ── the page's two buttons ──

    private string GamePrimaryLabel => Stage switch
    {
        GameStage.NeedRuntime => S.Format("setup.game.download", ("name", RuntimeName)),

        GameStage.NeedDump => S["setup.game.pick_file"],
        GameStage.NeedSetup => S["setup.game.continue"],
        GameStage.SetupFailed => S["setup.game.retry"],
        GameStage.Integrating => "…",
        GameStage.NeedIntegration => S["setup.game.install_mkwmp"],
        _ => S["common.next"],
    };

    private bool GameCanPrimary => Stage switch
    {
        GameStage.NeedRuntime => !Launcher.Session.Busy && Launcher.Runtime.State != RuntimeState.Unavailable,
        GameStage.NeedDump or GameStage.NeedSetup or GameStage.SetupFailed => CanStartSetup,
        GameStage.Integrating => false,
        _ => true,
    };

    /// <summary>"Later": everything but a finished game can wait (what runs goes on behind the next pages).</summary>
    private bool GameCanWait => Stage is not (GameStage.Ready or GameStage.Integrating or GameStage.Downloading or GameStage.SettingUp);

    private Task GamePrimaryAsync()
    {
        switch (Stage)
        {
            case GameStage.NeedRuntime:
                StartDownload();
                break;

            case GameStage.NeedDump:
                PickDumpFile();
                break;
            case GameStage.NeedSetup:
                RunSetup(null);
                break;
            case GameStage.SetupFailed:
                // The same copy again (the setup goes on from its last finished step), unless there was none to begin with.
                if (Launcher.GameSetup.NeedsDump && Launcher.GameSetup.DumpPath is null && _dumpText.Length == 0)
                {
                    PickDumpFile();
                }
                else
                {
                    RunSetup(_dumpText.Length > 0 ? _dumpText : null);
                }
                break;
            case GameStage.NeedIntegration:
                return Launcher.InstallIntegrationAsync();
            default:
                // ready; or a download or the setup is running, and goes on behind the next pages
                Step = SetupStep.Online;
                break;
        }
        return Task.CompletedTask;
    }

    private void EnterGame()
    {
        if (Launcher.Game.Install is null && !Launcher.Game.Searching && !Launcher.Runtime.Busy)
        {
            RelayCommand.Async(() => Launcher.FindGameAsync()).Execute(null); // the launcher looks by itself first
        }
        FollowGame();
    }

    /// <summary>The launcher changed: what the page does by itself (MKWMP's files, once; the download's modal going away).</summary>
    private void FollowGame()
    {
        if (_step != SetupStep.Game)
        {
            return;
        }
        if (Stage == GameStage.NeedIntegration && !_integrationTried && !Launcher.Session.Busy)
        {
            _integrationTried = true;
            RelayCommand.Async(() => Launcher.InstallIntegrationAsync()).Execute(null);
        }
        if (_dumpText.Length == 0 && Launcher.GameSetup.DumpPath is { } known)
        {
            _dumpText = known;
        }
    }

    // ── 1. the runtime ──

    private void StartDownload()
    {
        _runtimeCancel?.Dispose();
        _runtimeCancel = new CancellationTokenSource();
        var token = _runtimeCancel.Token;
        RelayCommand.Async(() => Launcher.InstallRuntimeAsync(null, token)).Execute(null);
        ShowRuntimeModal();
    }

    private void ShowRuntimeModal()
    {
        _runtimeModal = new RuntimeModal(Shell, RuntimeName, StartDownload);
        Shell.ShowModal(_runtimeModal);
    }

    // ── 2 and 3. the player's game, and the setup ──

    private void PickDumpFile() => PickDump(folderOnly: false);

    private void PickDumpFolder() => PickDump(folderOnly: true);

    /// <summary>
    /// The launcher's own picker (never the system's dialog), starting where the last copy was: a package file or the
    /// folder that holds the game, or only a folder. It gives back only what the setup takes (a wrong choice is refused
    /// inside it, with the setup's own reason), and the setup starts with it.
    /// </summary>
    private void PickDump(bool folderOnly) => RelayCommand.Async(async () =>
    {
        if (await Shell.PickAsync(PickerRequests.GameCopy(Shell, folderOnly, _dumpText.Length > 0 ? _dumpText : null)).ConfigureAwait(true) is { } path)
        {
            RunSetup(path);
        }
    }).Execute(null);

    private void RunSetup(string? dump)
    {
        if (!CanStartSetup)
        {
            return;
        }
        if (dump is not null)
        {
            DumpText = dump;
        }
        _setupCancel?.Dispose();
        _setupCancel = new CancellationTokenSource();
        var token = _setupCancel.Token;
        RelayCommand.Async(() => Launcher.RunGameSetupAsync(dump, token)).Execute(null);
    }
}

/// <summary>
/// The download of the game's runtime, as a card over the page: bytes, speed, time left, Cancel
/// (what was downloaded is kept). It follows <c>Launcher.Runtime</c>, closes by itself when the
/// install is done, and stays to say why when it failed. Close only hides it: the download goes on.
/// </summary>
public sealed class RuntimeModal : ModalViewModel
{
    private readonly ShellViewModel _shell;
    private readonly string _name;
    private readonly Action _retry;
    private RuntimeState? _shown;
    private bool _wasBusy;

    public RuntimeModal(ShellViewModel shell, string name, Action retry)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _name = name;
        _retry = retry;
        OnStateChanged();
    }

    public override void OnStateChanged()
    {
        var s = _shell.S;
        var runtime = _shell.Launcher.Runtime;
        Progress = runtime.Progress;
        if (runtime.Busy)
        {
            _wasBusy = true;
            var parts = new List<string>();
            if (runtime.BytesTotal > 0)
            {
                parts.Add(s.Format("setup.game.bytes", ("done", SetupViewModel.Bytes(runtime.BytesDone)), ("total", SetupViewModel.Bytes(runtime.BytesTotal))));
            }
            if (runtime.State == RuntimeState.Downloading && runtime.BytesPerSecond > 0)
            {
                parts.Add(SetupViewModel.Bytes((long)runtime.BytesPerSecond) + "/s");
            }
            if (runtime.Remaining is { } left)
            {
                parts.Add(SetupViewModel.Minutes(s, left));
            }
            Detail = string.Join(" · ", parts);
        }
        if (_shown == runtime.State)
        {
            return;
        }
        _shown = runtime.State;
        Actions.Clear();
        ShowProgress = runtime.Busy;
        ShowSpinner = runtime.Busy;
        var close = new ModalAction(s["common.close"], () => _shell.CloseModal(this));
        switch (runtime.State)
        {
            case RuntimeState.Downloading:
                Title = s.Format("setup.game.downloading", ("name", _name));
                Body = s["setup.game.downloading_body"];
                Actions.Add(new ModalAction(s["common.cancel"], () => RelayCommand.Async(() => _shell.Launcher.CancelRuntimeInstall()).Execute(null)));
                Bottom = close;
                break;
            case RuntimeState.Verifying:
                Title = s["setup.game.verifying"];
                Bottom = close;
                break;
            case RuntimeState.Extracting or RuntimeState.Installing:
                Title = s.Format("setup.game.installing", ("name", _name));
                Bottom = close;
                break;
            case RuntimeState.Failed:
                Title = s["setup.game.download_failed"];
                Body = runtime.Note ?? "";
                Actions.Add(new ModalAction(s["update.retry"], _retry, Controls.ButtonKind.Outline, isDefault: true));
                Bottom = close;
                break;
            default:
                if (_wasBusy && runtime.Note is { Length: > 0 } note && runtime.State != RuntimeState.Installed)
                {
                    // stopped: what was downloaded is kept
                    Title = s["setup.game.download_paused"];
                    Body = note;
                    Bottom = close;
                }
                else if (_wasBusy)
                {
                    _shell.CloseModal(this); // installed: the page behind shows the next thing to do
                }
                else
                {
                    Title = s.Format("setup.game.downloading", ("name", _name));
                    ShowSpinner = true;
                    Bottom = close;
                }
                break;
        }
    }
}

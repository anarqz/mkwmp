// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.Core;
using Mkwmp.Core.Targets;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The work that makes the game playable (the runtime's download, the setup), seen from every
/// page: one line and one fraction for the Game tile, home's status and the indicator at the top;
/// the card with all of it (<see cref="GameWorkModal"/>); a toast when it ends behind another
/// page; and the update of the game after a launcher update, started by the launcher itself.
/// </summary>
public sealed partial class ShellViewModel
{
    private GameWorkPhase _workSeen;
    private bool _gameUpdateOffered;
    private ICommand? _gameWorkCommand;

    /// <summary>The name of the selected way to run the game ("mkw-recomp").</summary>
    public string TargetName
    {
        get
        {
            var id = Launcher.Game.Install?.TargetId ?? Launcher.Settings.Target;
            return Launcher.Game.Targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;
        }
    }

    /// <summary>"mkw-recomp 0.1.16-mkwmp.1", "mkw-recomp 0.1.16-mkwmp.1 (no AVX2)": the runtime the launcher installs.</summary>
    public string RuntimeName => Launcher.Runtime.PinnedLabel is { } version ? $"{TargetName} {version}" : TargetName;

    /// <summary>Where the game is on its way to playable, from the launcher's state.</summary>
    public GameWorkPhase GameWorkPhase
    {
        get
        {
            var runtime = Launcher.Runtime;
            var setup = Launcher.GameSetup;
            return runtime.Busy ? GameWorkPhase.Runtime
                : setup.State == GameSetupState.Running ? GameWorkPhase.Setup
                : runtime.State == RuntimeState.Failed ? GameWorkPhase.RuntimeFailed
                : runtime.State == RuntimeState.UpdateAvailable ? GameWorkPhase.UpdateNeeded
                : Launcher.Game.Install is null || Launcher.Game.Check is { Status: TargetStatus.Missing or TargetStatus.Unsupported } ? GameWorkPhase.None
                : setup.State == GameSetupState.Failed ? GameWorkPhase.SetupFailed
                : setup.State == GameSetupState.NeedsSetup ? GameWorkPhase.SetupNeeded
                : Launcher.Game.Working ? GameWorkPhase.Integrating
                : GameWorkPhase.None;
        }
    }

    /// <summary>A download, an install or the setup is running now.</summary>
    public bool IsGameWorking => GameWorkPhase is GameWorkPhase.Runtime or GameWorkPhase.Setup;

    /// <summary>Something about the game's setup is running, failed or needed: the Game tile and PLAY lead to its card.</summary>
    public bool HasGameWork => GameWorkPhase is not (GameWorkPhase.None or GameWorkPhase.Integrating);

    /// <summary>0..1 of what runs; NaN when nothing with a fraction does.</summary>
    public double GameWorkProgress => GameWorkPhase switch
    {
        GameWorkPhase.Runtime => Launcher.Runtime.Progress,
        GameWorkPhase.Setup => Launcher.GameSetup.Progress,
        _ => double.NaN,
    };

    /// <summary>"42%" for the indicator.</summary>
    public string GameWorkPercent => double.IsNaN(GameWorkProgress) ? "" : Math.Clamp(GameWorkProgress, 0, 1).ToString("P0", S.Culture);

    /// <summary>What runs, by name: "Setting up the game", "Downloading mkw-recomp 0.1.16-mkwmp"; "" when nothing does.</summary>
    public string GameWorkTitle => GameWorkPhase switch
    {
        GameWorkPhase.Setup => S["setup.game.setting_up"],
        GameWorkPhase.Runtime => Launcher.Runtime.State switch
        {
            RuntimeState.Verifying => S["setup.game.verifying"],
            RuntimeState.Extracting or RuntimeState.Installing => S.Format("setup.game.installing", ("name", RuntimeName)),
            _ => S.Format("setup.game.downloading", ("name", RuntimeName)),
        },
        _ => "",
    };

    /// <summary>"Setting up the game · 42% · 6 minutes left"; null when nothing runs.</summary>
    public string? GameWorkText
    {
        get
        {
            if (!IsGameWorking)
            {
                return null;
            }
            var left = GameWorkPhase == GameWorkPhase.Setup ? Launcher.GameSetup.Remaining : Launcher.Runtime.Remaining;
            return left is { } time
                ? S.Format("gamework.line_left", ("what", GameWorkTitle), ("percent", GameWorkPercent), ("left", SetupViewModel.Minutes(S, time)))
                : S.Format("gamework.line", ("what", GameWorkTitle), ("percent", GameWorkPercent));
        }
    }

    /// <summary>The short form for a tile: "42% · 6 minutes left".</summary>
    public string? GameWorkShort
    {
        get
        {
            if (!IsGameWorking)
            {
                return null;
            }
            var left = GameWorkPhase == GameWorkPhase.Setup ? Launcher.GameSetup.Remaining : Launcher.Runtime.Remaining;
            return left is { } time ? GameWorkPercent + " · " + SetupViewModel.Minutes(S, time) : GameWorkPercent;
        }
    }

    /// <summary>The state of the game's setup in a word, for Settings › Game's row; "" when there is nothing to say.</summary>
    public string GameWorkState => GameWorkPhase switch
    {
        GameWorkPhase.Runtime or GameWorkPhase.Setup => S.Format("gamework.state.running", ("percent", GameWorkPercent)),
        GameWorkPhase.RuntimeFailed or GameWorkPhase.SetupFailed => S["gamework.state.failed"],
        GameWorkPhase.SetupNeeded => S["gamework.state.needed"],
        GameWorkPhase.UpdateNeeded => S["gamework.state.update"],
        _ => Launcher.GameSetup.Available && Launcher.Game.Install is not null ? S["common.done"] : "",
    };

    /// <summary>Whether the first-run welcome's own game page shows this now (it has its own progress and its own buttons).</summary>
    private bool WelcomeShowsGame => Page is SetupViewModel { Step: SetupStep.Game };

    /// <summary>The indicator at the top of every page: while something runs, and not where the page itself shows it.</summary>
    public bool ShowGameWorkIndicator => IsGameWorking && _modal is not (GameWorkModal or RuntimeModal) && !WelcomeShowsGame && !IsOverlay;

    /// <summary>Opens the card (the indicator, the Game tile, PLAY's "not ready", Settings › Game).</summary>
    public ICommand GameWorkCommand => _gameWorkCommand ??= new RelayCommand(() => ShowGameWork());

    /// <summary>The "Game setup" card; <paramref name="driving"/>: it runs the whole update by itself.</summary>
    public void ShowGameWork(bool driving = false)
    {
        if (_modal is GameWorkModal open && (open.Driving || !driving))
        {
            return;
        }
        ShowModal(new GameWorkModal(this, driving));
    }

    /// <summary>Whether what blocks PLAY is the game's runtime or setup: its card explains it, live.</summary>
    public bool GameWorkExplains(string problem)
    {
        if (!HasGameWork)
        {
            return false;
        }
        var setup = Launcher.GameSetup;
        return problem == CoreStrings.SetupRunning || problem == setup.Message
            || problem == CoreStrings.RuntimeBusy(TargetName)
            || (Launcher.Runtime.PinnedLabel is { } pinned && problem == CoreStrings.RuntimeUpdateNeeded(TargetName, pinned))
            || (setup.State is GameSetupState.NeedsSetup or GameSetupState.Failed && Launcher.Game.Check is { Status: TargetStatus.NeedsRebuild });
    }

    /// <summary>
    /// After every change of the launcher: a toast when the work ended behind another page, and
    /// the update of the game, which the launcher starts by itself.
    /// </summary>
    private void WatchGameWork()
    {
        var phase = GameWorkPhase;
        var before = _workSeen;
        _workSeen = phase;
        Raise(nameof(ShowGameWorkIndicator));
        var watched = _modal is GameWorkModal or RuntimeModal || WelcomeShowsGame;
        if (before != phase && !watched)
        {
            if (before == GameWorkPhase.Setup && phase == GameWorkPhase.SetupFailed)
            {
                ShowToast(S["setup.game.failed"], Launcher.GameSetup.Message ?? "");
            }
            else if (before == GameWorkPhase.Setup && Launcher.GameSetup.State == GameSetupState.Done)
            {
                ShowToast(S["setup.game.ready"], Launcher.PlayProblem ?? "");
            }
            else if (before == GameWorkPhase.Runtime && phase == GameWorkPhase.RuntimeFailed)
            {
                ShowToast(S["setup.game.download_failed"], Launcher.Runtime.Note ?? "");
            }
            else if (before == GameWorkPhase.Runtime && Launcher.Runtime.State == RuntimeState.Installed && Launcher.Runtime.Note is { Length: > 0 } installed)
            {
                ShowToast(installed, phase == GameWorkPhase.SetupNeeded ? Launcher.GameSetup.Message ?? "" : "");
            }
        }
        OfferGameUpdate(phase);
    }

    /// <summary>
    /// A launcher update brought a newer runtime, or the game must be set up again for the one
    /// that is there (and the copy it was built from is still where it was): the launcher does it
    /// now, in a card that holds the screen. Once per run, after the welcome, never over a game,
    /// and never over another card (that one first: the next change of state asks again).
    /// </summary>
    private void OfferGameUpdate(GameWorkPhase phase)
    {
        if (_gameUpdateOffered || !AutoGameUpdate || !Launcher.Settings.Ui.SetupDone || Page is SetupViewModel || _modal is not null
            || GameRuns || Launcher.Session.Busy || IsOverlay)
        {
            return;
        }
        var again = phase == GameWorkPhase.SetupNeeded && !Launcher.GameSetup.NeedsDump
            && Launcher.GameSetup.Reason is GameSetupReason.RuntimeChanged or GameSetupReason.Unfinished or GameSetupReason.GameMismatch;
        if (phase == GameWorkPhase.UpdateNeeded || again)
        {
            _gameUpdateOffered = true;
            App.Log($"game update: started by the launcher ({phase}, {Launcher.Runtime.InstalledVersion} -> {Launcher.Runtime.PinnedVersion})");
            ShowGameWork(driving: true);
        }
    }

    /// <summary>False: the launcher never starts the game's update by itself (the screenshot harness, which shows the state it was asked for).</summary>
    public bool AutoGameUpdate { get; set; } = true;
}

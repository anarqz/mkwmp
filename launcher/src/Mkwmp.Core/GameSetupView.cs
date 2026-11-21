// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

public enum GameSetupState
{
    /// <summary>Nothing to do: the game is set up, or this game has no setup the launcher runs (<see cref="GameSetupView.Available"/>).</summary>
    Idle,

    /// <summary>
    /// PLAY is blocked until <see cref="ILauncher.RunGameSetupAsync"/> ran: <see cref="GameSetupView.Reason"/> and
    /// <see cref="GameSetupView.Message"/> say why, <see cref="GameSetupView.NeedsDump"/> whether the player must pick their game first.
    /// </summary>
    NeedsSetup,

    /// <summary>A setup is under way: the step, its label, the fractions and the time left.</summary>
    Running,

    /// <summary>The last setup failed: <see cref="GameSetupView.Message"/> says why and what to do, <see cref="GameSetupView.LastLog"/> has it all. Running it again continues it.</summary>
    Failed,

    /// <summary>A setup finished in this session and the game is ready.</summary>
    Done,
}

/// <summary>The game's setup as the launcher runs it (see <see cref="IGameTarget.SetupStatus"/>), for the window.</summary>
/// <param name="Available">The game in use is one the launcher sets up itself.</param>
/// <param name="Reason">Why a setup is needed; null when none is.</param>
/// <param name="Message">Why it is needed, why it failed, or what just happened (stopped, done); null when nothing to say.</param>
/// <param name="NeedsDump">The player must pick their own copy of the game before the setup can run.</param>
/// <param name="DumpPath">The copy the last setup used (it may have moved since); null when none.</param>
/// <param name="Step">The running step's own name ("compile"); "" before the first, null when not running.</param>
/// <param name="StepLabel">What the player reads ("Building the game").</param>
/// <param name="StepProgress">0..1 of the step.</param>
/// <param name="StepIndeterminate">The step gives no count: <paramref name="StepProgress"/> is a guess from the clock.</param>
/// <param name="Progress">0..1 of the whole setup.</param>
/// <param name="Remaining">An estimate; null while unknown.</param>
/// <param name="GameVersion">The version of the player's game the setup read ("1.8.0"); null until it has.</param>
/// <param name="ErrorCode">Failed: the cause as one stable word ("nogame", "space", "failed", "running"...).</param>
/// <param name="LastLog">The file with the whole log of the last setup of this session; null when none ran.</param>
/// <param name="Mode">How the last setup took <paramref name="DumpPath"/>'s data (read in place or copied): what a change of copy starts from.</param>
public sealed record GameSetupView(
    GameSetupState State,
    bool Available,
    GameSetupReason? Reason,
    string? Message,
    bool NeedsDump,
    string? DumpPath,
    string? Step,
    string? StepLabel,
    int StepIndex,
    int StepCount,
    double StepProgress,
    bool StepIndeterminate,
    double Progress,
    TimeSpan? Remaining,
    string? GameVersion,
    string? ErrorCode,
    string? LastLog,
    GameDataMode Mode = GameDataMode.Link)
{
    public static GameSetupView None { get; } = new(GameSetupState.Idle, false, null, null, false, null, null, null, 0, 0, 0, false, 0, null, null, null, null);

    public bool Busy => State == GameSetupState.Running;
}

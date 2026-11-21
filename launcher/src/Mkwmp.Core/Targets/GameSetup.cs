// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Targets;

/// <summary>Why a game must be set up (again) before it can start.</summary>
public enum GameSetupReason
{
    /// <summary>Never set up here: the player picks their copy of the game.</summary>
    NotSetUp,

    /// <summary>A setup was started and did not reach its end: running it again continues it.</summary>
    Unfinished,

    /// <summary>The runtime was installed or updated since the last setup: the steps whose tools changed run again.</summary>
    RuntimeChanged,

    /// <summary>The game code on disk was built for another runtime: this one would refuse it.</summary>
    GameMismatch,

    /// <summary>The player's copy of the game is no longer where the last setup read it from, and the game reads it in place.</summary>
    DumpMissing,

    /// <summary>The player's copy changed since the game was built from it (updated elsewhere): the game is set up again from it.</summary>
    CopyChanged,
}

/// <summary>What a setup would be for, before it runs.</summary>
/// <param name="Message">One line for the player: it blocks PLAY until the setup ran.</param>
/// <param name="NeedsDump">The player must pick their copy of the game: there is no earlier one to use, or it is gone.</param>
/// <param name="DumpPath">The copy the last setup used (it may be gone); null when there was none.</param>
public sealed record GameSetupPlan(GameSetupReason Reason, string Message, bool NeedsDump, string? DumpPath);

/// <summary>A target's setup for one install: what <see cref="IGameTarget.SetupStatus"/> answers.</summary>
/// <param name="Needed">Null: the game is set up for this runtime.</param>
/// <param name="DumpPath">The player's copy of the game that the last setup used; null when none is recorded.</param>
/// <param name="Mode">How the last setup took that copy's data: read in place, or copied into the install.</param>
public sealed record GameSetupStatus(GameSetupPlan? Needed, string? DumpPath, GameDataMode Mode = GameDataMode.Link);

/// <summary>What happens to the game's data (about 22 GiB): read where the player keeps it, or copied into the install.</summary>
public enum GameDataMode
{
    /// <summary>Read in place: nothing is copied, and the player's copy must stay where it is.</summary>
    Link,

    Copy,
}

/// <param name="DumpPath">
/// The player's own copy of the game (for mkw-recomp: a <c>.nup</c> or <c>.nx</c> file, or a folder with
/// <c>exefs/</c> and <c>romfs/</c>). Null: the one the last setup used, with its mode.
/// The launcher only ever reads it; it never copies, moves or uploads it.
/// </param>
/// <param name="Mode">Only with a <paramref name="DumpPath"/>. Changing it (or the copy) redoes only the steps that depend on it (mkw-recomp: exefs, romfs, cleanup).</param>
/// <param name="LogFolder">Where the whole log of this run is kept (<c>setup-*.log</c>); null: nowhere.</param>
/// <param name="Rebuild">Every step that builds the game runs again (not only those whose inputs changed): "Set up the game again".</param>
public sealed record GameSetupRequest(string? DumpPath, GameDataMode Mode = GameDataMode.Link, string? LogFolder = null, bool Rebuild = false);

/// <summary>Where a running setup is.</summary>
/// <param name="Step">The step's own name ("compile"); "" before the first one.</param>
/// <param name="Label">What the player reads ("Building the game").</param>
/// <param name="StepIndex">From 0.</param>
/// <param name="StepFraction">0..1 of this step. An estimate from the clock when <paramref name="Indeterminate"/>.</param>
/// <param name="Indeterminate">The step reports no count: show a moving bar rather than a number.</param>
/// <param name="Fraction">0..1 of the whole setup (steps that need not run are not counted).</param>
/// <param name="Remaining">An estimate; null while there is nothing to estimate from.</param>
/// <param name="GameVersion">The version of the player's game, once the setup has read it.</param>
public readonly record struct GameSetupProgress(
    string Step,
    string Label,
    int StepIndex,
    int StepCount,
    double StepFraction,
    bool Indeterminate,
    double Fraction,
    TimeSpan? Remaining,
    string? GameVersion);

/// <summary>How a setup ended. A cancelled one throws <see cref="OperationCanceledException"/> instead.</summary>
/// <param name="ErrorCode">Null when <paramref name="Ok"/>; else a stable word for the cause ("nogame", "space", "failed"...), for the window to pick an action by.</param>
/// <param name="Message">Null when <paramref name="Ok"/>; else what went wrong and what to do, for the player.</param>
/// <param name="Detail">The setup's own detail for the error (a path, a count, a tool's message), unchanged.</param>
/// <param name="FailedStep">The step that was under way.</param>
/// <param name="Log">The file with the whole log of the run; null when none was written.</param>
/// <param name="ExitCode">The setup program's; null when it never started.</param>
public sealed record GameSetupResult(bool Ok, string? ErrorCode, string? Message, string? Detail, string? FailedStep, string? GameVersion, string? Log, int? ExitCode)
{
    public static GameSetupResult Refused(string code, string message, string? detail = null) => new(false, code, message, detail, null, null, null, null);
}

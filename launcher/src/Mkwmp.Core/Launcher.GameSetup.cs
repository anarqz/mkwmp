// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// The game's setup, run by the launcher through the selected target
/// (<see cref="IGameTarget.SetupStatus"/>, <see cref="IGameTarget.RunSetupAsync"/>): for mkw-recomp,
/// building the game from the player's own copy, so the player never meets mkw-recomp's own
/// screens (launcher/docs/RECOMP_INTEGRATION.md, stage 1). A setup that is needed blocks PLAY as
/// <see cref="PlayProblem"/>, after the game folder and before the integration; the window offers
/// <see cref="RunGameSetupAsync"/> for it.
/// </summary>
public sealed partial class Launcher
{
    private readonly Lock _setupStart = new();

    /// <summary>The setup in progress (under <see cref="_setupStart"/>).</summary>
    private CancellationTokenSource? _setupCancel;

    // ---- under _gate ----
    /// <summary>The target's answer for the game folder, from the last look at it; null when it has no setup.</summary>
    private GameSetupStatus? _setupStatus;
    private GameSetupProgress? _setupProgress;
    private (string Text, bool IsError, string? Code)? _setupNote;
    private bool _setupDone;
    private string? _setupLog;
    private string? _setupGameVersion;

    private volatile GameSetupView _gameSetupView = GameSetupView.None;

    public GameSetupView GameSetup => _gameSetupView;

    /// <summary>A setup is under way (PLAY, and an install of the runtime, wait for it).</summary>
    private bool SetupRunning
    {
        get
        {
            lock (_setupStart)
            {
                return _setupCancel is not null;
            }
        }
    }

    /// <summary>Asks the target about the install's setup (reads a small file; no lock held).</summary>
    private GameSetupStatus? InspectSetup(GameInstall? install)
    {
        if (install is null)
        {
            return null;
        }
        try
        {
            return _targets.Get(install.TargetId).SetupStatus(install);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // the folder went away between two looks: the next refresh says so
        }
    }

    /// <summary>A setup only matters for an install the target accepts: a missing or unsupported one has its own problem first.</summary>
    private GameSetupStatus? SetupFor(TargetCheck? check) =>
        _install is not null && check?.Status is not (TargetStatus.Missing or TargetStatus.Unsupported) ? _setupStatus : null;

    /// <summary>What blocks PLAY because of the setup (under the lock).</summary>
    private string? SetupProblem(TargetCheck? check) =>
        _setupProgress is not null ? Strings.SetupRunning : SetupFor(check)?.Needed?.Message;

    /// <summary>The view, from the state (under the lock).</summary>
    private GameSetupView GameSetupNow(TargetCheck? check)
    {
        var status = SetupFor(check);
        var plan = status?.Needed;
        var p = _setupProgress;
        var state = p is not null ? GameSetupState.Running
            : _setupNote is { IsError: true } ? GameSetupState.Failed
            : plan is not null ? GameSetupState.NeedsSetup
            : _setupDone ? GameSetupState.Done
            : GameSetupState.Idle;
        var message = state switch
        {
            GameSetupState.Running => null,
            GameSetupState.Failed => _setupNote?.Text,
            GameSetupState.NeedsSetup => _setupNote?.Text ?? plan?.Message,
            GameSetupState.Done => _setupNote?.Text ?? Strings.SetupDone,
            _ => _setupNote?.Text,
        };
        return new GameSetupView(
            state,
            status is not null || p is not null,
            plan?.Reason,
            message,
            plan?.NeedsDump ?? false,
            status?.DumpPath,
            p?.Step,
            p?.Label,
            p?.StepIndex ?? 0,
            p?.StepCount ?? 0,
            p?.StepFraction ?? 0,
            p?.Indeterminate ?? false,
            p?.Fraction ?? 0,
            p?.Remaining,
            p?.GameVersion ?? _setupGameVersion,
            state == GameSetupState.Failed ? _setupNote?.Code : null,
            _setupLog,
            status?.Mode ?? GameDataMode.Link);
    }

    public Task RunGameSetupAsync(string? dumpPath, CancellationToken cancel) => RunGameSetupAsync(dumpPath, null, cancel);

    /// <summary>
    /// Why the player's copy of the game cannot be changed now (<see cref="ChangeGameCopyAsync"/>), or null: the game
    /// in use must have a setup the launcher runs, and nothing may hold the game folder (a setup, the runtime's install,
    /// PLAY, the game itself). With <paramref name="dumpPath"/>, also the first setup's own check of that copy. Reads a
    /// few files and lists processes: not for the window's thread.
    /// </summary>
    public string? GameCopyChangeProblem(string? dumpPath)
    {
        var s = Settings;
        var target = _targets.Get(s.Target);
        if (GameFinder.Detect(target, s.Game) is not { } install)
        {
            return Strings.SetGameFolder(target.FolderHint);
        }
        if (InspectSetup(install) is null)
        {
            return Strings.SetupNone;
        }
        if (SetupRunning)
        {
            return Strings.SetupAlreadyRunning;
        }
        lock (_runtimeStart)
        {
            if (_runtimeCancel is not null)
            {
                return Strings.SetupWaitForInstall;
            }
        }
        if (SessionHoldsGame)
        {
            return Strings.PressStopFirst;
        }
        if (target.IsRunning(install))
        {
            return Strings.SetupCloseGame;
        }
        return dumpPath is null ? null : target.GameCopyProblem(dumpPath.Trim().Trim('"'));
    }

    /// <summary>
    /// "Use another copy of the game", once the game is set up: the setup again from <paramref name="dumpPath"/>, its
    /// data read in place or copied (<paramref name="mode"/>; null: as the last setup did). The setup itself redoes
    /// only what depends on the copy (mkw-recomp's prepare.py: exefs, romfs, cleanup). Null when the setup ran (its
    /// outcome is in <see cref="GameSetup"/>); else why it was not started, and nothing was changed.
    /// </summary>
    public async Task<string?> ChangeGameCopyAsync(string dumpPath, GameDataMode? mode, CancellationToken cancel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpPath);
        if (await Task.Run(() => GameCopyChangeProblem(dumpPath), cancel).ConfigureAwait(false) is { } why)
        {
            Note($"game copy: not changed to {dumpPath}: {why}");
            return why;
        }
        Note($"game copy: changing to {dumpPath} ({(mode is { } m ? m.ToString().ToLowerInvariant() : "as before")})");
        await RunGameSetupAsync(dumpPath, mode, cancel).ConfigureAwait(false);
        return null;
    }

    /// <param name="mode">Only with a <paramref name="dumpPath"/>; null: as the last setup took its copy (read in place for a first one).</param>
    private async Task RunGameSetupAsync(string? dumpPath, GameDataMode? mode, CancellationToken cancel)
    {
        CancellationTokenSource both;
        lock (_setupStart)
        {
            if (_setupCancel is not null)
            {
                return; // one at a time: the window shows the one that runs
            }
            both = _setupCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel, _life.Token);
        }
        var dump = string.IsNullOrWhiteSpace(dumpPath) ? null : dumpPath.Trim().Trim('"');
        try
        {
            var s = Settings;
            var target = _targets.Get(s.Target);
            if (GameFinder.Detect(target, s.Game) is not { } install)
            {
                throw new LauncherException(Strings.SetGameFolder(target.FolderHint));
            }
            if (SessionHoldsGame)
            {
                throw new LauncherException(Strings.PressStopFirst);
            }
            lock (_runtimeStart)
            {
                if (_runtimeCancel is not null)
                {
                    throw new LauncherException(Strings.SetupWaitForInstall);
                }
            }
            if (target.SetupStatus(install) is not { } before)
            {
                throw new LauncherException(Strings.SetupNone);
            }
            var data = mode ?? before.Mode; // a new copy is taken as the last one was, unless the player said otherwise
            Note($"game setup: {target.DisplayName} {install.Version} in {install.Root}, game copy: {(dump is null ? "the last setup's" : $"{dump} ({data.ToString().ToLowerInvariant()})")}");
            Mutate(() =>
            {
                (_setupNote, _setupDone) = (null, false);
                _setupProgress = new GameSetupProgress("", Strings.SetupStarting, 0, 0, 0, true, 0, null, null);
            });
            var shown = _clock.GetTimestamp();
            var progress = new SetupReport(p =>
            {
                // A few times a second is plenty for a bar; a new step, a new label and the end always show.
                var now = _clock.GetTimestamp();
                lock (_gate)
                {
                    if (_setupProgress is not { } last)
                    {
                        return; // a late report, after the end
                    }
                    if (last.Step == p.Step && last.Label == p.Label && last.GameVersion == p.GameVersion && p.Fraction < 1
                        && _clock.GetElapsedTime(shown, now) < TimeSpan.FromMilliseconds(100))
                    {
                        return;
                    }
                }
                shown = now;
                Mutate(() => _setupProgress = _setupProgress is null ? null : p);
            });
            var request = new GameSetupRequest(dump, data, Path.Combine(LauncherFolder, "logs"));
            var result = await Task.Run(() => target.RunSetupAsync(install, request, progress, both.Token), both.Token).ConfigureAwait(false);
            Note(result.Ok
                ? $"game setup: done, game {result.GameVersion ?? "?"} (log: {result.Log ?? "none"})"
                : $"game setup: {result.ErrorCode}{(result.FailedStep is null ? "" : $" in {result.FailedStep}")}: {result.Message} (log: {result.Log ?? "none"})");
            // The game folder is looked at again first, so the view goes from Running to its outcome in one step.
            await RefreshAfterSetupAsync(() =>
            {
                _setupProgress = null;
                _setupLog = result.Log ?? _setupLog;
                _setupGameVersion = result.GameVersion ?? _setupGameVersion;
                _setupDone = result.Ok;
                _setupNote = result.Ok ? null : (result.Message ?? Strings.SetupError(result.ErrorCode ?? "", null, null, null), true, result.ErrorCode);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Note("game setup: stopped");
            await RefreshAfterSetupAsync(() =>
            {
                _setupProgress = null;
                _setupLog = RecentSetupLog() ?? _setupLog;
                _setupNote = (Strings.SetupCancelled, false, "cancelled");
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
        {
            Note($"game setup: {e.Message}");
            Mutate(() => (_setupProgress, _setupNote) = (null, (e.Message, true, "refused")));
        }
        finally
        {
            lock (_setupStart)
            {
                _setupCancel = null;
            }
            both.Dispose();
        }
    }

    /// <summary>Looks at the game folder again and applies the setup's outcome with it, in one change.</summary>
    private async Task RefreshAfterSetupAsync(Action outcome)
    {
        var status = await Task.Run(() =>
        {
            InspectGame(out var install, out _, out _);
            return InspectSetup(install);
        }).ConfigureAwait(false);
        Mutate(() =>
        {
            _setupStatus = status;
            outcome();
        });
        await Task.Run(RefreshGame).ConfigureAwait(false);
    }

    /// <summary>The newest setup log in the launcher's logs folder (a cancelled setup returns none itself).</summary>
    private string? RecentSetupLog()
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(LauncherFolder, "logs"), "setup-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task CancelGameSetup()
    {
        lock (_setupStart)
        {
            if (_setupCancel is { } running)
            {
                running.Cancel();
                return Task.CompletedTask;
            }
        }
        Mutate(() => (_setupNote, _setupDone) = (null, false));
        return Task.CompletedTask;
    }

    /// <summary>Progress reported on the working thread itself (Progress&lt;T&gt; would post to a context and reorder).</summary>
    private sealed class SetupReport(Action<GameSetupProgress> report) : IProgress<GameSetupProgress>
    {
        public void Report(GameSetupProgress value) => report(value);
    }
}

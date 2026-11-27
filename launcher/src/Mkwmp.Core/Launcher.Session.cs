// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// PLAY: take a seat (or queue), write the game's session file, start the game, keep the seat.
/// Offline (no server chosen) it only starts the game: no seat, no server asked, and a session
/// file that names this launcher for the overlay and nothing else.
/// </summary>
public sealed partial class Launcher
{
    private readonly Signal _stop = new();
    private volatile SeatSession? _seat;

    /// <summary>The session under way is an offline one (under <see cref="_gate"/>): its status says so, and it has no seat phase.</summary>
    private bool _offlineSession;

    /// <summary>A session has the game (a seat is held, or an offline game was started): the integration and the setup wait for STOP.</summary>
    private bool SessionHoldsGame
    {
        get
        {
            lock (_gate)
            {
                return _seat is not null || (_offlineSession && _sessionState is SessionState.Starting or SessionState.Running);
            }
        }
    }
    private Task _sessionTask = Task.CompletedTask;

    /// <summary>The running (or last) PLAY session: completes when it ends.</summary>
    internal Task SessionTask
    {
        get
        {
            lock (_gate)
            {
                return _sessionTask;
            }
        }
    }

    /// <summary>
    /// Starts the session in the background and returns at once: <see cref="Session"/> tells how
    /// it goes (a session lasts as long as the player plays). Nothing happens while one runs.
    /// </summary>
    public Task PlayAsync()
    {
        if (Update.Required)
        {
            Note("PLAY refused: this launcher installs its update first");
            return Task.CompletedTask;
        }
        Mutate(() =>
        {
            if (_sessionState is SessionState.Starting or SessionState.Running)
            {
                return;
            }
            (_sessionState, _sessionError, _seatSnapshot, _gameRunning) = (SessionState.Starting, null, null, false);
            _offlineSession = !IsOnline(_config.Settings);
            _nextendoSession = NextendoMode; // no MKWMP seat either: the game goes online on Nextendo (Launcher.Nextendo.cs)
            (_nextendoServed, _nextendoToasted) = (0, null);
            _stop.Reset(); // a STOP pressed while idle is not for this session
            _sessionTask = Task.Run(RunSessionAsync);
        });
        return Task.CompletedTask;
    }

    /// <summary>STOP: frees the seat (the game, if running, is left alone) and waits for the session to end.</summary>
    public async Task StopAsync()
    {
        if (!Session.Busy)
        {
            return;
        }
        _stop.Set();
        await Task.WhenAny(SessionTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    /// <summary>Leaving: free the seat, show offline, give both a moment.</summary>
    private async Task WaitIdleAsync(TimeSpan patience)
    {
        if (Session.Busy)
        {
            _stop.Set();
        }
        await Task.WhenAny(Task.WhenAll(SignOutAsync(), SessionTask), Task.Delay(patience)).ConfigureAwait(false);
    }

    private async Task RunSessionAsync()
    {
        string? error = null;
        try
        {
            await SessionAsync().ConfigureAwait(false);
        }
        catch (LauncherException e)
        {
            error = e.Message;
        }
        catch (ApiException e)
        {
            ConsentRefused(e);
            error = Reason(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            error = e.Message;
        }
        _seat = null;
        Volatile.Write(ref _nextendoServed, 0);
        if (error is not null)
        {
            Note(error);
        }
        Mutate(() => (_sessionState, _sessionError, _seatSnapshot, _gameRunning) = (error is null ? SessionState.Idle : SessionState.Failed, error, null, false));
    }

    private async Task SessionAsync()
    {
        var life = _life.Token;
        var s = Settings;
        var target = _targets.Get(s.Target);
        var install = GameFinder.Detect(target, s.Game);
        if (ConfigProblem(s, install is not null, target.FolderHint, IsOnline(s)) is { } missing)
        {
            throw new LauncherException(missing);
        }
        if (HostedGameProblem is { } hosted)
        {
            throw new LauncherException(hosted); // that game has its own seat: a second one would only fight it
        }
        if (!LocalApiUp)
        {
            throw new LauncherException(Strings.AnotherLauncher(s.LocalPort));
        }
        // Before any file is written or the server asked: the hook only fits some runtimes and game versions.
        Note($"game version: {install!.GameVersion ?? "unknown"}");
        var check = target.Check(install);
        if (check.Problem is { } gate)
        {
            throw new LauncherException(check.Explain is null ? gate : $"{gate} ({check.Explain})");
        }
        // The game must be set up for this runtime (Launcher.GameSetup.cs): the game itself would stop at once, or run old code.
        if (SetupRunning)
        {
            throw new LauncherException(Strings.SetupRunning);
        }
        if (target.SetupNeeded(install) is { } setup)
        {
            throw new LauncherException(setup.Message);
        }
        // The rest of the play checklist (the runtime's update, the game's version): PLAY never starts past a row that
        // blocks it. MKWMP's files are the exception: PLAY brings them in line itself, below, and refuses if it cannot.
        if (Checklist.Blocking.FirstOrDefault(i => i.Id is CheckId.Runtime or CheckId.GameCopy or CheckId.GameVersion or CheckId.GameSetup) is { } blocked)
        {
            throw new LauncherException(blocked.Status);
        }
        if (!IsOnline(s))
        {
            await OfflineSessionAsync(s, target, install, life).ConfigureAwait(false);
            return;
        }
        var api = Api(s.Server);
        var seat = new SeatSession(api, s.Seat, s.Name, s.Pin, Note, _clock, _options.BusyRetry);
        var (lobby, lobbyCa, voicePort) = await api.LobbyAsync(life).ConfigureAwait(false);
        try
        {
            await SyncIntegrationAsync(force: false).ConfigureAwait(false);
        }
        catch (LauncherException e)
        {
            Note($"game patch: {e.Message}");
        }
        var integration = target.Inspect(install, Package);
        if (!integration.Ok)
        {
            throw new LauncherException($"{integration.Summary} ({string.Join("; ", integration.Lines)})");
        }
        var address = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{(_local.Port != 0 ? _local.Port : s.LocalPort)}");
        // Always the current server's lobby and certificate: a stale file from another server would make the game reject this one.
        void Write(string? ticket, string toLobby) =>
            target.WriteSession(install, new SessionFile(toLobby, s.Name, address, Token, voicePort, ticket, s.Slot, lobbyCa, LauncherExe));

        Write(null, lobby); // never a stale ticket
        // Consent, and a profile the server deleted (a month unused) comes back here, before the
        // seat server is asked for a seat.
        await PresenceTokenAsync(life).ConfigureAwait(false);
        _seat = seat;
        IGameProcess? game = null;
        try
        {
            if (s.Seat == SeatMode.Start)
            {
                Note($"taking a seat on {s.Server}");
                // The game starts only once seated.
                if (await TakeSeatAsync(seat, life).ConfigureAwait(false) is not { } seated)
                {
                    return; // STOP while queued
                }
                Write(seated.Ticket, seated.Lobby);
            }
            else
            {
                // No seat until the game goes online, but the profile must be good.
                await PresenceTokenAsync(life).ConfigureAwait(false);
                Note("you'll get a seat when you go online in the game");
            }

            EnforceOnlineOwned(target, install);
            EnforceOverlayOwned(target, install);
            SyncModSettings(target, install, force: true);
            var launches = LaunchesGame(s) && target.Capabilities.HasFlag(TargetCapabilities.Launch);
            if (launches)
            {
                Note("starting the game");
                GameStarting();
                game = target.Launch(install, new LaunchOptions(s.LaunchCommand.Length == 0 ? null : s.LaunchCommand));
            }
            else
            {
                Note("ready: start the game from Steam");
                GameStarting();
            }

            while (true)
            {
                if (await seat.TickAsync(life).ConfigureAwait(false) is { } renewed)
                {
                    Write(s.Seat == SeatMode.Start ? renewed.Ticket : null, renewed.Lobby);
                }
                var snap = await seat.SnapshotAsync().ConfigureAwait(false);
                bool running;
                if (game is not null)
                {
                    if (game.HasExited)
                    {
                        Note(string.Create(CultureInfo.InvariantCulture, $"game exited (exit code: {game.ExitCode}); seat released"));
                        GameEnded(install, game.ExitCode);
                        return;
                    }
                    running = true;
                }
                else
                {
                    running = target.IsRunning(install);
                }
                Mutate(() => (_sessionState, _seatSnapshot, _gameRunning) = (SessionState.Running, snap, running));
                if (await _stop.WaitAsync(_options.Tick, life).ConfigureAwait(false))
                {
                    Note("stopped; seat released");
                    return;
                }
            }
        }
        finally
        {
            await seat.ReleaseAsync().ConfigureAwait(false);
            game?.Dispose();
        }
    }

    /// <summary>
    /// PLAY with no server: the game, and nothing online. The integration is still brought in line
    /// (the overlay is MKWMP's), and the session file names only this launcher's local API
    /// (<c>launcher</c>, <c>launcher_token</c>, <c>launcher_exe</c>): with no <c>server</c>,
    /// <c>name</c> or <c>ticket</c> the mod stays dormant for online play (its Online Play asks the
    /// local API for a seat and is refused with <see cref="Strings.OnlineOffInGame"/>) and still
    /// brings the launcher over the game. The session lasts while the game runs.
    /// </summary>
    private async Task OfflineSessionAsync(LauncherSettings s, IGameTarget target, GameInstall install, CancellationToken life)
    {
        try
        {
            await SyncIntegrationAsync(force: false).ConfigureAwait(false);
        }
        catch (LauncherException e)
        {
            Note($"game patch: {e.Message}");
        }
        var integration = target.Inspect(install, Package);
        if (!integration.Ok)
        {
            throw new LauncherException($"{integration.Summary} ({string.Join("; ", integration.Lines)})");
        }
        var address = string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{(_local.Port != 0 ? _local.Port : s.LocalPort)}");
        if (_nextendoSession)
        {
            // Nextendo is asked first (may the account go online, where, which certificate); then its certificate and
            // the session file that names it. Nothing of an MKWMP session stays: no server, ticket or lobby certificate.
            target.WriteSession(install, await NextendoSessionFileAsync(address, life).ConfigureAwait(false));
        }
        else
        {
            // Also takes away what an online session left there: the server, the name, a ticket, the lobby's certificate.
            target.WriteSession(install, SessionFile.Offline(address, Token, LauncherExe));
            Note("playing offline: no server is set (SETTINGS > ONLINE), so no seat is taken and no server is contacted");
        }

        IGameProcess? game = null;
        try
        {
            if (_nextendoSession)
            {
                EnforceOnlineOwned(target, install); // the game goes online all the same: what online play needs of its settings
            }
            EnforceOverlayOwned(target, install);
            SyncModSettings(target, install, force: true);
            var launches = LaunchesGame(s) && target.Capabilities.HasFlag(TargetCapabilities.Launch);
            if (launches)
            {
                Note("starting the game");
                GameStarting();
                game = target.Launch(install, new LaunchOptions(s.LaunchCommand.Length == 0 ? null : s.LaunchCommand));
            }
            else
            {
                Note("ready: start the game from Steam");
                GameStarting();
            }
            var seen = false; // started elsewhere (Steam): the session ends when that game does
            while (true)
            {
                bool running;
                if (game is not null)
                {
                    if (game.HasExited)
                    {
                        Note(string.Create(CultureInfo.InvariantCulture, $"game exited (exit code: {game.ExitCode})"));
                        GameEnded(install, game.ExitCode);
                        return;
                    }
                    running = true;
                }
                else
                {
                    running = target.IsRunning(install);
                    if (seen && !running)
                    {
                        Note("the game was closed");
                        GameEnded(install, null);
                        return;
                    }
                    seen |= running;
                }
                Mutate(() => (_sessionState, _gameRunning) = (SessionState.Running, running));
                if (await _stop.WaitAsync(_options.Tick, life).ConfigureAwait(false))
                {
                    Note("stopped");
                    return;
                }
            }
        }
        finally
        {
            game?.Dispose();
        }
    }

    /// <summary>
    /// seat = "start": join and wait (queued) until seated. The seated ticket and lobby; null if
    /// STOP was pressed while waiting. A refusal or an unreachable server is an error (the game
    /// is not started).
    /// </summary>
    private async Task<(string Ticket, string Lobby)?> TakeSeatAsync(SeatSession seat, CancellationToken life)
    {
        switch (await seat.WantAsync(life).ConfigureAwait(false))
        {
            case SeatAnswer.Seated s:
                return (s.Ticket, s.Lobby);
            case SeatAnswer.Refused r:
                throw new LauncherException(r.Message);
            case SeatAnswer.Unavailable u:
                Note($"no seat: {u.Message}");
                if ((await seat.SnapshotAsync().ConfigureAwait(false)).Phase != SeatPhase.Busy)
                {
                    throw new LauncherException(Strings.Unreachable);
                }
                break; // busy (gamertag in use, queue full): wait here, retrying
            default:
                break;
        }
        while (true)
        {
            var changed = await seat.TickAsync(life).ConfigureAwait(false);
            var snap = await seat.SnapshotAsync().ConfigureAwait(false);
            if (snap.Phase == SeatPhase.Refused)
            {
                throw new LauncherException(snap.Message ?? "");
            }
            if (changed is not null)
            {
                return changed;
            }
            if (snap.Phase == SeatPhase.Seated && await seat.WantAsync(life).ConfigureAwait(false) is SeatAnswer.Seated s)
            {
                return (s.Ticket, s.Lobby);
            }
            Mutate(() => (_sessionState, _seatSnapshot, _gameRunning) = (SessionState.Running, snap, false));
            if (await _stop.WaitAsync(_options.Tick, life).ConfigureAwait(false))
            {
                Note("stopped; seat released");
                return null;
            }
        }
    }
}

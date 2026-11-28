// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net.Sockets;
using System.Text.Json;
using Mkwmp.Core.Net;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core;

/// <summary>What runs beside the window: the local API, the server's status, GitHub stars, updates.</summary>
public sealed partial class Launcher
{
    /// <summary>Wakes the status loop (the server or the consent changed).</summary>
    private readonly Signal _statusWake = new();
    private Task _startupUpdates = Task.CompletedTask;
    private int _started;
    private int _startupDeferred;

    /// <summary>The start-up update check and integration sync: <c>--headless</c> waits for it before PLAY.</summary>
    internal Task StartupUpdates => _startupUpdates;

    public async Task StartAsync(CancellationToken cancel)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        var s = Settings;
        // The session header: what a bug report needs to know first.
        Log.Line($"mkwmp-launcher {Version} (update channel: {UpdateChannels.Name(Settings.UpdateChannel)})");
        Log.Line($"os: {SupportBundle.Os}" + (Platform.WineHost.How is { } wine ? $" under Wine ({wine})" : ""));
        Log.Line($"config: {ConfigPath}");
        Log.Line($"server: {(IsOnline(s) ? s.Server : "none (offline)")}");
        Log.Line($"game folder: {s.Game}");
        Log.Line($"seat mode: {s.Seat}");
        if ((_options.Executable ?? LauncherOptions.SelfExecutable()) is { } exe)
        {
            Updater.CleanOld(exe); // the <exe>.old of an update by a launcher from before the helper
        }
        RemoveLegacyRaceHistory(); // nothing about other players is stored: what 1.5.4 kept in races\ goes (Launcher.Lobby.cs)
        if (_options.BindLocalApi)
        {
            await BindLocalApiAsync(s.LocalPort, cancel).ConfigureAwait(false);
        }
        if (!_options.BackgroundLoops)
        {
            return;
        }
        var life = _life.Token;
        _ = Task.Run(RefreshCrashes, CancellationToken.None); // a crash from before this start, not answered yet
        _ = Task.Run(() => StatusLoopAsync(life), CancellationToken.None);
        _ = Task.Run(() => FriendsLoopAsync(life), CancellationToken.None);
        _ = Task.Run(() => NextendoFriendsLoopAsync(life), CancellationToken.None); // Launcher.NextendoFriends.cs
        _ = Task.Run(() => NextendoPresenceLoopAsync(life), CancellationToken.None);
        _ = Task.Run(() => NextendoLobbiesLoopAsync(life), CancellationToken.None); // Launcher.NextendoLobbies.cs
        _ = Task.Run(() => NextendoAvatarsLoopAsync(life), CancellationToken.None); // Launcher.NextendoAvatars.cs
        _ = Task.Run(() => GameLoopAsync(life), CancellationToken.None);
        if (!_options.Headless)
        {
            _ = Task.Run(() => StarsLoopAsync(life), CancellationToken.None);
        }
        if (_options.OverlayHost)
        {
            // Standing by for a game that already runs: nothing of the launcher's own start-up until the player opens it.
            SetUpdate(new UpdateView(UpdateState.UpToDate, null, null, 0, null, Strings.UpToDate));
            Volatile.Write(ref _startupDeferred, 1);
            return;
        }
        BeginStartupWork(s);
    }

    private void BeginStartupWork(LauncherSettings s)
    {
        // Keep the start-up entry pointing at this launcher (it may have moved).
        if (!_options.Headless && s.Background.StartWithSystem)
        {
            ApplyAutostart(true);
        }
        _startupUpdates = Task.Run(StartupUpdatesAsync, CancellationToken.None);
    }

    /// <summary>
    /// The overlay host became the ordinary launcher (the player opened it): what
    /// <see cref="LauncherOptions.OverlayHost"/> held back at the start runs now, once.
    /// </summary>
    public void LeaveOverlayHostMode()
    {
        if (Interlocked.Exchange(ref _startupDeferred, 0) == 1)
        {
            BeginStartupWork(Settings);
        }
    }

    /// <summary>
    /// The local API the game calls for its seat. A launcher that just replaced itself (update)
    /// or is closing may still hold the port: retry for a while, then keep retrying in the
    /// background with PLAY refused until it is ours.
    /// </summary>
    private async Task BindLocalApiAsync(int port, CancellationToken cancel)
    {
        var asked = _clock.GetTimestamp();
        SocketException? last = null;
        while (_clock.GetElapsedTime(asked) < TimeSpan.FromSeconds(3) && !cancel.IsCancellationRequested)
        {
            try
            {
                _local.Listen(port);
                Mutate(() => _localApiUp = true);
                return;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                last = e;
                await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
            }
            catch (SocketException e)
            {
                last = e;
                break;
            }
        }
        Note($"cannot open 127.0.0.1:{port} ({last?.Message}); is another launcher running? PLAY waits until it is free");
        var life = _life.Token;
        _ = Task.Run(
            async () =>
            {
                while (!life.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), life).ConfigureAwait(false);
                        _local.Listen(port);
                        Note($"127.0.0.1:{port} is free now: the game's local API is up");
                        Mutate(() => _localApiUp = true);
                        return;
                    }
                    catch (SocketException)
                    {
                        // still taken
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// The server's public status, every 5 s, for whatever server is set (choosing one is what asks
    /// for it; the consent comes after signing in). Never while offline (no server chosen): the
    /// loop then only waits to be woken.
    /// </summary>
    private async Task StatusLoopAsync(CancellationToken life)
    {
        while (!life.IsCancellationRequested)
        {
            try
            {
                var s = Settings;
                if (!IsOnline(s))
                {
                    Mutate(() => _server = NotAsked);
                    await _statusWake.WaitAsync(TimeSpan.FromSeconds(30), life).ConfigureAwait(false);
                    continue;
                }
                ServerView view;
                try
                {
                    var status = await Api(s.Server).StatusAsync(life).ConfigureAwait(false);
                    view = new ServerView(ServerState.Online, status.Name, status.SeatsTotal, status.Seated, status.Queued, null);
                }
                catch (ApiException e)
                {
                    view = new ServerView(ServerState.Offline, null, 0, 0, 0, e.Message);
                }
                // The answer of a server that was left meanwhile (Disconnect, another server) is nobody's.
                Mutate(() => _server = ClientConfig.SameServer(_config.Settings.Server, s.Server) ? view : _server);
                await _statusWake.WaitAsync(TimeSpan.FromSeconds(5), life).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The game folder's verdict, redone every 2 s: the player updates the runtime or enables the mod with the launcher open.</summary>
    private async Task GameLoopAsync(CancellationToken life)
    {
        while (!life.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), life).ConfigureAwait(false);
                if (!Game.Working && !Game.Searching)
                {
                    RefreshGame();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The project's GitHub star count, every 10 minutes.</summary>
    private async Task StarsLoopAsync(CancellationToken life)
    {
        while (!life.IsCancellationRequested)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(life);
                limit.CancelAfter(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{Updater.DefaultApi}/repos/{Updater.Repo}");
                req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                using var res = await _http.SendAsync(req, limit.Token).ConfigureAwait(false);
                var body = await res.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
                if (Json.Parse(body).At("stargazers_count").Long() is { } stars)
                {
                    Mutate(() => _stars = (int)Math.Clamp(stars, 0, int.MaxValue));
                    RaiseChanged();
                }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException || (e is OperationCanceledException && !life.IsCancellationRequested))
            {
                // offline: the button just shows no count
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(600), life).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

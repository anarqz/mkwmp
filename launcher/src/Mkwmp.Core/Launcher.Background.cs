// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Diagnostics;
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
    private Release? _availableRelease;
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
        Log.Line($"mkwmp-launcher {Version}");
        Log.Line($"os: {SupportBundle.Os}");
        Log.Line($"config: {ConfigPath}");
        Log.Line($"server: {(IsOnline(s) ? s.Server : "none (offline)")}");
        Log.Line($"game folder: {s.Game}");
        Log.Line($"seat mode: {s.Seat}");
        if ((_options.Executable ?? LauncherOptions.SelfExecutable()) is { } exe)
        {
            Updater.CleanOld(exe);
        }
        if (_options.BindLocalApi)
        {
            await BindLocalApiAsync(s.LocalPort, cancel).ConfigureAwait(false);
        }
        if (!_options.BackgroundLoops)
        {
            return;
        }
        var life = _life.Token;
        _ = Task.Run(() => StatusLoopAsync(life), CancellationToken.None);
        _ = Task.Run(() => FriendsLoopAsync(life), CancellationToken.None);
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

    // ---- updates --------------------------------------------------------------------------

    private void SetUpdate(UpdateView view)
    {
        _updateView = view;
        RaiseChanged();
    }

    /// <summary>Start-up: look for a newer launcher (unless this one was just updated), then bring the game's integration in line.</summary>
    private async Task StartupUpdatesAsync()
    {
        var s = Settings;
        if (!UpdatesOn(s))
        {
            await SyncQuietlyAsync().ConfigureAwait(false);
            SetUpdate(new UpdateView(UpdateState.Off, null, null, 0, null, Strings.UpdatesOff));
            return;
        }
        var available = false;
        if (Environment.GetEnvironmentVariable("MKWMP_UPDATED") is null)
        {
            available = await CheckAsync(manual: false).ConfigureAwait(false);
            if (available && _options.ApplyUpdatesAutomatically)
            {
                await ApplyUpdateAsync(_life.Token).ConfigureAwait(false);
                if (Update.State == UpdateState.Restarting)
                {
                    return;
                }
                available = false;
            }
        }
        await SyncQuietlyAsync().ConfigureAwait(false);
        if (!available)
        {
            SetUpdate(new UpdateView(UpdateState.UpToDate, null, null, 0, null, Strings.UpToDate));
        }
    }

    private async Task SyncQuietlyAsync()
    {
        try
        {
            await SyncIntegrationAsync(force: false).ConfigureAwait(false);
        }
        catch (LauncherException e)
        {
            Note($"game patch: {e.Message}");
        }
        catch (OperationCanceledException)
        {
            // the launcher is closing
        }
    }

    /// <summary>Asks GitHub for a newer release; <see cref="Update"/> says Available (with its notes) or UpToDate.</summary>
    public async Task CheckForUpdatesAsync() => await CheckAsync(manual: true).ConfigureAwait(false);

    private async Task<bool> CheckAsync(bool manual)
    {
        SetUpdate(new UpdateView(UpdateState.Checking, null, null, 0, null, Strings.CheckingForUpdates));
        try
        {
            var current = SemVer.TryParse(Version) ?? throw new LauncherException($"{Version} is not a version");
            var newest = Updater.Newest(await _updater.ReleasesAsync(_life.Token).ConfigureAwait(false), current);
            lock (_gate)
            {
                _availableRelease = newest;
            }
            if (newest is null)
            {
                SetUpdate(new UpdateView(UpdateState.UpToDate, null, null, 0, null, Strings.UpToDate));
                return false;
            }
            var version = newest.Version!.ToString();
            SetUpdate(new UpdateView(UpdateState.Available, version, newest.Notes, 0, null, Strings.UpdateAvailable(version)));
            return true;
        }
        catch (Exception e) when (e is LauncherException or OperationCanceledException)
        {
            Note($"update check failed: {e.Message}");
            // At start a failed check is no news (offline is normal); asked for, it is the answer.
            SetUpdate(manual
                ? new UpdateView(UpdateState.Failed, null, null, 0, null, Strings.UpdateFailed(e.Message))
                : new UpdateView(UpdateState.UpToDate, null, null, 0, null, Strings.UpToDate));
            return false;
        }
    }

    public async Task ApplyUpdateAsync(CancellationToken cancel)
    {
        Release? release;
        lock (_gate)
        {
            release = _availableRelease;
        }
        if (release is null && (!await CheckAsync(manual: true).ConfigureAwait(false) || (release = _availableRelease) is null))
        {
            return;
        }
        var version = release.Version?.ToString() ?? release.Tag;
        var notes = release.Notes;
        try
        {
            var exe = _options.Executable ?? LauncherOptions.SelfExecutable() ?? throw new LauncherException(Strings.CannotUpdateItself);
            Note($"updating the launcher {Version} -> {release.Tag}");
            SetUpdate(new UpdateView(UpdateState.Downloading, version, notes, 0, null, Strings.Downloading(version)));
            using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, _life.Token);
            var sums = await _updater.SumsAsync(release, both.Token).ConfigureAwait(false);
            var began = _clock.GetTimestamp();
            var shown = began;
            var progress = new InlineProgress(p =>
            {
                // A few times a second is plenty for a progress bar.
                var now = _clock.GetTimestamp();
                if (p.Total is not > 0 || (_clock.GetElapsedTime(shown, now) < TimeSpan.FromMilliseconds(100) && p.Got < p.Total))
                {
                    return;
                }
                shown = now;
                var done = Math.Clamp((double)p.Got / p.Total.Value, 0, 1);
                var elapsed = _clock.GetElapsedTime(began, now);
                TimeSpan? remaining = done > 0.02 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - done) / done) : null;
                SetUpdate(new UpdateView(UpdateState.Downloading, version, notes, done, remaining, Strings.Downloading(version)));
            });
            var bytes = await _updater.AssetAsync(release, sums, Updater.LauncherAsset, progress, both.Token).ConfigureAwait(false);
            Updater.ReplaceExecutable(exe, bytes);
            SetUpdate(new UpdateView(UpdateState.Restarting, version, notes, 1, TimeSpan.Zero, Strings.UpdatedRestarting));
            await RestartIntoAsync(exe).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The player closed the dialog: the update stays on offer.
            SetUpdate(new UpdateView(UpdateState.Available, version, notes, 0, null, Strings.UpdateAvailable(version)));
        }
        catch (Exception e) when (e is LauncherException or OperationCanceledException or IOException)
        {
            Note($"update failed: {e.Message}");
            SetUpdate(new UpdateView(UpdateState.Failed, version, notes, 0, null, Strings.UpdateFailed(e.Message)));
        }
    }

    /// <summary>Frees the seat, starts the new launcher with the same arguments, and ends this one.</summary>
    private async Task RestartIntoAsync(string exe)
    {
        await WaitIdleAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (_options.Exit is { } testExit)
        {
            testExit(0);
            return;
        }
        var code = 0;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in _options.Arguments)
            {
                start.ArgumentList.Add(arg);
            }
            // The new launcher does not look for an update again at once (a release whose own version is wrong would loop).
            start.Environment["MKWMP_UPDATED"] = "1";
            using var process = Process.Start(start);
            if (_options.Headless && process is not null)
            {
                // A service or script waits on this process: stay until the new one is done, and pass its answer on.
                await process.WaitForExitAsync().ConfigureAwait(false);
                code = process.ExitCode;
            }
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            Note($"starting the new launcher failed: {e.Message}");
            code = 1;
        }
        Log.Dispose();
        Environment.Exit(code);
    }

    /// <summary>Progress reported on the downloading thread itself (Progress&lt;T&gt; would post to a context and reorder).</summary>
    private sealed class InlineProgress(Action<(long Got, long? Total)> report) : IProgress<(long Got, long? Total)>
    {
        public void Report((long Got, long? Total) value) => report(value);
    }
}

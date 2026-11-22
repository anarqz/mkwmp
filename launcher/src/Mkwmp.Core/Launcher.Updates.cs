// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Updates;
using Mkwmp.Update;

namespace Mkwmp.Core;

/// <summary>
/// The launcher's own update (launcher/docs/UPDATES.md): check, download into the staging folder,
/// "ready to install", and the hand-over to the helper that swaps the files once this process is gone.
/// </summary>
public sealed partial class Launcher
{
    private Release? _availableRelease;
    private StagedUpdate? _staged;
    private volatile bool _installAfterGame;
    private int _updateWork;
    private int _afterGameLoop;

    // A newer release was found and must be installed (LauncherOptions.RequireUpdates), until it is, or until
    // installing failed and the player chose to go on without it for this run.
    private volatile bool _mustUpdate;
    private volatile bool _waived;

    /// <summary>This run enforces updates: a real, windowed launcher that can replace itself.</summary>
    private bool Enforces => _options.RequireUpdates && !_options.Headless && UpdateExe is not null;

    private IReadOnlyList<string> UpdateKeys => _options.ReleaseKeys ?? Updater.ReleaseKeys;

    /// <summary>The executable an update replaces; null under <c>dotnet</c> or a test host (then there is nothing to update).</summary>
    private string? UpdateExe => _options.Executable ?? LauncherOptions.SelfExecutable();

    private static string RootOf(string exe) => Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ".";

    private void SetUpdate(UpdateView view)
    {
        if (view.State is UpdateState.UpToDate or UpdateState.Off)
        {
            _mustUpdate = false;
        }
        view = view with { Required = _mustUpdate && !_waived && view.State is not (UpdateState.UpToDate or UpdateState.Off or UpdateState.Checking) };
        lock (_gate)
        {
            _updateView = view;
            Rebuild(); // the checklist's update row
        }
        RaiseChanged();
    }

    private void SetReady(StagedUpdate staged, bool gameBlocks = false)
    {
        var after = _installAfterGame;
        SetUpdate(new UpdateView(
            UpdateState.ReadyToInstall, staged.Version, staged.Notes, 1, null,
            after ? Strings.UpdateAfterGame : gameBlocks ? Strings.UpdateNeedsNoGame : Strings.UpdateReady(staged.Version),
            GameBlocks: gameBlocks || after, AfterGame: after));
    }

    /// <summary>Start-up: look for a newer launcher (unless this one was just updated), then bring the game's integration in line.</summary>
    private async Task StartupUpdatesAsync()
    {
        var s = Settings;
        if (UpdateExe is { } exe && SemVer.TryParse(Version) is { } current)
        {
            SelfUpdate.Clean(RootOf(exe), current, Note); // what the update that brought this version left
        }
        if (!UpdatesOn(s))
        {
            await SyncQuietlyAsync().ConfigureAwait(false);
            SetUpdate(new UpdateView(UpdateState.Off, null, null, 0, null, Strings.UpdatesOff));
            return;
        }
        var available = false;
        // Set by the helper: this launcher is the result of an update, or of one that could not be installed.
        var environment = _options.UpdateEnvironment ?? Environment.GetEnvironmentVariable;
        var failed = environment(UpdateRunner.FailedVariable);
        if (environment(UpdateRunner.UpdatedVariable) is null)
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
            else if (available)
            {
                await EnforceUpdateAsync().ConfigureAwait(false); // not an offer: downloaded now, installed before anything else
            }
        }
        await SyncQuietlyAsync().ConfigureAwait(false);
        if (failed is not null)
        {
            // The helper put the previous launcher back and started it: said once, with Try Again and a way on.
            // Nothing is checked or installed by itself in this run (MKWMP_UPDATED is set), so a release that
            // cannot be installed here never loops.
            Note($"the update was not installed: {failed} (logs/updater.log)");
            _mustUpdate = Enforces;
            SetUpdate(new UpdateView(UpdateState.Failed, null, null, 0, null, Strings.UpdateNotInstalled(failed)));
        }
        else if (!available)
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

    /// <summary>Asks GitHub for a newer release; <see cref="Update"/> says Available (with its notes), ReadyToInstall (already downloaded) or UpToDate.</summary>
    public async Task CheckForUpdatesAsync()
    {
        if (await CheckAsync(manual: true).ConfigureAwait(false))
        {
            await EnforceUpdateAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A newer release is known: required from now on, and downloaded at once. With a game running it is armed to
    /// install when the game is gone (a game is never interrupted); else the window installs it ("Install and
    /// Restart", by itself after a moment). A failed download leaves <see cref="UpdateState.Failed"/>, required.
    /// </summary>
    private async Task EnforceUpdateAsync()
    {
        if (!Enforces)
        {
            return;
        }
        _mustUpdate = true;
        SetUpdate(Update);
        if (Interlocked.CompareExchange(ref _updateWork, 1, 0) != 0)
        {
            return;
        }
        try
        {
            if (await StageAsync(_life.Token).ConfigureAwait(false) is { } staged && GameBlocksUpdate())
            {
                Note("update ready: it is installed when the game closes");
                SetReady(staged, gameBlocks: true);
                InstallUpdateAfterGame(true);
            }
        }
        finally
        {
            Volatile.Write(ref _updateWork, 0);
        }
    }

    public void ContinueWithoutUpdate()
    {
        if (Update is { State: UpdateState.Failed, Required: true })
        {
            Note("the update failed and the player goes on without it for this run");
            _waived = true;
            SetUpdate(Update);
        }
    }

    private async Task<bool> CheckAsync(bool manual)
    {
        SetUpdate(new UpdateView(UpdateState.Checking, null, null, 0, null, Strings.CheckingForUpdates));
        var current = SemVer.TryParse(Version);
        try
        {
            if (current is null)
            {
                throw new LauncherException($"{Version} is not a version");
            }
            var newest = Updater.Newest(await _updater.ReleasesAsync(_life.Token).ConfigureAwait(false), current);
            lock (_gate)
            {
                _availableRelease = newest;
            }
            if (newest is null)
            {
                _staged = null;
                SetUpdate(new UpdateView(UpdateState.UpToDate, null, null, 0, null, Strings.UpToDate));
                return false;
            }
            var version = newest.Version!.ToString();
            // Downloaded before (this run, or an earlier one): nothing to fetch again.
            if (await Task.Run(() => Staged(current, version)).ConfigureAwait(false) is { } staged)
            {
                SetReady(staged with { Notes = newest.Notes ?? staged.Notes });
                return true;
            }
            SetUpdate(new UpdateView(UpdateState.Available, version, newest.Notes, 0, null, Strings.UpdateAvailable(version)));
            return true;
        }
        catch (Exception e) when (e is LauncherException or OperationCanceledException)
        {
            Note($"update check failed: {e.Message}");
            // Offline, but a verified download waits: it can be installed all the same.
            if (current is not null && Staged(current, null) is { } staged)
            {
                SetReady(staged);
                return true;
            }
            // At start a failed check is no news (offline is normal); asked for, it is the answer.
            SetUpdate(manual
                ? new UpdateView(UpdateState.Failed, null, null, 0, null, Strings.UpdateFailed(e.Message))
                : new UpdateView(UpdateState.UpToDate, null, null, 0, null, Strings.UpToDate));
            return false;
        }
    }

    /// <summary>The verified download in the staging folder (of <paramref name="version"/>, or the newest), remembered.</summary>
    private StagedUpdate? Staged(SemVer current, string? version)
    {
        if (UpdateExe is not { } exe)
        {
            return null;
        }
        if (_staged is { } known && (version is null || known.Version == version) && Directory.Exists(UpdatePlan.TransactionFolder(known.Root, known.Version)))
        {
            return known;
        }
        return _staged = SelfUpdate.FindStaged(RootOf(exe), current, UpdateKeys, version);
    }

    public async Task DownloadUpdateAsync(CancellationToken cancel)
    {
        if (Interlocked.CompareExchange(ref _updateWork, 1, 0) != 0)
        {
            return; // a download or an install is under way
        }
        try
        {
            _ = await StageAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _updateWork, 0);
        }
    }

    public async Task ApplyUpdateAsync(CancellationToken cancel)
    {
        if (Interlocked.CompareExchange(ref _updateWork, 1, 0) != 0)
        {
            return;
        }
        try
        {
            if (await StageAsync(cancel).ConfigureAwait(false) is not { } staged)
            {
                return;
            }
            if (GameBlocksUpdate())
            {
                // Never under a running game: it would lose its launcher (the seat, the overlay) mid-race.
                Note("update ready, not installed: a game is running");
                SetReady(staged, gameBlocks: true);
                if (_mustUpdate && !_waived)
                {
                    InstallUpdateAfterGame(true); // required: it follows the game by itself
                }
                return;
            }
            await InstallAsync(staged).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _updateWork, 0);
        }
    }

    public void InstallUpdateAfterGame(bool on)
    {
        if (!on && _mustUpdate && !_waived)
        {
            return; // a required update cannot be put off
        }
        _installAfterGame = on;
        if (_staged is { } staged && Update.State == UpdateState.ReadyToInstall)
        {
            SetReady(staged, gameBlocks: GameBlocksUpdate());
        }
        if (on && Interlocked.CompareExchange(ref _afterGameLoop, 1, 0) == 0)
        {
            _ = Task.Run(AfterGameAsync, CancellationToken.None);
        }
    }

    /// <summary>Waits for the game to be gone, then installs (the player asked for exactly that).</summary>
    private async Task AfterGameAsync()
    {
        try
        {
            while (_installAfterGame && !_life.IsCancellationRequested)
            {
                if (!GameBlocksUpdate())
                {
                    _installAfterGame = false;
                    await ApplyUpdateAsync(_life.Token).ConfigureAwait(false);
                    return;
                }
                await Task.Delay(_options.Tick * 2, _life.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // the launcher is closing: the download stays, and is offered at the next start
        }
        finally
        {
            Volatile.Write(ref _afterGameLoop, 0);
        }
    }

    /// <summary>
    /// A game session of this launcher, a game it stands by for as the overlay's host, or the game
    /// running by itself: restarting the launcher now would be felt in the game.
    /// </summary>
    private bool GameBlocksUpdate()
    {
        var session = Session;
        if (session.Busy || session.GameRunning || _local.Instance.ParentPid != 0)
        {
            return true;
        }
        var s = Settings;
        var target = _targets.Get(s.Target);
        return GameFinder.Detect(target, s.Game) is { } install && target.IsRunning(install);
    }

    /// <summary>The verified download of the available release: what is staged already, else downloaded now, with progress. Null: failed or cancelled (the view says which).</summary>
    private async Task<StagedUpdate?> StageAsync(CancellationToken cancel)
    {
        Release? release;
        lock (_gate)
        {
            release = _availableRelease;
        }
        if (_staged is { } ready && (release is null || release.Version?.ToString() == ready.Version)
            && File.Exists(Path.Combine(UpdatePlan.TransactionFolder(ready.Root, ready.Version), UpdatePlan.ZipName)))
        {
            return ready;
        }
        if (release is null)
        {
            if (!await CheckAsync(manual: true).ConfigureAwait(false))
            {
                return null;
            }
            if (_staged is { } found && Update.State == UpdateState.ReadyToInstall)
            {
                return found;
            }
            lock (_gate)
            {
                release = _availableRelease;
            }
            if (release is null)
            {
                return null;
            }
        }
        var version = release.Version?.ToString() ?? release.Tag;
        var notes = release.Notes;
        try
        {
            var exe = UpdateExe ?? throw new LauncherException(Strings.CannotUpdateItself);
            Note($"downloading the launcher {Version} -> {release.Tag}");
            SetUpdate(new UpdateView(UpdateState.Downloading, version, notes, 0, null, Strings.Downloading(version)));
            using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, _life.Token);
            var meter = new DownloadMeter(_clock);
            var progress = new InlineProgress(p =>
            {
                if (meter.Add(p.Got, p.Total) is { } now)
                {
                    SetUpdate(new UpdateView(UpdateState.Downloading, version, notes, now.Done, now.Remaining, Strings.Downloading(version), p.Got, p.Total ?? 0, now.Speed));
                }
            });
            var staged = await SelfUpdate.StageAsync(_updater, release, RootOf(exe), progress, both.Token).ConfigureAwait(false);
            _staged = staged;
            Note($"launcher {staged.Version} downloaded and verified ({staged.Asset}); ready to install");
            SetReady(staged);
            return staged;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The player cancelled: the update stays on offer, and what was downloaded is continued next time.
            SetUpdate(new UpdateView(UpdateState.Available, version, notes, 0, null, Strings.UpdateAvailable(version)));
            return null;
        }
        catch (Exception e) when (e is LauncherException or OperationCanceledException or IOException)
        {
            Note($"update failed: {e.Message}");
            SetUpdate(new UpdateView(UpdateState.Failed, version, notes, 0, null, Strings.UpdateFailed(e.Message)));
            return null;
        }
    }

    /// <summary>
    /// Hands the staged release over and leaves. With a window: the helper (the new release's own)
    /// waits for this process to end, swaps the files and starts the new launcher. Headless, or
    /// when neither the release nor this install has a helper: the same swap is done here (a running
    /// executable can be renamed aside) and the new launcher is started, as before.
    /// </summary>
    private async Task InstallAsync(StagedUpdate staged)
    {
        var version = staged.Version;
        try
        {
            var exe = UpdateExe ?? throw new LauncherException(Strings.CannotUpdateItself);
            var plan = staged.Plan(Version, Path.GetFileName(exe), _options.Arguments);
            plan.Write();
            if (!_options.Headless && staged.Helper is { } helper)
            {
                Note($"installing the launcher {Version} -> {version}: handing over to {helper}");
                SetUpdate(new UpdateView(UpdateState.Restarting, version, staged.Notes, 1, TimeSpan.Zero, Strings.UpdateInstalling(version)));
                var s = Settings;
                var arguments = UpdaterArgs.For(plan, Environment.ProcessId, s.Ui.Theme == "dark", HelperLocale(plan.Root, s.Ui.Locale), quiet: false);
                if (!(_options.StartUpdateHelper ?? StartHelper)(helper, arguments))
                {
                    throw new LauncherException("the update helper could not be started");
                }
                await ExitForUpdateAsync().ConfigureAwait(false);
                return;
            }
            Note($"installing the launcher {Version} -> {version} in place");
            var installer = new UpdateInstaller(SelfUpdate.Verify, UpdateKeys, Note) { Patience = TimeSpan.FromSeconds(2) };
            var installed = await Task.Run(() => installer.Apply(plan)).ConfigureAwait(false);
            installed.Commit();
            SetUpdate(new UpdateView(UpdateState.Restarting, version, staged.Notes, 1, TimeSpan.Zero, Strings.UpdatedRestarting));
            await RestartIntoAsync(exe).ConfigureAwait(false);
        }
        catch (Exception e) when (e is LauncherException or UpdateException or IOException or UnauthorizedAccessException)
        {
            Note($"update failed: {e.Message}");
            SetUpdate(new UpdateView(UpdateState.Failed, version, staged.Notes, 0, null, Strings.UpdateFailed(e.Message)));
        }
    }

    private bool StartHelper(string helper, IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(helper) };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            Note($"starting {helper} failed: {e.Message}");
            return false;
        }
    }

    /// <summary>The helper's language: the player's choice, else the system's when the launcher has a catalog for it.</summary>
    private static string HelperLocale(string root, string saved)
    {
        if (UpdaterArgs.ValidLocale(saved))
        {
            return saved;
        }
        var culture = ReferenceEquals(Strings.Culture, CultureInfo.InvariantCulture) ? CultureInfo.CurrentUICulture : Strings.Culture;
        foreach (var name in (string[])[culture.Name, culture.TwoLetterISOLanguageName])
        {
            if (UpdaterArgs.ValidLocale(name) && File.Exists(Path.Combine(root, "i18n", name + ".json")))
            {
                return name;
            }
        }
        return culture.TwoLetterISOLanguageName == "pt" ? "pt-BR" : "en";
    }

    /// <summary>The helper waits for this process: free the seat, close everything, end.</summary>
    private async Task ExitForUpdateAsync()
    {
        if (_options.Exit is { } testExit)
        {
            await WaitIdleAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            testExit(0);
            return;
        }
        await DisposeAsync().ConfigureAwait(false);
        Environment.Exit(0);
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
            start.Environment[UpdateRunner.UpdatedVariable] = "1";
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

    /// <summary>Fraction, speed (over the last seconds) and time left of a download; an answer a few times a second at most.</summary>
    private sealed class DownloadMeter(TimeProvider clock)
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(4);
        private readonly Queue<(long At, long Got)> _samples = new();
        private long _shown;
        private bool _any;

        public (double Done, double Speed, TimeSpan? Remaining)? Add(long got, long? total)
        {
            var now = clock.GetTimestamp();
            if (total is not > 0 || (_any && clock.GetElapsedTime(_shown, now) < TimeSpan.FromMilliseconds(100) && got < total))
            {
                return null;
            }
            (_shown, _any) = (now, true);
            _samples.Enqueue((now, got));
            while (_samples.Count > 2 && clock.GetElapsedTime(_samples.Peek().At, now) > Window)
            {
                _samples.Dequeue();
            }
            var (firstAt, firstGot) = _samples.Peek();
            var seconds = clock.GetElapsedTime(firstAt, now).TotalSeconds;
            var speed = seconds > 0.2 ? Math.Max(0, got - firstGot) / seconds : 0;
            TimeSpan? remaining = speed > 0 && seconds >= 1 ? TimeSpan.FromSeconds((total.Value - got) / speed) : null;
            return (Math.Clamp((double)got / total.Value, 0, 1), speed, remaining);
        }
    }
}

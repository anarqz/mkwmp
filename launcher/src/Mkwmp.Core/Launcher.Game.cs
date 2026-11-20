// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core;

/// <summary>The game folder and MKWMP's integration in it (the probe or mod files), through the selected <see cref="IGameTarget"/>.</summary>
public sealed partial class Launcher
{
    /// <summary>The bundled integration files: <c>game/</c> next to the launcher, as in the release zip.</summary>
    public const string BundleFolder = "game";

    private readonly SemaphoreSlim _integrationWork = new(1, 1);
    private IntegrationPackage? _package;

    /// <summary>The verified integration files for this version, once known.</summary>
    private IntegrationPackage? Package
    {
        get
        {
            lock (_gate)
            {
                return _package;
            }
        }
    }

    /// <summary>Looks at the configured folder through the selected target (reads files; no lock held).</summary>
    private void InspectGame(out GameInstall? install, out TargetCheck? check, out IntegrationReport? integration)
    {
        var s = Config.Settings;
        var target = _targets.Get(s.Target);
        install = GameFinder.Detect(target, s.Game);
        (check, integration) = (null, null);
        if (install is null)
        {
            return;
        }
        try
        {
            check = target.Check(install);
            integration = target.Inspect(install, Package);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the folder went away between two looks: the next refresh says so
        }
    }

    /// <summary>Refreshes <see cref="Game"/> from the disk (the player may have updated the runtime, or enabled the mod, with the launcher open).</summary>
    private void RefreshGame()
    {
        InspectGame(out var install, out var check, out var integration);
        var setup = InspectSetup(install);
        Mutate(() =>
        {
            _install = install;
            _check = check;
            _setupStatus = setup;
            // The same verdict keeps the same object, so the window is not told every two seconds.
            if (integration is null || _integration is null || integration.State != _integration.State || integration.Summary != _integration.Summary
                || !integration.Lines.SequenceEqual(_integration.Lines))
            {
                _integration = integration;
            }
        });
        FollowGameSettings();
        PublishModRecord(); // the mod finds this launcher in its own folder (portable)
    }

    /// <summary>
    /// Looks for the game (Infinity Launcher's picker order): the saved folder, the launcher's own
    /// folder and its parent, then a shallow look under the usual places, off the calling thread.
    /// A folder found this way becomes the saved choice (it is one that detects).
    /// </summary>
    public async Task FindGameAsync()
    {
        var already = false;
        Mutate(() =>
        {
            already = _searching;
            _searching = true;
        });
        if (already)
        {
            return;
        }
        try
        {
            var s = Settings;
            var found = await Task.Run(
                () => GameFinder.Find(_targets, null, s.Game, LauncherFolder, s.Target, _options.SearchRoots, _life.Token), _life.Token).ConfigureAwait(false);
            if (found is { } hit && (!PathEquals(hit.Install.Root, s.Game) || hit.Target.Id != s.Target))
            {
                Note($"found {hit.Target.DisplayName} in {hit.Install.Root}");
                await SaveSettingsAsync(Settings with { Game = hit.Install.Root, Target = hit.Target.Id }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // the launcher is closing
        }
        finally
        {
            await Task.Run(RefreshGame).ConfigureAwait(false);
            Mutate(() => _searching = false);
        }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The player picked a folder: saved only when a target recognises it (every target is asked).</summary>
    public async Task<bool> ChooseGameFolderAsync(string folder)
    {
        var hit = await Task.Run(() => _targets.Detect(folder, Settings.Target)).ConfigureAwait(false);
        if (hit is not { } found)
        {
            var hints = string.Join(" / ", _targets.All.Select(t => t.FolderHint));
            Mutate(() => _gameNote = (Strings.NotAGameFolder(hints), true));
            return false;
        }
        Mutate(() => _gameNote = null);
        await SaveSettingsAsync(Settings with { Game = found.Install.Root, Target = found.Target.Id }).ConfigureAwait(false);
        await Task.Run(RefreshGame).ConfigureAwait(false);
        return true;
    }

    /// <summary>INSTALL: turn the integration on, save, install or repair it.</summary>
    public Task InstallIntegrationAsync() => IntegrationJobAsync(async () =>
    {
        EditConfig(c => c with { Settings = c.Settings with { Patch = true } }, wakeFriends: false);
        await SyncIntegrationAsync(force: true).ConfigureAwait(false);
        if (Game.Integration is { Ok: false } bad)
        {
            throw new LauncherException($"{bad.Summary}: {string.Join("; ", bad.Lines)}");
        }
        return Strings.PatchInstalled;
    });

    /// <summary>UNINSTALL: turn the integration off (so nothing puts it back), save, and remove every MKWMP file from the game folder.</summary>
    public Task UninstallIntegrationAsync() => IntegrationJobAsync(async () =>
    {
        if (SessionHoldsGame)
        {
            throw new LauncherException(Strings.PressStopFirst);
        }
        EditConfig(c => c with { Settings = c.Settings with { Patch = false } }, wakeFriends: false);
        var s = Settings;
        var target = _targets.Get(s.Target);
        if (GameFinder.Detect(target, s.Game) is not { } install)
        {
            throw new LauncherException(Strings.SetGameFolder(target.FolderHint));
        }
        await target.UninstallAsync(install, _life.Token).ConfigureAwait(false);
        return Strings.PatchRemoved;
    });

    private async Task IntegrationJobAsync(Func<Task<string>> job)
    {
        Mutate(() => (_gameWorking, _gameNote) = (true, null));
        (string Text, bool IsError) note;
        try
        {
            note = (await job().ConfigureAwait(false), false);
        }
        catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            note = (e.Message, true);
        }
        await Task.Run(RefreshGame).ConfigureAwait(false);
        Mutate(() => (_gameWorking, _gameNote) = (false, note));
    }

    /// <summary>
    /// Learns the files this version's integration must be and, when it is on (or
    /// <paramref name="force"/>: INSTALL), brings the game folder in line. Throws
    /// <see cref="LauncherException"/> with what went wrong; the verdict goes to the log either way.
    /// </summary>
    internal async Task SyncIntegrationAsync(bool force)
    {
        await _integrationWork.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            var s = Settings;
            var target = _targets.Get(s.Target);
            if (GameFinder.Detect(target, s.Game) is not { } install)
            {
                return;
            }
            IntegrationPackage? package = null;
            string? packageProblem = null;
            try
            {
                package = await PackageAsync(target, s).ConfigureAwait(false);
                lock (_gate)
                {
                    _package = package;
                }
            }
            catch (LauncherException e)
            {
                packageProblem = e.Message;
            }
            LauncherException? failure = null;
            try
            {
                if (s.Patch || force)
                {
                    if (!force && target.KeepsOwnIntegration)
                    {
                        Note("game patch: a developer build is kept in the game folder; nothing installed");
                    }
                    else if (!target.NeedsPackage(install))
                    {
                        await target.InstallAsync(install, package ?? IntegrationPackage.Empty(Version), _life.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await target.InstallAsync(install, package ?? throw new LauncherException(packageProblem ?? Strings.NoBundleAndUpdatesOff), _life.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (LauncherException e)
            {
                failure = e;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failure = new LauncherException(e.Message, e);
            }
            // The verdict, for bug reports.
            RefreshGame();
            if (target.Check(install) is { } check)
            {
                Note($"game: {check.Problem ?? check.Explain ?? check.Status.ToString()}");
            }
            if (Game.Integration is { } report)
            {
                Note($"game patch: {report.Summary} [{string.Join("; ", report.Lines)}]");
            }
            if (failure is not null)
            {
                throw failure;
            }
        }
        finally
        {
            _integrationWork.Release();
        }
    }

    /// <summary>
    /// The integration files for this launcher's version and the hashes they must have: from the
    /// <c>game/</c> folder next to the launcher when its signed list is for this version, else
    /// (with updates on) downloaded from this launcher's own release, checked against its signed
    /// SHA256SUMS and kept in that folder, so the next start needs no network.
    /// </summary>
    private async Task<IntegrationPackage> PackageAsync(IGameTarget target, LauncherSettings s)
    {
        var assets = target.PackageAssets;
        if (assets.Count == 0)
        {
            return IntegrationPackage.Empty(Version);
        }
        var folder = Path.Combine(LauncherFolder, BundleFolder);
        var keys = _options.ReleaseKeys ?? Updater.ReleaseKeys;
        if (Bundled(folder, Version, keys, assets) is { } bundled)
        {
            return bundled;
        }
        if (!UpdatesOn(s))
        {
            throw new LauncherException(Strings.NoBundleAndUpdatesOff);
        }
        var (release, sums) = await _updater.OwnReleaseAsync(Version, _life.Token).ConfigureAwait(false);
        var files = new List<(string Name, byte[] Bytes)>();
        foreach (var asset in assets)
        {
            if (!sums.Hashes.ContainsKey(asset.Name))
            {
                if (asset.Required)
                {
                    throw new LauncherException(Strings.ReleaseListsNo(asset.Label));
                }
                continue;
            }
            try
            {
                files.Add((asset.Name, await _updater.AssetAsync(release, sums, asset.Name, null, _life.Token).ConfigureAwait(false)));
            }
            catch (LauncherException e)
            {
                throw new LauncherException(Strings.AssetDownloadFailed(asset.Label, e.Message), e);
            }
        }
        foreach (var where in new[] { folder, Path.Combine(Path.GetTempPath(), "mkwmp-launcher", $"game-{Version}") })
        {
            try
            {
                Directory.CreateDirectory(where);
                foreach (var (name, bytes) in files)
                {
                    File.WriteAllBytes(Path.Combine(where, name), bytes);
                }
                File.WriteAllBytes(Path.Combine(where, "SHA256SUMS"), sums.Bytes);
                File.WriteAllText(Path.Combine(where, "SHA256SUMS.sig"), sums.Signature);
                if (Bundled(where, Version, keys, assets) is { } saved)
                {
                    return saved;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Note($"keeping the game patch in {where}: {e.Message}");
            }
        }
        throw new LauncherException(Strings.PatchNotSaved);
    }

    /// <summary>
    /// The integration files in <paramref name="folder"/> (<c>SHA256SUMS</c>, its <c>.sig</c> and
    /// the assets), used only when the list verifies for this launcher's version (after a
    /// self-update the zip's copy is older) and the files match it.
    /// </summary>
    internal static IntegrationPackage? Bundled(string folder, string version, IEnumerable<string> keys, IReadOnlyList<PackageAsset> assets)
    {
        try
        {
            if (SemVer.TryParse(version) is not { } v || !File.Exists(Path.Combine(folder, "SHA256SUMS")) || !File.Exists(Path.Combine(folder, "SHA256SUMS.sig")))
            {
                return null;
            }
            var sums = Updater.VerifySums(File.ReadAllBytes(Path.Combine(folder, "SHA256SUMS")), File.ReadAllText(Path.Combine(folder, "SHA256SUMS.sig")), keys, v);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var asset in assets)
            {
                if (!sums.TryGetValue(asset.Name, out var want))
                {
                    if (asset.Required)
                    {
                        return null;
                    }
                    continue;
                }
                var path = Path.Combine(folder, asset.Name);
                if (!File.Exists(path) || Updater.Sha256Hex(File.ReadAllBytes(path)) != want)
                {
                    return null;
                }
                (files[asset.Name], hashes[asset.Name]) = (path, want);
            }
            return new IntegrationPackage(version, files, hashes);
        }
        catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

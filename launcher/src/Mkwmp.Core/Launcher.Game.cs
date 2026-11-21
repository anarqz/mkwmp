// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core;

/// <summary>The game folder and MKWMP's integration in it (the probe or mod files), through the selected <see cref="IGameTarget"/>.</summary>
public sealed partial class Launcher
{
    /// <summary>The bundled integration files: <c>game/</c> next to the launcher, as in the release zip.</summary>
    public const string BundleFolder = "game";

    /// <summary>
    /// <c>MKWMP_DEV_PACKAGE=1</c> (or <c>dev_package = true</c> in client.toml): take the <c>game/</c>
    /// folder's integration files although their SHA256SUMS is not signed, when the folder says it is
    /// a development package (<see cref="DevPackageMarker"/>, written by <c>publish.ps1 -Dev</c>).
    /// Never on by itself: a release build without the switch treats such a folder as no package.
    /// </summary>
    public const string DevPackageEnv = "MKWMP_DEV_PACKAGE";

    /// <summary>The file that marks <c>game/</c> as an unsigned development package (in place of <c>SHA256SUMS.sig</c>).</summary>
    public const string DevPackageMarker = "DEV-PACKAGE";

    /// <summary>The player (a developer) turned the development package on, for this run or in client.toml.</summary>
    internal static bool DevPackageOn(LauncherSettings s) => s.DevPackage || Environment.GetEnvironmentVariable(DevPackageEnv) == "1";

    /// <summary>The last developer's mkw.exe the log named (once per build, not every refresh).</summary>
    private string? _devExeLogged;

    /// <summary>The <c>[dev] recomp_exe_sha256</c> this launcher last handed to <see cref="global::Mkwmp.Core.Targets.MkwRecomp.Recomp"/>.</summary>
    private string? _devExesApplied;

    /// <summary>A developer's test builds of mkw.exe (client.toml <c>[dev] recomp_exe_sha256</c>), taken as the pinned release from now on.</summary>
    private void FollowDevExes(LauncherSettings s)
    {
        if (Interlocked.Exchange(ref _devExesApplied, s.DevRecompExes) != s.DevRecompExes)
        {
            global::Mkwmp.Core.Targets.MkwRecomp.Recomp.SetDevExes(this, s.DevRecompExes);
        }
    }

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
        FollowDevExes(s);
        install = GameFinder.Detect(target, s.Game);
        (check, integration) = (null, null);
        if (install is null)
        {
            return;
        }
        if (install.DevSha256 is { } dev && Interlocked.Exchange(ref _devExeLogged, dev) != dev)
        {
            // Its first 16 digits: the log masks any 64-hex word as a token.
            Note($"mkw.exe {dev[..16]}: accepted as a DEVELOPMENT build ({(global::Mkwmp.Core.Targets.MkwRecomp.Recomp.DevExeSetting)}), as {install.Version}: never updated or replaced by the launcher");
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
        FollowMods();
        FollowCover(install, setup); // the Play card's picture, made once from the player's copy (Launcher.Cover.cs)
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
        return Game.Integration is { AsMod: true } ? Strings.ModInstalled : Strings.PatchInstalled;
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
                package = LocalPackage(target, s, install);
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
                        await target.InstallAsync(install, package ?? throw new LauncherException(packageProblem ?? Strings.PackageMissing(Version)), _life.Token).ConfigureAwait(false);
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
    /// The integration files for this launcher's version and the hashes they must have: the
    /// <c>game/</c> folder next to the launcher, used when its signed list is for this version. The
    /// release zip brings that folder with the launcher (and every update installs the whole zip), so
    /// nothing is downloaded here: a folder that is missing or another version's is reported.
    /// </summary>
    private IntegrationPackage LocalPackage(IGameTarget target, LauncherSettings s, GameInstall install)
    {
        var assets = target.PackageAssetsFor(install);
        if (assets.Count == 0)
        {
            return IntegrationPackage.Empty(Version);
        }
        var folder = Path.Combine(LauncherFolder, BundleFolder);
        var keys = _options.ReleaseKeys ?? Updater.ReleaseKeys;
        var dev = DevPackageOn(s);
        if (Bundled(folder, Version, keys, assets, dev) is { } bundled)
        {
            if (bundled.Development && !_devPackageNoted)
            {
                _devPackageNoted = true;
                Note($"game patch: DEVELOPMENT PACKAGE (unsigned) from {folder}, because {DevPackageEnv}=1 or dev_package = true");
            }
            return bundled;
        }
        // A development package that is not turned on is no package: said, so a developer is not left guessing.
        var unused = !dev && File.Exists(Path.Combine(folder, DevPackageMarker)) && !File.Exists(Path.Combine(folder, "SHA256SUMS.sig"));
        throw new LauncherException(unused ? Strings.DevPackageOff(DevPackageEnv, Strings.PackageMissing(Version)) : Strings.PackageMissing(Version));
    }

    private bool _devPackageNoted;

    /// <summary>
    /// The integration files in <paramref name="folder"/> (<c>SHA256SUMS</c>, its <c>.sig</c> and
    /// the assets), used only when the list verifies for this launcher's version (a copy of another
    /// release's folder is not this launcher's) and the files match it. A zip asset may be there
    /// unpacked instead (<see cref="PackageAsset.Unpacked"/>): every file of that folder in the list.
    /// <para>
    /// <paramref name="allowUnsigned"/> (<see cref="DevPackageOn"/>): a folder with no
    /// <c>SHA256SUMS.sig</c> and a <see cref="DevPackageMarker"/> file is taken on its unsigned list
    /// (a local build cannot be signed), marked <see cref="IntegrationPackage.Development"/>. A folder
    /// that has a signature is always held to it.
    /// </para>
    /// </summary>
    internal static IntegrationPackage? Bundled(string folder, string version, IEnumerable<string> keys, IReadOnlyList<PackageAsset> assets, bool allowUnsigned = false)
    {
        try
        {
            if (SemVer.TryParse(version) is not { } v || !File.Exists(Path.Combine(folder, "SHA256SUMS")))
            {
                return null;
            }
            var signed = File.Exists(Path.Combine(folder, "SHA256SUMS.sig"));
            if (!signed && !(allowUnsigned && File.Exists(Path.Combine(folder, DevPackageMarker))))
            {
                return null;
            }
            var list = File.ReadAllBytes(Path.Combine(folder, "SHA256SUMS"));
            var sums = signed ? Updater.VerifySums(list, File.ReadAllText(Path.Combine(folder, "SHA256SUMS.sig")), keys, v) : Updater.ParseSums(list, v);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            bool Take(string name, string want)
            {
                var path = Path.GetFullPath(Path.Combine(folder, name));
                if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path) || Updater.Sha256Hex(File.ReadAllBytes(path)) != want)
                {
                    return false;
                }
                (files[name], hashes[name]) = (path, want);
                return true;
            }

            foreach (var asset in assets)
            {
                if (sums.TryGetValue(asset.Name, out var want))
                {
                    if (!Take(asset.Name, want))
                    {
                        return null;
                    }
                    continue;
                }
                var unpacked = asset.Unpacked is { } prefix ? sums.Where(p => p.Key.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList() : [];
                if (unpacked.Count > 0)
                {
                    if (!unpacked.All(p => Take(p.Key, p.Value)))
                    {
                        return null;
                    }
                }
                else if (asset.Required)
                {
                    return null;
                }
            }
            return new IntegrationPackage(version, files, hashes) { Development = !signed };
        }
        catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Modding;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core;

/// <summary>
/// The game's mods (launcher/docs/MODS.md): whatever the selected <see cref="IGameTarget"/> lists, turned
/// on and off, installed and removed through it; downloads (a link, GameBanana) land in <c>downloads\</c>
/// beside the launcher and go through the same install. Nothing of a game is written while it runs.
/// </summary>
public sealed partial class Launcher
{
    /// <summary>Beside the exe: the launcher is portable.</summary>
    public const string DownloadsFolderName = "downloads";

    /// <summary>One change to the mods at a time.</summary>
    private readonly SemaphoreSlim _modsWork = new(1, 1);
    private volatile ModsView? _modsView;
    private GameBanana? _gameBanana;
    private IReadOnlyDictionary<string, GbFile> _modUpdates = new Dictionary<string, GbFile>();
    private (string Mod, string Note)? _modOptionsError;

    public ModsView Mods => _modsView ?? ModsView.None;

    public GameBanana GameBanana => _gameBanana ??= new GameBanana(_http, Path.Combine(LauncherFolder, CacheFolderName, "gamebanana"), _userAgent + " (+https://github.com/anarqz/mkwmp)", _clock);

    public Task<GbPage> BrowseModsAsync(GbSort sort, string query, int page, CancellationToken cancel) =>
        string.IsNullOrWhiteSpace(query) ? GameBanana.BrowseAsync(sort, page, cancel) : GameBanana.SearchAsync(query, page, cancel);

    public Task<GbDetails> ModPageAsync(int id, CancellationToken cancel) => GameBanana.DetailsAsync(id, cancel);

    public Task<string?> ModImageAsync(string url, CancellationToken cancel) => GameBanana.ImageAsync(url, cancel);

    private string DownloadsFolder => Path.Combine(LauncherFolder, DownloadsFolderName);

    private static string? ModLocale => Strings.Culture.Name is { Length: > 0 } name ? name : null;

    private (IGameTarget Target, GameInstall? Install) ModTarget()
    {
        var s = Settings;
        var target = _targets.Get(s.Target);
        return (target, GameFinder.Detect(target, s.Game));
    }

    private ModsView BuildMods()
    {
        var (target, install) = ModTarget();
        if (install is null)
        {
            return ModsView.None with { Loaded = true, Note = Strings.SetGameFolder(target.FolderHint) };
        }
        if (!target.SupportsMods(install))
        {
            return ModsView.None with { Loaded = true, Note = Strings.ModsNone(install.Version is { } v ? $"{target.DisplayName} {v}" : target.DisplayName) };
        }
        var running = target.IsRunning(install);
        var mature = install.TargetId == MkwRecompTarget.TargetId
            && string.Equals(RecompIni.Values(Mod.ReadIni(install.Root) ?? "").GetValueOrDefault("GB_MATURE")?.Trim().Trim('"'), "show", StringComparison.OrdinalIgnoreCase);
        var mods = target.Mods(install, ModLocale);
        IReadOnlyDictionary<string, GbFile> updates;
        lock (_gate)
        {
            updates = _modUpdates = _modUpdates.Where(u => mods.Any(m => m.Id == u.Key && m.Source is { } s && s.GameBananaFile != u.Value.Id)).ToDictionary(u => u.Key, u => u.Value);
        }
        return new ModsView(true, true, mods, !running, running ? Strings.ModsCloseGame : null, mature, updates) { TurnedOff = target.ModsTurnedOff(install) };
    }

    public Task RefreshModsAsync() => Task.Run(() =>
    {
        _modsView = BuildMods();
        RaiseChanged();
    });

    /// <summary>For the two-second look at the game: a game that starts or ends locks or frees the page.</summary>
    private void FollowMods()
    {
        if (_modsView is not { Supported: true } view)
        {
            return;
        }
        var (target, install) = ModTarget();
        var running = install is not null && target.IsRunning(install);
        if (view.Editable == running)
        {
            _modsView = view with { Editable = !running, Note = running ? Strings.ModsCloseGame : null };
            RaiseChanged();
        }
    }

    private async Task<string?> ChangeModsAsync(Action<IGameTarget, GameInstall> change)
    {
        await _modsWork.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                string? error = null;
                try
                {
                    var (target, install) = ModTarget();
                    if (install is null || !target.SupportsMods(install))
                    {
                        throw new LauncherException(install is null ? Strings.SetGameFolder(target.FolderHint) : Strings.ModsNone(target.DisplayName));
                    }
                    change(target, install);
                }
                catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
                {
                    error = e.Message;
                    Note($"mods: {e.Message}");
                }
                _modsView = BuildMods();
                RaiseChanged();
                return error;
            }).ConfigureAwait(false);
        }
        finally
        {
            _modsWork.Release();
        }
    }

    public Task<string?> SetModEnabledAsync(string id, bool on, bool acceptCode)
    {
        ArgumentNullException.ThrowIfNull(id);
        return ChangeModsAsync((target, install) =>
        {
            target.SetModEnabled(install, id, on, acceptCode);
            Note($"mods: {id} {(on ? "on" : "off")}");
        });
    }

    public async Task<(int Changed, string? Error)> SetAllModsEnabledAsync(bool on)
    {
        var changed = 0;
        var error = await ChangeModsAsync((target, install) =>
        {
            changed = target.SetAllModsEnabled(install, on);
            Note($"mods: all {(on ? "back on" : "off")}: {changed} changed");
        }).ConfigureAwait(false);
        return (changed, error);
    }

    public Task<string?> UninstallModAsync(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return ChangeModsAsync((target, install) =>
        {
            target.UninstallMod(install, id);
            Note($"mods: {id} removed");
        });
    }

    public async Task<ModInstallResult> InstallModAsync(ModInstallRequest request, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _modsWork.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ModInstallResult result;
                try
                {
                    var (target, install) = ModTarget();
                    result = install is null ? new(ModInstallCode.NoGame, Strings.SetGameFolder(target.FolderHint))
                        : !target.SupportsMods(install) ? new(ModInstallCode.NoGame, Strings.ModsNone(target.DisplayName))
                        : target.InstallMod(install, request, progress, cancel);
                }
                catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
                {
                    result = new(ModInstallCode.Io, Strings.ModsFailed(e.Message));
                }
                Note($"mods: install of {Path.GetFileName(request.Path)}: {result.Code}{(result.ModId is { } id ? " (" + id + ")" : "")}");
                if (result.Code is not (ModInstallCode.Variants or ModInstallCode.Exists))
                {
                    ForgetDownload(request.Path); // a question keeps the archive for its answer
                }
                _modsView = BuildMods();
                RaiseChanged();
                return result;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _modsWork.Release();
        }
    }

    /// <summary>Deletes an archive of the launcher's own downloads folder (and nothing anywhere else).</summary>
    private void ForgetDownload(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full) && string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(DownloadsFolder), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(full);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // it stays in downloads\: written over by the next download of that name
        }
    }

    public Task<string> DownloadModAsync(string link, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        var uri = ModDownloader.Link(link) ?? throw new LauncherException(Strings.ModsBadLink);
        Note($"mods: download from {uri.Host}");
        return ModDownloader.DownloadAsync(_http, uri, DownloadsFolder, _userAgent, progress, cancel);
    }

    public Task<string> DownloadModAsync(GbDetails mod, GbFile file, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(file);
        Note($"mods: download of GameBanana mod {mod.Mod.Id}, file {file.Id}");
        return GameBanana.DownloadAsync(mod, file, DownloadsFolder, progress, cancel);
    }

    public Task<string?> SetShowMatureModsAsync(bool show) => ChangeModsAsync((target, install) =>
    {
        if (install.TargetId != MkwRecompTarget.TargetId)
        {
            return;
        }
        if (target.IsRunning(install))
        {
            throw new LauncherException(Strings.ModsCloseGame);
        }
        // the game's own setting of its GameBanana tab (cfg.c: gb_mature = obfuscate | show, under [launcher]): one choice for both
        Mod.EditIni(install.Root, ini => RecompIni.Apply(ini, [new("gb_mature", show ? "show" : null)], _ => "launcher"));
    });

    public async Task CheckModUpdatesAsync(CancellationToken cancel)
    {
        var updates = new Dictionary<string, GbFile>();
        foreach (var mod in Mods.Mods.Where(m => m.Source is { GameBananaMod: > 0 }).Take(24))
        {
            try
            {
                var details = await GameBanana.DetailsAsync(mod.Source!.GameBananaMod, cancel).ConfigureAwait(false);
                if (NewerFile(mod.Source, details) is { } newer)
                {
                    updates[mod.Id] = newer;
                }
            }
            catch (LauncherException)
            {
                // GameBanana did not answer for this one: no update shown
            }
        }
        lock (_gate)
        {
            _modUpdates = updates;
        }
        if (_modsView is { } view)
        {
            _modsView = view with { Updates = updates };
        }
        RaiseChanged();
    }

    /// <summary>The mod's newest clean file, when GameBanana got it after the one installed.</summary>
    internal static GbFile? NewerFile(ModSource source, GbDetails details)
    {
        var newest = details.Files.Where(f => f.Clean).OrderByDescending(f => f.Added).FirstOrDefault();
        return newest is not null && newest.Id != source.GameBananaFile && newest.Added > source.FileAdded ? newest : null;
    }

    public GameSettingsView ModOptions(string id)
    {
        var view = Mods;
        var none = new GameSettingsView(null, NoValues, NoIds, false, view.Note, false);
        if (view.Find(id) is not { Options: { } schema } mod)
        {
            return none;
        }
        var (target, install) = ModTarget();
        if (install is null)
        {
            return none;
        }
        var set = target.ReadModOptions(install, mod);
        string? error;
        lock (_gate)
        {
            error = _modOptionsError is { } e && e.Mod == mod.Id ? e.Note : null;
        }
        return new GameSettingsView(schema, schema.Effective(set), new HashSet<string>(set.Keys, StringComparer.OrdinalIgnoreCase), view.Editable, error ?? view.Note, error is not null);
    }

    public Task SetModOptionAsync(string id, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(key);
        return Task.Run(() =>
        {
            string? error = null;
            try
            {
                var (target, install) = ModTarget();
                if (install is null || Mods.Find(id) is not { } mod)
                {
                    throw new LauncherException(Strings.ModsNotAMod(id));
                }
                target.WriteModOptions(install, mod, new Dictionary<string, string?> { [key] = value });
            }
            catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
            {
                error = e.Message;
                Note($"mods: {id}.{key}: {e.Message}");
            }
            lock (_gate)
            {
                _modOptionsError = error is null ? null : (id, error);
            }
            RaiseChanged(); // always: a refused change puts the row back to the value in effect
        });
    }
}

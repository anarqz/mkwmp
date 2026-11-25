// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.Concurrent;
using System.IO.Compression;
using Mkwmp.Core.Net;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>One file of the mod: its path under <c>mods/mkwmp/</c> with forward slashes (<c>plugin/mkwmp.dll</c>).</summary>
internal sealed record ModFile(string Path, byte[] Bytes, string Sha256);

/// <summary>
/// The MKWMP mod in an mkw-recomp that loads mods (<see cref="Recomp.ModRuntimes"/>): there the mod
/// is the integration. The launcher puts it into <c>&lt;data&gt;/mods/mkwmp/</c> from its package
/// (the release asset <c>mkwmp-mod.zip</c>, as native/probe/build_plugin.py --package writes it:
/// <c>mkwmp/mod.json</c>, <c>mkwmp/plugin/mkwmp.dll</c>…; or the same files unpacked, each listed
/// as <c>mods/mkwmp/…</c> in SHA256SUMS), and turns it on the way mkw-recomp's Mods page does
/// (runtime/ui/launcher/mods.cpp, set_enabled): <c>mods = a,b,mkwmp</c> under <c>[launcher]</c> of
/// the installation's mkw.ini, appended, the other mods kept.
/// <para>
/// Left alone on purpose: <c>mods_played</c> (the game saves a copy of the save before the first
/// start with a new set of mods, and writes it then), <c>mods_ack</c> (its own Mods page's
/// "I understand" box), and the player's settings of the mod
/// (<c>profile/mod-settings/mkwmp.ini</c>, the host's: see <see cref="ModSettings"/>), also on uninstall.
/// </para>
/// </summary>
internal static class Mod
{
    /// <summary>The release asset, and its name in SHA256SUMS.</summary>
    public const string Zip = "mkwmp-mod.zip";

    /// <summary>The same files unpacked in the launcher's <c>game/</c> folder, and their prefix in its SHA256SUMS.</summary>
    public const string Unpacked = "mods/mkwmp";

    private const string Manifest = "mod.json";
    private const string Staging = ".mkwmp-new";
    private const string Backups = ".mkwmp-backup";
    private const long MaxFile = 256L * 1024 * 1024;

    private static readonly string[] Libraries = ["plugin/mkwmp.dll", "plugin/mkwmp.so", "plugin/libmkwmp.so"];
    private static readonly Recomp.FileCache<string?> Hashes = new();
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<(string Path, string Sha256)> Files, string? Version)?> Lists = new(StringComparer.Ordinal);

    /// <summary>A runtime that loads mods: the integration there is the mod.</summary>
    public static bool Capable(string game) =>
        Recomp.Identify(game) is { Kind: RecompKind.Supported, Version: { } v } && Recomp.LoadsMods(v);

    public static string Dir(string game) => Path.Combine(Recomp.DataDir(game), "mods", Recomp.Mod);

    internal static string IniPath(string game) => Path.Combine(Recomp.DataDir(game), "mkw.ini");

    /// <summary>Does the package carry the mod (zipped or unpacked)?</summary>
    public static bool In(IntegrationPackage? package) =>
        package is not null && (package.Files.ContainsKey(Zip) || package.Files.Keys.Any(k => k.StartsWith(Unpacked + "/", StringComparison.Ordinal)));

    /// <summary>Anything of the mod in the game: a file in its folder.</summary>
    public static bool Present(string game)
    {
        try
        {
            var dir = Dir(game);
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? HashOf(string path) => Hashes.TryGet(path, out var hash, p =>
    {
        try
        {
            return Updater.Sha256Hex(File.ReadAllBytes(p));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }) ? hash : null;

    /// <summary>A path inside the mod's folder, or null: nothing absolute, no <c>..</c>, no drive, no stream.</summary>
    private static string? Relative(string name)
    {
        var parts = name.Replace('\\', '/').Split('/');
        return parts.Length is > 0 and <= 8 && parts.All(p => p.Length is > 0 and <= 96 && p != "." && p != ".." && p.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) < 0 && !p.EndsWith('.') && !p.EndsWith(' '))
            ? string.Join('/', parts)
            : null;
    }

    /// <summary>
    /// The package's files of the mod, each checked once more against the verified list (the copy on
    /// disk may have changed since). Null: the package has no mod. Throws <see cref="LauncherException"/>
    /// for a file that no longer matches, or a zip that is not the mod.
    /// </summary>
    public static IReadOnlyList<ModFile>? Load(IntegrationPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var files = new List<ModFile>();
        try
        {
            if (package.Files.TryGetValue(Zip, out var zipPath) && package.Sha256.TryGetValue(Zip, out var want))
            {
                var bytes = File.ReadAllBytes(zipPath);
                if (Updater.Sha256Hex(bytes) != want)
                {
                    throw new LauncherException(Strings.RecompChecksum(Zip));
                }
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        continue; // a folder
                    }
                    var name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith(Recomp.Mod + "/", StringComparison.Ordinal) || Relative(name[(Recomp.Mod.Length + 1)..]) is not { } rel || entry.Length > MaxFile)
                    {
                        throw new LauncherException(Strings.RecompNotTheMod(Zip));
                    }
                    using var data = new MemoryStream((int)entry.Length);
                    using (var from = entry.Open())
                    {
                        from.CopyTo(data);
                    }
                    var content = data.ToArray();
                    files.Add(new ModFile(rel, content, Updater.Sha256Hex(content)));
                }
            }
            else
            {
                foreach (var (name, path) in package.Files.Where(f => f.Key.StartsWith(Unpacked + "/", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    if (Relative(name[(Unpacked.Length + 1)..]) is not { } rel || !package.Sha256.TryGetValue(name, out var hash))
                    {
                        throw new LauncherException(Strings.RecompNotTheMod(Unpacked));
                    }
                    var content = File.ReadAllBytes(path);
                    if (Updater.Sha256Hex(content) != hash)
                    {
                        throw new LauncherException(Strings.RecompChecksum(name));
                    }
                    files.Add(new ModFile(rel, content, hash));
                }
                if (files.Count == 0)
                {
                    return null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new LauncherException(Strings.RecompInstallFailed(Strings.RecompAssetMod, e.Message), e);
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!files.All(f => names.Add(f.Path)) || !names.Contains(Manifest) || !Libraries.Any(names.Contains))
        {
            throw new LauncherException(Strings.RecompNotTheMod(package.Files.ContainsKey(Zip) ? Zip : Unpacked));
        }
        return files;
    }

    /// <summary>What the package says each file of the mod must be, and the version in its mod.json; null when it has no mod, or it cannot be read now. Never throws; asked every two seconds, so the package is opened once.</summary>
    public static (IReadOnlyList<(string Path, string Sha256)> Files, string? Version)? Expected(IntegrationPackage? package)
    {
        if (!In(package))
        {
            return null;
        }
        var key = package!.Sha256.GetValueOrDefault(Zip)
            ?? string.Join('|', package.Sha256.Where(p => p.Key.StartsWith(Unpacked + "/", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
        return Lists.GetOrAdd(key, _ =>
        {
            try
            {
                return Load(package) is { } files
                    ? ([.. files.Select(f => (f.Path, f.Sha256))], files.FirstOrDefault(f => f.Path == Manifest) is { } manifest ? VersionIn(manifest.Bytes) : null)
                    : null;
            }
            catch (LauncherException)
            {
                return null;
            }
        });
    }

    /// <summary>The <c>version</c> of a mod.json; null when there is none.</summary>
    internal static string? VersionIn(ReadOnlySpan<byte> manifest) => Json.Parse(manifest).At("version").Str();

    private static string? InstalledVersion(string game)
    {
        try
        {
            var path = Path.Combine(Dir(game), Manifest);
            return File.Exists(path) && new FileInfo(path).Length < 65536 ? VersionIn(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- mkw.ini: [launcher] mods ---------------------------------------------------------

    internal static string? ReadIni(string game)
    {
        try
        {
            var path = IniPath(game);
            return File.Exists(path) ? RecompIni.Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The mods the installation's mkw.ini turns on, in its order (the order is the game's precedence between mods).</summary>
    internal static List<string> Listed(string? ini)
    {
        var value = ini is null ? null : Recomp.ModsIn(ini) ?? RecompIni.Values(ini).GetValueOrDefault("MODS");
        var list = value?.Trim() ?? "";
        if (list.Length >= 2 && list[0] == '"' && list[^1] == '"')
        {
            list = list[1..^1];
        }
        return [.. list.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0)];
    }

    /// <summary>mkw.ini with MKWMP's mod turned on or off, every other mod and every other line kept; the same text when there is nothing to change.</summary>
    internal static string WithMod(string ini, bool on) => WithMod(ini, Recomp.Mod, on);

    /// <summary>The same for any mod of <c>mods/</c>, by its folder name (launcher/docs/MODS.md): the list edit mkw-recomp's own Mods page makes (set_enabled).</summary>
    internal static string WithMod(string ini, string id, bool on)
    {
        var mods = Listed(ini);
        var has = mods.Any(m => m.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (has == on && (!on || Recomp.ModsIn(ini) is { } seen && Recomp.ModsListHas(seen, id)))
        {
            return ini;
        }
        mods.RemoveAll(m => m.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (on)
        {
            mods.Add(id); // enabling appends, as the game's Mods page does: the order is the precedence, the newest wins
        }
        var value = mods.Count == 0 ? null : string.Join(',', mods);
        static string? Section(string key) => "launcher";
        var after = RecompIni.Apply(ini, [new("mods", value)], Section);
        if (on && !(Recomp.ModsIn(after) is { } now && Recomp.ModsListHas(now, id)))
        {
            // the key's line sat outside [launcher], where the mod itself does not look: it moves there
            after = RecompIni.Apply(RecompIni.Apply(ini, [new("mods", null)], Section), [new("mods", value)], Section);
        }
        return after;
    }

    /// <summary>True when the file was changed. Throws <see cref="LauncherException"/> when it cannot be written.</summary>
    private static bool SetEnabled(string game, bool on) => SetEnabled(game, Recomp.Mod, on);

    /// <summary>The same for any mod. True when the file was changed.</summary>
    internal static bool SetEnabled(string game, string id, bool on) => EditIni(game, ini => WithMod(ini, id, on));

    /// <summary>Rewrites the installation's mkw.ini (atomically) with <paramref name="edit"/>'s text. True when the file was changed.</summary>
    internal static bool EditIni(string game, Func<string, string> edit)
    {
        var path = IniPath(game);
        try
        {
            var exists = File.Exists(path);
            var before = exists ? RecompIni.Decode(File.ReadAllBytes(path)) : "";
            var after = edit(before);
            if (after == before)
            {
                return false;
            }
            if (exists && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            {
                throw new LauncherException(Strings.GameSettingsReadOnly("mkw.ini"));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".mkwmp-tmp";
            File.WriteAllBytes(temporary, RecompIni.Encode(after));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(Strings.GameSettingsSaveFailed(e.Message), e);
        }
    }

    // ---- the state ------------------------------------------------------------------------

    /// <summary>The mod's state in the game, against the package (null: nothing to compare with).</summary>
    public static IntegrationReport Check(string game, IntegrationPackage? package, string launcherVersion)
    {
        IntegrationReport Report(IntegrationState state, string summary, List<string> lines) => new(state, summary, lines) { AsMod = true };

        var dir = Dir(game);
        if (!Present(game))
        {
            return Report(IntegrationState.Missing, Strings.PatchState("missing"), [$"mods/{Recomp.Mod}: not installed ({dir})"]);
        }
        var version = InstalledVersion(game);
        var lines = new List<string> { $"mods/{Recomp.Mod}: {(version is null ? "no version" : "v" + version)} ({dir})" };
        var env = Environment.GetEnvironmentVariable("MKW_MODS");
        var enabled = env is not null ? Recomp.ModsListHas(env, Recomp.Mod) : Listed(ReadIni(game)).Any(m => m.Equals(Recomp.Mod, StringComparison.OrdinalIgnoreCase));
        lines.Add(env is not null ? $"MKW_MODS: {(enabled ? "enabled" : "not enabled")}" : $"mkw.ini [launcher] mods: {(enabled ? "enabled" : "not enabled")}");
        var hasLibrary = Libraries.Any(l => File.Exists(Path.Combine(dir, l)));
        var (missing, changed) = (!hasLibrary, false);
        if (!hasLibrary)
        {
            lines.Add("plugin/mkwmp.dll: missing");
        }
        var expected = Expected(package)?.Files;
        if (expected is null)
        {
            lines.Add("files: not verified (no package of this launcher's version)");
        }
        else
        {
            var verified = 0;
            foreach (var (path, want) in expected)
            {
                var have = HashOf(Path.Combine(dir, path));
                if (have == want)
                {
                    verified++;
                }
                else if (have is null)
                {
                    lines.Add($"{path}: missing");
                    missing = true;
                }
                else
                {
                    lines.Add($"{path}: another version, or changed");
                    changed = true;
                }
            }
            lines.Add($"files: {verified} of {expected.Count} verified against v{package!.Version}");
            if (package.Development)
            {
                lines.Add($"development package (unsigned): used because {Launcher.DevPackageEnv}=1 or dev_package = true");
            }
        }
        var packaged = Expected(package)?.Version;
        return missing ? Report(IntegrationState.Damaged, Strings.PatchState("damaged"), lines)
            : changed ? (packaged is not null && version == packaged
                ? Report(IntegrationState.Damaged, Strings.PatchState("damaged"), lines)
                : Report(IntegrationState.Outdated, Strings.PatchState("outdated"), lines))
            : !enabled ? Report(IntegrationState.Disabled, Strings.PatchState("mod_disabled"), lines)
            : expected is null ? Report(IntegrationState.Mod, Strings.PatchState("mod"), lines)
            : package!.Development ? Report(IntegrationState.Installed, Strings.PatchState("mod_dev", version ?? launcherVersion), lines)
            : Report(IntegrationState.Installed, Strings.PatchState("mod_installed", version ?? launcherVersion), lines);
    }

    // ---- install, uninstall ---------------------------------------------------------------

    /// <summary>
    /// Brings <c>mods/mkwmp</c> in line with the package and turns the mod on. Only files that differ
    /// are written: all of them are staged first, each one replaced keeps its old copy until the last
    /// is in place, and a failure puts every old one back. Files the package does not have (what the
    /// mod keeps in its folder) stay. Nothing is written while the game runs. What it did, a line each.
    /// </summary>
    public static List<string> Install(string game, IntegrationPackage package, bool running)
    {
        var files = Load(package) ?? throw new LauncherException(Strings.ReleaseListsNo(Strings.RecompAssetMod));
        var dir = Dir(game);
        var done = new List<string>();
        var stale = files.Where(f => HashOf(Path.Combine(dir, f.Path)) != f.Sha256).ToList();
        var enable = WithMod(ReadIni(game) ?? "", on: true) != (ReadIni(game) ?? "");
        if ((stale.Count > 0 || enable) && running)
        {
            throw new LauncherException(Strings.RecompCloseForPatch);
        }
        if (stale.Count > 0)
        {
            try
            {
                Replace(dir, stale);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(Strings.RecompInstallFailed(Strings.RecompAssetMod, e.Message), e);
            }
            var version = files.FirstOrDefault(f => f.Path == Manifest) is { } manifest ? VersionIn(manifest.Bytes) : null;
            done.Add($"installed the MKWMP mod {(version is null ? "" : "v" + version + " ")}in {dir} ({stale.Count} of {files.Count} files written{(package.Development ? "; development package, unsigned" : "")})");
        }
        if (SetEnabled(game, on: true))
        {
            done.Add($"turned the mod on in {IniPath(game)} ([launcher] mods)");
        }
        if (Environment.GetEnvironmentVariable("MKW_MODS") is { } env && !Recomp.ModsListHas(env, Recomp.Mod))
        {
            done.Add("MKW_MODS is set without mkwmp: the game follows it, not mkw.ini");
        }
        return done;
    }

    private static void Replace(string dir, List<ModFile> stale)
    {
        var staging = Path.Combine(dir, Staging);
        var backups = Path.Combine(dir, Backups);
        Clear(staging);
        Clear(backups); // of a run that was cut short: what is in place is compared again, file by file
        var placed = new List<(string Path, string? Backup)>();
        try
        {
            foreach (var f in stale)
            {
                var staged = Path.Combine(staging, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.WriteAllBytes(staged, f.Bytes);
            }
            foreach (var f in stale)
            {
                var path = Path.Combine(dir, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string? backup = null;
                if (File.Exists(path))
                {
                    backup = Path.Combine(backups, f.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(path, backup);
                }
                placed.Add((path, backup));
                File.Move(Path.Combine(staging, f.Path), path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var (path, backup) in Enumerable.Reverse(placed))
            {
                try
                {
                    File.Delete(path);
                    if (backup is not null && File.Exists(backup))
                    {
                        File.Move(backup, path);
                    }
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    // what could be put back is; the report says which files differ
                }
            }
            Clear(staging);
            Clear(backups);
            throw;
        }
        Clear(staging);
        Clear(backups);
    }

    private static void Clear(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left behind: no file of the mod is read from it
        }
    }

    /// <summary>Takes the mod out: its folder, and its name from mkw.ini's list (the other mods stay). The player's settings of the mod stay. Nothing to do: an empty list.</summary>
    public static List<string> Uninstall(string game, bool running)
    {
        var dir = Dir(game);
        var done = new List<string>();
        var listed = Listed(ReadIni(game)).Any(m => m.Equals(Recomp.Mod, StringComparison.OrdinalIgnoreCase));
        if (!Directory.Exists(dir) && !listed)
        {
            return done;
        }
        if (running)
        {
            throw new LauncherException(Strings.RecompCloseGameFirst);
        }
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                done.Add($"removed {dir}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"remove {dir}: {e.Message}", e);
        }
        if (SetEnabled(game, on: false))
        {
            done.Add("took mkwmp out of mkw.ini's mods");
        }
        return done;
    }
}

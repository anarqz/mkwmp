// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// The mods of an mkw-recomp install, the way its own start screen keeps them (launcher/docs/MODS.md;
/// runtime/ui/launcher/mods.cpp there): a mod is a folder of <c>&lt;data&gt;/mods/</c> with a <c>mod.json</c>,
/// it is on when <c>[launcher] mods = a,b</c> of mkw.ini lists it (the order is the precedence: the last
/// one wins shared files), and the game reads both when it starts. This class lists them, turns them on
/// and off, removes them and reads and writes their settings (<see cref="ModSettings"/>);
/// <see cref="ModImport"/> installs them.
/// <para>
/// What the game's own Mods page does and this does not: build the game's code again for a mod that
/// patches it (such a mod is listed and left alone), and choose between mods that change the same file
/// inside one game archive (its merge runs when the install has its tools, else the last mod wins).
/// </para>
/// </summary>
internal static class RecompMods
{
    /// <summary>mods.cpp's MODS_MAX, and MOD_NAME_MAX with its terminator.</summary>
    public const int MaxMods = 64;
    public const int MaxName = 63;

    public const string ManifestFile = "mod.json";
    public const string SourceFile = ".mkwmp.json";
    public const string Backups = ".mkwmp-backup";
    public const string Staging = ".mkwmp-staging";
    private const string Merged = ".merged";
    private const int KeepSaveCopies = 10;

    public static bool Capable(GameInstall install) => install.TargetId == MkwRecompTarget.TargetId && Mod.Capable(install.Root);

    public static string ModsDir(GameInstall install) => Path.Combine(Recomp.DataDir(install.Root), "mods");

    /// <summary>A mod's folder name: what <c>mods =</c> can list and the game's own page shows.</summary>
    public static bool ValidId(string? id) =>
        id is { Length: > 0 } && Encoding.UTF8.GetByteCount(id) <= MaxName && id[0] != '.' && id == id.Trim()
        && id.IndexOfAny(['/', '\\', ':', ',', '"', '=', '\0', '*', '?', '<', '>', '|']) < 0 && !id.Any(char.IsControl) && !id.EndsWith('.');

    private static bool IsManaged(string id) => id.Equals(Recomp.Mod, StringComparison.OrdinalIgnoreCase);

    // ---- the list ---------------------------------------------------------------------------

    /// <summary>What a mod.json says, read loosely: a missing or broken one is a mod named after its folder.</summary>
    internal sealed record Manifest(string Title, string Version, string Author, string Description, bool HasRomfs, int RomfsFiles, bool HasExefs, bool HasPlugin, bool Code, IReadOnlyList<string> Warnings)
    {
        public static Manifest Read(string folder, string fallback)
        {
            var (title, version, author, description) = (fallback, "", "", "");
            var (romfs, files, exefs, plugin, code) = (false, 0, false, false, false);
            var warnings = new List<string>();
            try
            {
                var path = Path.Combine(folder, ManifestFile);
                if (File.Exists(path) && new FileInfo(path).Length <= 4 << 20)
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        string Text(string name, string or, int most) =>
                            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
                                ? new string([.. s.Where(c => !char.IsControl(c) || c == '\n')]).Trim() is { Length: > 0 } clean ? (clean.Length > most ? clean[..most] : clean) : or
                                : or;
                        bool Flag(string name) => root.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n != 0));
                        bool Filled(string name) => root.TryGetProperty(name, out var v) && v.ValueKind switch
                        {
                            JsonValueKind.Object => v.EnumerateObject().Any(),
                            JsonValueKind.Array => v.GetArrayLength() > 0,
                            _ => false,
                        };
                        title = Text("title", fallback, 96);
                        version = Text("version", "", 32);
                        author = Text("author", "", 64);
                        description = Text("description", "", 2000);
                        romfs = Flag("has_romfs");
                        exefs = Flag("has_exefs");
                        plugin = Flag("has_plugin");
                        files = root.TryGetProperty("romfs_files", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) ? Math.Max(0, n) : 0;
                        // what the game's page calls a code mod ("image", "patches"), and a port's switches: all need the game's own page
                        code = Filled("image") || Filled("patches") || Filled("switches");
                        if (root.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array)
                        {
                            warnings.AddRange(w.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(8));
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // a mod without a readable mod.json still lists, by its folder
            }
            return new Manifest(title, version, author, description, romfs, files, exefs, plugin, code, warnings);
        }
    }

    /// <summary>The mods the game is told to load, in its order: <c>MKW_MODS</c> when the environment sets it (the game follows that), else mkw.ini.</summary>
    internal static (List<string> Mods, bool FromEnvironment) Enabled(GameInstall install) =>
        Environment.GetEnvironmentVariable("MKW_MODS") is { } env
            ? ([.. env.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0)], true)
            : (Mod.Listed(Mod.ReadIni(install.Root)), false);

    /// <summary>Every mod of the install, by name (the game's own page sorts them so too). Never throws: an unreadable folder is an empty list.</summary>
    public static IReadOnlyList<GameMod> List(GameInstall install, string? locale)
    {
        ArgumentNullException.ThrowIfNull(install);
        var mods = new List<GameMod>();
        if (!Capable(install))
        {
            return mods;
        }
        var dir = ModsDir(install);
        var (enabled, fromEnvironment) = Enabled(install);
        try
        {
            if (!Directory.Exists(dir))
            {
                return mods;
            }
            var files = new Dictionary<string, List<string>>(StringComparer.Ordinal); // a game file → the enabled mods that ship it, in the game's order
            var folders = Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>()
                .Where(ValidId).OrderBy(n => n, StringComparer.Ordinal).Take(MaxMods).ToList();
            foreach (var id in enabled.Where(e => folders.Contains(e, StringComparer.OrdinalIgnoreCase)))
            {
                foreach (var rel in ManifestPaths(Path.Combine(dir, id, "romfs")))
                {
                    if (!files.TryGetValue(rel, out var owners))
                    {
                        files[rel] = owners = [];
                    }
                    owners.Add(id);
                }
            }
            foreach (var id in folders)
            {
                var folder = Path.Combine(dir, id);
                var manifest = Manifest.Read(folder, id);
                var managed = IsManaged(id);
                var hasPlugin = manifest.HasPlugin || Directory.Exists(Path.Combine(folder, "plugin"));
                var hasFiles = manifest.HasRomfs || Directory.Exists(Path.Combine(folder, "romfs"));
                var code = manifest.Code || manifest.HasExefs || Directory.Exists(Path.Combine(folder, "exefs"));
                var on = enabled.Contains(id, StringComparer.OrdinalIgnoreCase);
                var problems = new List<string>();
                if (fromEnvironment)
                {
                    problems.Add(Strings.ModsEnvironment);
                }
                if (manifest.Warnings.Contains("shaders"))
                {
                    problems.Add(Strings.ModsWarnShaders);
                }
                if (manifest.Warnings.Contains("graphics"))
                {
                    problems.Add(Strings.ModsWarnGraphics);
                }
                if (on)
                {
                    foreach (var other in files.Values.Where(o => o.Count > 1 && o.Contains(id)).SelectMany(o => o).Where(o => o != id).GroupBy(o => o))
                    {
                        var shared = files.Values.Count(o => o.Contains(id) && o.Contains(other.Key));
                        problems.Add(Strings.ModsShares(shared, TitleOf(dir, other.Key)));
                    }
                }
                GameSettingsSchema? options = null;
                try
                {
                    var path = Path.Combine(folder, ModOptions.FileName);
                    if (!managed && File.Exists(path) && new FileInfo(path).Length <= ModOptions.MaxBytes) // MKWMP's own settings are the launcher's pages (Voice & Camera, the overlay), never listed here
                    {
                        options = ModOptions.Parse(File.ReadAllBytes(path), locale, out var why);
                        if (why is not null)
                        {
                            problems.Add(why);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problems.Add(e.Message);
                }
                var (size, _) = Measure(folder);
                mods.Add(new GameMod(id, manifest.Title)
                {
                    Version = manifest.Version,
                    Author = manifest.Author,
                    Description = manifest.Description,
                    Enabled = on,
                    HasPlugin = hasPlugin,
                    HasFiles = hasFiles,
                    FileCount = manifest.RomfsFiles,
                    ChangesCode = code,
                    Managed = managed,
                    Locked = managed ? Strings.ModsManaged : code ? Strings.ModsChangesCode : fromEnvironment ? Strings.ModsEnvironment : null,
                    Size = size,
                    Options = options,
                    Source = ReadSource(folder),
                    Problems = problems,
                });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // what was read stays
        }
        return mods;
    }

    private static string TitleOf(string dir, string id) => Manifest.Read(Path.Combine(dir, id), id).Title;

    /// <summary>The game files a mod's romfs/manifest.tsv names (the game reads nothing else of the folder).</summary>
    private static IEnumerable<string> ManifestPaths(string romfs)
    {
        string[] lines;
        try
        {
            var path = Path.Combine(romfs, "manifest.tsv");
            if (!File.Exists(path) || new FileInfo(path).Length > 32 << 20)
            {
                yield break;
            }
            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var line in lines)
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab > 0 && line[0] != '#')
            {
                yield return line[..tab];
            }
        }
    }

    private static (long Bytes, int Files) Measure(string folder)
    {
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                bytes += file.Length;
                if (++files >= ModImport.MaxFiles)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // what was counted
        }
        return (bytes, files);
    }

    // ---- where it came from -----------------------------------------------------------------

    internal static ModSource? ReadSource(string folder)
    {
        try
        {
            var path = Path.Combine(folder, SourceFile);
            if (!File.Exists(path) || new FileInfo(path).Length > 65536)
            {
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            int Int(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : 0;
            var url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps ? parsed.AbsoluteUri : null;
            var version = root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String ? ver.GetString() ?? "" : "";
            var added = root.TryGetProperty("file_added", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var at) ? at : 0;
            return new ModSource(url, Int("gamebanana_mod"), Int("gamebanana_file"), version.Length > 32 ? version[..32] : version, added);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void WriteSource(string folder, ModSource source, DateTimeOffset now)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            if (source.Url is { } url)
            {
                json.WriteString("url", url);
            }
            if (source.GameBananaMod > 0)
            {
                json.WriteNumber("gamebanana_mod", source.GameBananaMod);
                json.WriteNumber("gamebanana_file", source.GameBananaFile);
                json.WriteNumber("file_added", source.FileAdded);
            }
            json.WriteString("version", source.Version);
            json.WriteString("installed", now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            json.WriteEndObject();
        }
        File.WriteAllBytes(Path.Combine(folder, SourceFile), stream.ToArray());
    }

    // ---- on, off, gone ----------------------------------------------------------------------

    private static string Folder(GameInstall install, string id)
    {
        if (!ValidId(id))
        {
            throw new LauncherException(Strings.ModsNotAMod(id));
        }
        var folder = Path.Combine(ModsDir(install), id);
        return Directory.Exists(folder) ? folder : throw new LauncherException(Strings.ModsNotAMod(id));
    }

    /// <summary>
    /// Turns a mod on (appended to the list: it wins shared files over the ones already on) or off.
    /// Throws <see cref="LauncherException"/> with what the player must know: the game runs, the mod is
    /// MKWMP's own, it patches the game's code, or it carries native code and <paramref name="acceptCode"/> is false.
    /// </summary>
    public static void SetEnabled(IGameTarget target, GameInstall install, string id, bool on, bool acceptCode, Action<string>? note = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(install);
        if (!Capable(install))
        {
            throw new LauncherException(Strings.ModsNone(target.DisplayName));
        }
        var folder = Folder(install, id);
        if (IsManaged(id))
        {
            throw new LauncherException(Strings.ModsManaged);
        }
        if (target.IsRunning(install))
        {
            throw new LauncherException(Strings.ModsCloseGame);
        }
        var manifest = Manifest.Read(folder, id);
        if (manifest.Code || manifest.HasExefs || Directory.Exists(Path.Combine(folder, "exefs")))
        {
            throw new LauncherException(Strings.ModsChangesCode);
        }
        if (on && !acceptCode && (manifest.HasPlugin || Directory.Exists(Path.Combine(folder, "plugin"))))
        {
            throw new LauncherException(Strings.ModsRunsCode);
        }
        if (Mod.SetEnabled(install.Root, id, on))
        {
            note?.Invoke($"mods: {id} turned {(on ? "on" : "off")} in mkw.ini ([launcher] mods = {string.Join(',', Mod.Listed(Mod.ReadIni(install.Root)))})");
            Remerge(install, note);
        }
    }

    /// <summary>Removes a mod: its folder, and its name from the list. Its settings file (the player's) stays, as the game's own page leaves it.</summary>
    public static void Uninstall(IGameTarget target, GameInstall install, string id, Action<string>? note = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(install);
        var folder = Folder(install, id);
        if (IsManaged(id))
        {
            throw new LauncherException(Strings.ModsManaged);
        }
        if (target.IsRunning(install))
        {
            throw new LauncherException(Strings.ModsCloseGame);
        }
        var manifest = Manifest.Read(folder, id);
        if (manifest.Code || manifest.HasExefs)
        {
            throw new LauncherException(Strings.ModsChangesCode); // the game's code holds it: its own page takes it out and builds again
        }
        try
        {
            // one folder directly inside mods/, never a link followed (a junction is removed, not what it points to)
            if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(folder);
            }
            else
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(Strings.ModsFailed(e.Message), e);
        }
        note?.Invoke($"mods: removed {folder}");
        if (Mod.SetEnabled(install.Root, id, on: false))
        {
            note?.Invoke($"mods: took {id} out of mkw.ini's mods");
        }
        Remerge(install, note);
    }

    // ---- the merge of shared archives -------------------------------------------------------

    /// <summary>
    /// After the set of enabled mods changed: the game's merged copy of the archives several mods share
    /// (<c>mods/.merged</c>, read last by the game) belongs to the old set. With the install's own tools it
    /// is made again the way its Mods page does (tools/mod/merge.py); without them, or when that fails, it
    /// is removed, and the mod turned on last wins a shared file whole.
    /// </summary>
    internal static void Remerge(GameInstall install, Action<string>? note)
    {
        var mods = ModsDir(install);
        var merged = Path.Combine(mods, Merged);
        var enabled = Mod.Listed(Mod.ReadIni(install.Root));
        var sharing = enabled.Count(m => ValidId(m) && File.Exists(Path.Combine(mods, m, "romfs", "manifest.tsv")));
        try
        {
            if (sharing >= 2 && RunMerge(install, enabled, note))
            {
                return;
            }
            if (Directory.Exists(merged))
            {
                Directory.Delete(merged, recursive: true);
                note?.Invoke("mods: removed mods/.merged (it was made for another set of mods)");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            note?.Invoke($"mods: mods/.merged could not be removed: {e.Message}");
        }
    }

    private static bool RunMerge(GameInstall install, List<string> enabled, Action<string>? note)
    {
        var python = RecompSetup.Python(install);
        var script = Path.Combine(install.Root, "setup", "src", "tools", "mod", "merge.py");
        if (!File.Exists(python) || !File.Exists(script) || GameRomfs.Locate(install) is not { } romfs)
        {
            return false;
        }
        try
        {
            var data = Recomp.DataDir(install.Root);
            var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = install.Root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-I", script, "--game", data, "--romfs", romfs, "--enable", string.Join(',', enabled), "--choices", Path.Combine(data, "mods", "choices.json") })
            {
                start.ArgumentList.Add(arg);
            }
            foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                start.Environment.Remove(name);
            }
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                process.Kill(entireProcessTree: true);
                note?.Invoke("mods: the merge of shared archives took too long and was stopped");
                return false;
            }
            note?.Invoke($"mods: merge of shared archives ended with code {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            note?.Invoke($"mods: the merge of shared archives did not start: {e.Message}");
            return false;
        }
    }

    // ---- a mod's settings -------------------------------------------------------------------

    /// <summary>The values the mod's settings file has for the options it declares, by key (an option at its default is absent).</summary>
    public static IReadOnlyDictionary<string, string> ReadOptions(GameInstall install, GameMod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return mod.Options is { } schema ? ModOptions.Known(schema, ModSettings.ReadAll(install, mod.Id)) : new Dictionary<string, string>();
    }

    /// <summary>
    /// Changes options of a mod (key to value; null removes the line, which is the default again), each
    /// checked against the mod's schema. Every other line of the file stays. Throws <see cref="LauncherException"/>
    /// for the player: the game runs, the game never ran (no profile yet), a value the option does not take.
    /// </summary>
    public static void WriteOptions(IGameTarget target, GameInstall install, GameMod mod, IReadOnlyDictionary<string, string?> changes)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(changes);
        var schema = mod.Options ?? throw new LauncherException(Strings.ModsNoOptions);
        var checkedChanges = new List<KeyValuePair<string, string?>>();
        foreach (var (key, value) in changes)
        {
            var setting = schema.Settings.FirstOrDefault(s => s.Id == key) ?? throw new LauncherException(Strings.ModSettingsNotASetting(key));
            checkedChanges.Add(new(setting.Id, value is null ? null : setting.Normalize(value)));
        }
        foreach (var (key, value) in checkedChanges)
        {
            if (!ModSettings.Write(target, install, mod.Id, key, value))
            {
                throw new LauncherException(Strings.ModsNoOptions);
            }
        }
    }

    // ---- before PLAY ------------------------------------------------------------------------

    /// <summary>
    /// What the game's own start screen does at Play (mods_before_play): with another set of mods than
    /// the last start played, the first player's save is copied to <c>profile/save-before-mods/&lt;time&gt;/</c>
    /// (the newest ten stay) and <c>mods_played</c> takes the set. The launcher starts the game past that
    /// screen, so it does this itself. Never throws; says what it did.
    /// </summary>
    public static void BeforePlay(GameInstall install, DateTime now, Action<string> note)
    {
        try
        {
            if (!Capable(install) || Environment.GetEnvironmentVariable("MKW_PROFILE_DIR") is { Length: > 0 } || Environment.GetEnvironmentVariable("MKW_PLAYER") is { Length: > 0 })
            {
                return;
            }
            var ini = Mod.ReadIni(install.Root) ?? "";
            var on = string.Join(',', Mod.Listed(ini));
            var values = RecompIni.Values(ini);
            var played = (values.GetValueOrDefault("MODS_PLAYED") ?? "").Trim().Trim('"');
            if (string.Join(',', played.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0)) == on)
            {
                return;
            }
            var profile = Path.Combine(Recomp.DataDir(install.Root), "profile");
            var save = Path.Combine(profile, "save");
            if (on.Length > 0 && Directory.Exists(save))
            {
                var root = Path.Combine(profile, "save-before-mods");
                var copy = Path.Combine(root, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                var files = 0;
                foreach (var file in Directory.EnumerateFiles(save, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var to = Path.Combine(copy, Path.GetRelativePath(save, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(file, to, overwrite: true);
                    files++;
                }
                note($"mods: the save before these mods ({on}): {files} files in {copy}");
                // only its own copies, named by their time: the oldest go
                var copies = Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>()
                    .Where(n => n.Length == 15 && n[8] == '-' && n.Where((c, i) => i != 8).All(char.IsAsciiDigit)).OrderBy(n => n, StringComparer.Ordinal).ToList();
                foreach (var old in copies.Take(Math.Max(0, copies.Count - KeepSaveCopies)))
                {
                    Directory.Delete(Path.Combine(root, old), recursive: true);
                }
            }
            Mod.EditIni(install.Root, text => RecompIni.Apply(text, [new("mods_played", on.Length == 0 ? null : on)], _ => "launcher"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LauncherException)
        {
            note($"mods: the save was not copied before these mods: {e.Message}");
        }
    }
}

/// <summary>
/// The game's own files (its RomFS), where the install reads them: a loose folder, or a <c>.nup</c>
/// package. A mod's file list (<c>romfs/manifest.tsv</c>) names the SHA-256 of the file each of its files
/// replaces, and the game checks it when it starts, so this must be exact: a file that is there and
/// cannot be read is an error, never "not there".
/// </summary>
internal static class GameRomfs
{
    /// <summary>The largest original the launcher hashes from a package (it is unpacked in memory).</summary>
    private const int MaxPacked = 1 << 30;

    /// <summary>The RomFS of this install, as the game resolves it (MKW_ROMFS, then mkw.ini's <c>romfs</c>, then <c>&lt;data&gt;/romfs</c>); null when there is none.</summary>
    public static string? Locate(GameInstall install)
    {
        var data = Recomp.DataDir(install.Root);
        var set = Environment.GetEnvironmentVariable("MKW_ROMFS") is { Length: > 0 } env ? env
            : RecompIni.Values(Mod.ReadIni(install.Root) ?? "").GetValueOrDefault("ROMFS") is { Length: > 0 } ini ? ini.Trim().Trim('"')
            : null;
        var path = set is null ? Path.Combine(data, "romfs") : Path.IsPathRooted(set) ? set : Path.Combine(data, set);
        return Directory.Exists(path) || File.Exists(path) ? path : null;
    }

    /// <summary>A reader of originals: (found, SHA-256 hex or null when it could not be read).</summary>
    public static Func<string, (bool Found, string? Sha256)>? Open(GameInstall install)
    {
        if (Locate(install) is not { } path)
        {
            return null;
        }
        if (Directory.Exists(path))
        {
            return rel => Loose(path, rel);
        }
        if (NupPackage.Open(path) is not { } nup)
        {
            return null; // a package this reader does not know (.nx)
        }
        return rel =>
        {
            var cut = rel.LastIndexOf('/');
            var (folder, name) = cut < 0 ? ("", rel) : (rel[..cut], rel[(cut + 1)..]);
            var found = false;
            var bytes = nup.ReadRomfs(folder, n =>
            {
                var it = n == name;
                found |= it;
                return it;
            }, MaxPacked);
            return (found, bytes is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        };
    }

    /// <summary>The file at exactly this path, letter case included: the game compares names byte for byte, Windows does not.</summary>
    private static (bool Found, string? Sha256) Loose(string root, string rel)
    {
        try
        {
            var at = root;
            foreach (var part in rel.Split('/'))
            {
                var next = Directory.EnumerateFileSystemEntries(at, part).Select(Path.GetFileName).FirstOrDefault(n => n == part);
                if (next is null)
                {
                    return (false, null);
                }
                at = Path.Combine(at, next);
            }
            if (!File.Exists(at))
            {
                return (false, null);
            }
            using var file = new FileStream(at, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            return (true, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)));
        }
        catch (DirectoryNotFoundException)
        {
            return (false, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (true, null);
        }
    }
}

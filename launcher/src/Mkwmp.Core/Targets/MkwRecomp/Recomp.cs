// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.Concurrent;
using System.Text.Json;
using Mkwmp.Core.Net;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core.Targets.MkwRecomp;

internal enum RecompKind
{
    /// <summary>A supported release.</summary>
    Supported,

    /// <summary>Another known release.</summary>
    Other,

    /// <summary>Not an mkw.exe from any release we know (older, newer, or modified).</summary>
    Unknown,

    /// <summary>No mkw.exe in the folder.</summary>
    Missing,
}

internal readonly record struct RecompId(RecompKind Kind, string? Version)
{
    public bool Ok => Kind == RecompKind.Supported;

    /// <summary>One line for the window; null when supported.</summary>
    public string? Problem => Kind switch
    {
        RecompKind.Supported => null,
        RecompKind.Other => $"mkw-recomp {Version}: MKWMP needs {Recomp.SupportedList}",
        RecompKind.Unknown => $"Unknown mkw-recomp: MKWMP needs {Recomp.SupportedList}",
        _ => "Set the game folder (the one with mkw.exe)",
    };

    /// <summary>Why, and what to do (details, logs).</summary>
    public string Explain => Kind switch
    {
        RecompKind.Supported => $"mkw-recomp {Version}: supported by this MKWMP.",
        RecompKind.Missing => "No mkw.exe in this folder.",
        _ => $"The MKWMP game patch hooks specific mkw-recomp releases ({Recomp.SupportedList}); other versions crash or can't "
            + $"connect. Install the Windows build of mkw-recomp {Recomp.Recommended} ({Recomp.Download}) with your game "
            + "files, and pick that folder.",
    };

    /// <summary>As the Rust launcher's bug reports wrote it (<c>Supported("0.1.15")</c>), so old and new reports read alike.</summary>
    public override string ToString() => Kind switch
    {
        RecompKind.Supported => $"Supported(\"{Version}\")",
        RecompKind.Other => $"Other(\"{Version}\")",
        RecompKind.Unknown => "Unknown",
        _ => "Missing",
    };
}

/// <summary>
/// Which mkw-recomp a game folder holds. The game patch hooks one runtime build (addresses,
/// symbols, fiber and host-function layout), so MKWMP runs only with the releases in
/// <see cref="Supported"/>. Each release's mkw.exe (the Windows build, also what runs under
/// Proton) is identified by its SHA-256, taken from the release zips: the official ones and
/// MKWMP's fork, 0.1.15-mkwmp (github.com/anarqz/mkw-recomp), which added the camera to the
/// plugin API before upstream took it in 0.1.16. The patch also hooks one game version (guest addresses): <see cref="Games"/>
/// lists the ones it has tables for, read from mkw-recomp's setup-state.json.
/// </summary>
internal static class Recomp
{
    public const string Exe = "mkw.exe";

    /// <summary>The runtimes the game patch supports.</summary>
    public static readonly string[] Supported = ["0.1.10", "0.1.12", "0.1.13", "0.1.15", "0.1.15-mkwmp", "0.1.16"];

    /// <summary>What the window recommends installing.</summary>
    public const string Recommended = "0.1.15-mkwmp";
    public const string Download = "https://github.com/anarqz/mkw-recomp/releases/tag/v0.1.15-mkwmp";

    /// <summary>mkw.exe SHA-256 per mkw-recomp release (mkw-recomp-&lt;v&gt;-windows-x64.zip).</summary>
    internal static readonly (string Version, string Sha256)[] Known =
    [
        ("0.1.10", "4fe39fc71d04ed46caf07476e9cdc688f7438f472983c424a2d27e62febcbdcb"),
        ("0.1.11", "8d65b3e3beae4411ea0bf195da8841dc2f23414bc85eedfa593c9f39f54d2a43"),
        ("0.1.12", "fbd215a5f232e9fc786bb4a7e5823d0d7a64ad04e09f50ed37b1cb2d5d66d1b1"),
        ("0.1.13", "1c47ffa2502a1ee92c2d01da0791802c18bf6c3365040c2b4fa1c91dacbc8695"),
        ("0.1.15", "511148040eac4f71cc3f003ace2d100fef784f1756339d88ca939896016d86e9"),
        ("0.1.15-mkwmp", "c6d732115683fff57ab1cd7e68414934c4fa132b2a5afc48247a8826797d58b9"),
        ("0.1.16", "e1829a08a7e24ab0f54425fd5e6a400a6fe5c54798757a80a104268e6c18e029"),
    ];

    /// <summary>MKWMP's mod folder name, under mkw-recomp's <c>mods/</c>.</summary>
    public const string Mod = "mkwmp";

    /// <summary>
    /// The supported releases that load mods (MKW_MODS): plugin API 1 in 0.1.15, 3 in 0.1.15-mkwmp
    /// and 0.1.16. The mod asks the runtime which version it has and leaves out what is missing.
    /// </summary>
    public static readonly string[] ModRuntimes = ["0.1.15", "0.1.15-mkwmp", "0.1.16"];

    /// <summary>
    /// Game versions the game patch has guest-address tables for. On any other version its hooks
    /// would write into unrelated game memory. Players only meet others on the same version (the
    /// lobby keeps them apart).
    /// </summary>
    public static readonly string[] Games = ["1.0.0", "1.8.0"];

    /// <summary>
    /// The game build id each runtime compiles the game for (<c>&lt;data dir&gt;/mkwgame.id</c>, also
    /// the first word of setup-state.json's <c>generate</c>). A runtime refuses a game set up by
    /// another one until its start screen has set it up again.
    /// </summary>
    private static readonly (string Runtime, string Id)[] GameIds =
    [
        ("0.1.12", "c0dd6cf12c07c23a"),
        ("0.1.13", "c0dd6cf12c07c23a"),
        ("0.1.15", "c9bbcb75a9ac9a49"),
        ("0.1.15-mkwmp", "c0dd6cf12c07c23a"),
        ("0.1.16", "c9bbcb75a9ac9a49"), // unchanged from 0.1.15: a game set up by it needs no rebuild
    ];

    private static readonly string[] ModLibraries = ["mkwmp.dll", "libmkwmp.so", "mkwmp.so"];

    public const string RebuildProblem = "mkw-recomp was updated: open mkw-recomp once and let it finish its setup, then press PLAY again";

    public static string SupportedList => string.Join(" or ", Supported);

    /// <summary>Tests' stand-ins for a real mkw.exe (sha256 → version); never filled outside the test project.</summary>
    internal static ConcurrentDictionary<string, string> TestExes { get; } = new(StringComparer.Ordinal);

    public static RecompId IdentifyBytes(ReadOnlySpan<byte> exe)
    {
        var hash = Updater.Sha256Hex(exe);
        var version = TestExes.TryGetValue(hash, out var test) ? test : Known.FirstOrDefault(k => k.Sha256 == hash).Version;
        return version switch
        {
            null => new RecompId(RecompKind.Unknown, null),
            _ when Supported.Contains(version) => new RecompId(RecompKind.Supported, version),
            _ => new RecompId(RecompKind.Other, version),
        };
    }

    /// <summary>
    /// Answers keyed by a file's path, size and modification time: hashing 33 MB (or re-reading
    /// the setup state) every time the window asks would be wasteful.
    /// </summary>
    private sealed class FileCache<T>
    {
        private readonly ConcurrentDictionary<string, (long Length, DateTime Modified, T Value)> _seen = new(StringComparer.OrdinalIgnoreCase);

        public bool TryGet(string path, out T value, Func<string, T> read)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    value = default!;
                    return false;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                value = default!;
                return false;
            }
            if (_seen.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.Modified == info.LastWriteTimeUtc)
            {
                value = hit.Value;
                return true;
            }
            value = read(path);
            _seen[path] = (info.Length, info.LastWriteTimeUtc, value);
            return true;
        }
    }

    private static readonly FileCache<RecompId> ExeCache = new();
    private static readonly FileCache<string?> VersionCache = new();
    private static readonly FileCache<string?> IdCache = new();

    public static RecompId Identify(string game)
    {
        var missing = new RecompId(RecompKind.Missing, null);
        return ExeCache.TryGet(Path.Combine(game, Exe), out var id, path =>
        {
            try
            {
                return IdentifyBytes(File.ReadAllBytes(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return missing;
            }
        }) ? id : missing;
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where mkw-recomp keeps this installation's data (mkwgame.dll, mkw.ini, logs/, profile/):
    /// the folder with mkw.exe, unless mkw-recomp 0.1.12's "choose game folder" wrote
    /// <c>game_dir = "&lt;path&gt;"</c> into the mkw.ini beside mkw.exe (absolute, or relative to that
    /// folder). The game patch's own files always stay beside mkw.exe. Under Wine / Proton a
    /// Linux path there (<c>/home/deck/mkw</c>, written by the Linux build of mkw-recomp) is the
    /// same folder on the drive Wine maps the Linux root to (<c>Z:\home\deck\mkw</c>).
    /// </summary>
    public static string DataDir(string game) => DataDir(game, Platform.WineHost.LinuxRoot);

    /// <param name="linuxRoot">The Linux root as this process sees it (<c>Z:\</c>); null on Windows.</param>
    internal static string DataDir(string game, string? linuxRoot)
    {
        if (ReadText(Path.Combine(game, "mkw.ini")) is not { } ini)
        {
            return game;
        }
        foreach (var line in ini.Split('\n'))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0 || !line[..eq].Trim().Equals("game_dir", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var v = Unquote(line[(eq + 1)..].Trim());
            if (v.Length == 0)
            {
                break;
            }
            if (linuxRoot is not null && v[0] == '/' && (v.Length == 1 || v[1] != '/'))
            {
                return Path.Combine(linuxRoot, v[1..].Replace('/', '\\'));
            }
            var absolute = v[0] is '/' or '\\' || (v.Length > 1 && v[1] == ':');
            return absolute ? v : Path.Combine(game, v);
        }
        return game;
    }

    private static string Unquote(string v) => v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;

    /// <summary>
    /// The MKWMP mod runs in this game: a runtime that loads mods, and the mod enabled there. A
    /// leftover <c>mods = mkwmp</c> on an older runtime does not count: nothing would load it.
    /// </summary>
    public static string? ActiveMod(string game) =>
        Identify(game) is { Kind: RecompKind.Supported, Version: { } v } && ModRuntimes.Contains(v) ? ModDir(game) : null;

    /// <summary>
    /// The MKWMP mod's folder when mkw-recomp (0.1.15+) loads it, by the same rule as the probe:
    /// <c>mkwmp</c> is in MKW_MODS (the environment wins, else <c>mods</c> under <c>[launcher]</c>
    /// in the installation's mkw.ini, the data dir's when game_dir is set) and
    /// <c>&lt;data dir&gt;/mods/mkwmp/plugin/mkwmp.dll</c> exists. The mod then carries the game
    /// patch, so the launcher keeps no winmm.dll of its own beside mkw.exe (the mod wins).
    /// </summary>
    public static string? ModDir(string game)
    {
        var data = DataDir(game);
        var mods = Environment.GetEnvironmentVariable("MKW_MODS");
        if (mods is null)
        {
            if (ReadText(Path.Combine(data, "mkw.ini")) is not { } ini || ModsIn(ini) is not { } listed)
            {
                return null;
            }
            mods = listed;
        }
        if (!ModsListHas(mods, Mod))
        {
            return null;
        }
        var dir = Path.Combine(data, "mods", Mod);
        return ModLibraries.Any(f => File.Exists(Path.Combine(dir, "plugin", f))) ? dir : null;
    }

    /// <summary>The <c>mods</c> value under <c>[launcher]</c>.</summary>
    internal static string? ModsIn(string ini)
    {
        var launcher = false;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                launcher = line.TrimStart('[').TrimEnd(']').Trim().Equals("launcher", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0 && launcher && line[..eq].Trim() == "mods")
            {
                return line[(eq + 1)..].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// mkw-recomp's list: names between commas, leading spaces skipped. Compared in any case, as
    /// the probe does (Windows opens the folder in any case). Quotes around the whole value are accepted.
    /// </summary>
    internal static bool ModsListHas(string list, string name) =>
        Unquote(list.Trim()).Split(',').Any(m => m.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The game version mkw-recomp set up, from <c>&lt;data dir&gt;/setup-state.json</c>
    /// (<c>"generate": "&lt;RT_GAME_ID&gt; &lt;game version&gt; &lt;hash&gt;"</c>, under <c>done</c>). Null when
    /// the file or field is missing or unreadable.
    /// </summary>
    public static string? GameVersion(string game) =>
        VersionCache.TryGet(Path.Combine(DataDir(game), "setup-state.json"), out var v, path => ReadText(path) is { } t ? GameVersionIn(t) : null) ? v : null;

    private static string? Generate(string json)
    {
        var v = Json.Parse(json);
        return (v.At("done").At("generate") ?? v.At("generate")).Str();
    }

    /// <summary>
    /// The second word of <c>generate</c>, if it looks like a version (dotted digits); anything
    /// else counts as unknown rather than refusing a layout we do not know.
    /// </summary>
    internal static string? GameVersionIn(string json)
    {
        if (Generate(json) is not { } generate)
        {
            return null;
        }
        var words = generate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return null;
        }
        var parts = words[1].Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)) ? words[1] : null;
    }

    /// <summary>The id the game in this folder was set up for: mkwgame.id, else the first word of <c>generate</c>.</summary>
    internal static string? GameId(string game)
    {
        var data = DataDir(game);
        var file = new[] { Path.Combine(data, "mkwgame.id"), Path.Combine(data, "setup-state.json") }.FirstOrDefault(File.Exists);
        if (file is null)
        {
            return null;
        }
        return IdCache.TryGet(file, out var id, path =>
            ReadText(path) is not { } t ? null : path.EndsWith("mkwgame.id", StringComparison.OrdinalIgnoreCase) ? IdWord(t) : GameIdIn(t)) ? id : null;
    }

    /// <summary>The id starts the file (or the word): 16 hex digits.</summary>
    internal static string? IdWord(string text)
    {
        var t = text.TrimStart();
        return t.Length >= 16 && t[..16].All(char.IsAsciiHexDigit) ? t[..16].ToLowerInvariant() : null;
    }

    internal static string? GameIdIn(string json) => Generate(json) is { } g ? IdWord(g) : null;

    /// <summary>The game was set up by another runtime: PLAY would stop at once.</summary>
    internal static string? RebuildProblemFor(RecompId runtime, string? id)
    {
        if (runtime is not { Kind: RecompKind.Supported, Version: { } v } || id is null)
        {
            return null;
        }
        var want = GameIds.FirstOrDefault(g => g.Runtime == v).Id;
        return want is not null && id != want ? RebuildProblem : null;
    }

    internal static string? GameProblemFor(string? version) => version is null || Games.Contains(version)
        ? null
        : $"Game {version}: MKWMP supports game {string.Join(" or ", Games)} (a newer MKWMP may support {version})";

    /// <summary>Why PLAY is refused for this game version, or because the game must be set up again for this runtime; null when fine or unknown.</summary>
    public static string? GameProblem(string game) => GameProblemFor(GameVersion(game)) ?? RebuildProblemFor(Identify(game), GameId(game));

    /// <summary>One line for logs and bug reports.</summary>
    public static string GameLine(string game) => GameVersion(game) switch
    {
        { } v when Games.Contains(v) => $"game version: {v}",
        { } v => $"game version: {v} (not supported)",
        null => "game version: unknown (no setup-state.json generate)",
    };
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// mkw-recomp's own settings, as its menu shows them (runtime/core/base/cfg.c <c>metas</c>,
/// runtime/ui/menu/settings_tabs.cpp; texts from i18n/ui/mkw.pot, mkw-recomp 0.1.15, 0.1.16 and 0.1.17):
/// the Screen, Graphics, Sound, Controls, General and Advanced tabs. They live in <c>&lt;data dir&gt;/mkw.ini</c>
/// (beside mkw.exe unless <c>game_dir</c> moved the installation; the same on Windows, under
/// Proton and in the Linux build), one <c>key = value</c> line each, absent while at its default.
/// <para>
/// Left out on purpose: the paths and the game's own bookkeeping (<c>romfs</c>, <c>exefs</c>,
/// <c>game_dir</c>, <c>mods</c>, <c>mods_played</c>, <c>mods_ack</c>, <c>player</c>,
/// <c>shortcut_asked</c>), which the game and MKWMP's install depend on; the Hacks tab (turbo,
/// unlock everything...), which has no place in online races; <c>lan</c> (LAN play turns the
/// title menu's Wireless Play into LAN Play, which may conflict with MKWMP's online play) and
/// with it 0.1.16's <c>lan_ip</c> (the LAN network row, a list of this PC's adapters); what the
/// game writes for itself (<c>hdr_system</c>) and the keys with no menu row (<c>wb_par</c>,
/// <c>dsu</c>'s server and slot aside); and what only the game can offer:
/// the graphics card (a list of Vulkan devices), the key and button bindings (<c>bind_*</c>),
/// input recording (a path) and the buttons (Best settings for this PC, Reset all).
/// </para>
/// <para>
/// The game reads the file once at start and writes back only the keys its menu changed, so the
/// launcher writes while the game is closed. An <c>MKW_*</c> environment variable wins over the
/// file: such a setting is locked here, as it is greyed out in the game.
/// </para>
/// </summary>
public static partial class RecompSettings
{
    public const string IniName = "mkw.ini";
    public static string GameIsRunning => Strings.GameSettingsRunning;
    public static string NextStart => Strings.GameSettingsNextStart;
    public const string Quality = "quality";

    /// <summary>
    /// The runtimes whose settings this table describes (others name or read some keys differently).
    /// 0.1.16-mkwmp is the fork's next build: upstream 0.1.16's menu, unchanged. 0.1.17 is upstream's next release:
    /// 0.1.16's menu and its own new rows (<see cref="Has017Rows"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> Runtimes = ["0.1.15", "0.1.15-mkwmp", "0.1.16", "0.1.16-mkwmp", "0.1.16-mkwmp.1", "0.1.17"];

    /// <summary>
    /// The Logic rate row came right after upstream 0.1.15 (the fork's 0.1.15-mkwmp is built from
    /// that later source, so it has the row; the official 0.1.15 does not know the key).
    /// </summary>
    internal static bool HasLogicRate(string version) => version != "0.1.15";

    /// <summary>0.1.16's rows: the HDR tone mapper with its peak and paper white, Keyboard and mouse, NSFW mods.</summary>
    internal static bool Has016Rows(string version) => version is "0.1.16" or "0.1.16-mkwmp" or "0.1.16-mkwmp.1" || Has017Rows(version);

    /// <summary>
    /// 0.1.17's rows: DLSS / FSR 3.1 upscaling (its quality, the DLSS model, CAS), frame generation's engine, NVIDIA
    /// Reflex (#462); the keys with no menu row the launcher offers too: the shadow-map draws (#644) and the first
    /// controller to player 2 (#742). Not MKW_GPU_CLOCKS (#645): it is Linux's amdgpu only (vk_pstate.c, __linux__), and
    /// MKWMP runs the Windows build, also under Proton.
    /// </summary>
    internal static bool Has017Rows(string version) => version is "0.1.17";

    /// <summary>
    /// Where the setup puts the HDR tone mapper's programs (prepare.py <c>tonemap</c>, from 0.1.16).
    /// Without the folder the game greys the row out: a game set up by an older runtime has none
    /// until it is set up again.
    /// </summary>
    public const string TonemapFolder = "spirv-renodx";

    /// <summary>mkw.pot UI_MENU_HDR_TONEMAP_MISSING.</summary>
    public static string TonemapMissing => Strings.GameSettingsTonemapMissing;

    /// <summary>The settings a Quality preset sets, and the Steam Deck's defaults (cfg.c DECK).</summary>
    private static readonly string[] QualityRows = ["aniso", "tex_quality", "detail_distance", "shadow_size"];

    private static readonly (string Key, string Value)[] Deck = [("fullscreen", "1"), ("window_size", "1280x800"), ("vk_present", "fifo"), ("resolution", "720")];

    /// <summary>cfg.c <c>sections</c>: where a key the file lacks is added.</summary>
    private static readonly Dictionary<string, string> Sections = new(StringComparer.Ordinal)
    {
        ["FULLSCREEN"] = "display", ["WINDOW_SIZE"] = "display", ["PERF_OVERLAY"] = "display", ["VK_PRESENT"] = "display", ["UPSCALER"] = "display",
        ["HDR"] = "display", ["HDR_TONEMAP"] = "display", ["HDR_PEAK"] = "display", ["HDR_WHITE"] = "display", ["SHARPNESS"] = "display", ["PICTURE_FIT"] = "display", ["RESOLUTION"] = "display", ["ASPECT"] = "display",
        ["HUD_POSITION"] = "display", ["FPS"] = "display", ["LOGIC_HZ"] = "display", ["FRAMEGEN"] = "display", ["VRR"] = "display", ["LOW_LATENCY"] = "display",
        ["TEMPORAL"] = "display", ["TEMPORAL_QUALITY"] = "display", ["DLSS_PRESET"] = "display", ["CAS"] = "display", ["CAS_SHARPNESS"] = "display",
        ["FG_ENGINE"] = "display", ["REFLEX"] = "display",
        ["SHADOW_DRAWS"] = "graphics", // a key cfg.c gives no section (K): next to the shadow map's size
        ["QUALITY"] = "graphics", ["ANISO"] = "graphics", ["TEX_QUALITY"] = "graphics", ["DETAIL_DISTANCE"] = "graphics", ["SHADOW_SIZE"] = "graphics",
        ["MENU_LANG"] = "language", ["LANG"] = "language", ["OPERATION_MODE"] = "language",
        ["VOLUME"] = "audio", ["AUDIO_CHANNELS"] = "audio", ["BACKGROUND_MUTE"] = "audio",
        ["MOTION"] = "input", ["RUMBLE_STRENGTH"] = "input", ["RUMBLE_CURVE"] = "input", ["RUMBLE_TRIGGERS"] = "input", ["PAD_LAYOUT"] = "input",
        ["BUTTON_ICONS"] = "input", ["MENU_SHORTCUT"] = "input", ["MOUSE_SENS"] = "input", ["KEYBOARD_MOUSE"] = "input", ["STICK_DEADZONE"] = "input", ["SWITCH2"] = "input", ["PAD_P2"] = "input",
        ["DSU"] = "input", ["DSU_SERVER"] = "input", ["DSU_SLOT"] = "input",
        ["ASYNC_PREEMPT"] = "performance", ["REPLAY_ASYNC"] = "performance", ["FREE_CORES"] = "performance", ["TIMER_1MS"] = "performance",
        ["PIPE_WAIT"] = "performance", ["PIPE_SKIP"] = "performance", ["PIPE_PREBUILD"] = "performance", ["GPL"] = "performance", ["ASTC"] = "performance",
        ["MEMORY"] = "performance", ["TEX_ALIAS"] = "performance", ["FIX_C0"] = "performance", ["FIX_ZERO"] = "performance", ["BIND_SKIP"] = "performance",
        ["SHADER_MEMO"] = "performance", ["PIPE_MEMO"] = "performance", ["TEX_MEMO"] = "performance", ["DESC_TEMPLATE"] = "performance",
        ["PUSH_SKIP"] = "performance", ["SPLIT_DRAWS"] = "performance", ["VK_VALIDATION"] = "performance", ["VK_DYN_VERTEX"] = "performance",
        ["VK_SUBGROUP_PIN"] = "performance",
        ["TRACE"] = "diagnostics", ["FLIGHT"] = "diagnostics", ["FLIGHT_SPIKE_MS"] = "diagnostics",
        ["UPDATE_CHECK"] = "launcher", ["START_SCREEN"] = "launcher", ["DISCORD"] = "launcher", ["GB_MATURE"] = "launcher",
    };

    private static readonly ConcurrentDictionary<string, GameSettingsSchema> Schemas = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^[1-9]\d{2,4}x[1-9]\d{2,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowSize();

    private static string? RealEnv(string name) => Environment.GetEnvironmentVariable(name);


    /// <summary>
    /// The settings file of this install; null when it is not an mkw-recomp whose settings this
    /// launcher knows, or when <c>MKW_GAME_DIR</c> sends the game to another installation.
    /// </summary>
    public static string? PathFor(GameInstall install) => PathFor(install, RealEnv);

    internal static string? PathFor(GameInstall install, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (install.TargetId != MkwRecompTarget.TargetId || install.Version is not { } version || !Runtimes.Contains(version)
            || env("MKW_GAME_DIR") is { Length: > 0 })
        {
            return null;
        }
        return Path.Combine(install.DataRoot ?? install.Root, IniName);
    }

    /// <summary>What the file sets (the game's key names, upper case); empty when there is none or it cannot be read.</summary>
    private static Dictionary<string, string> FileValues(string path)
    {
        try
        {
            return File.Exists(path) ? RecompIni.Values(RecompIni.Decode(File.ReadAllBytes(path))) : new(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>What this install's mkw.ini sets (the game's key names, upper case); empty when the launcher does not know its settings.</summary>
    public static IReadOnlyDictionary<string, string> IniValues(GameInstall install) =>
        PathFor(install, RealEnv) is { } path ? FileValues(path) : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The schema for this install (the same object while nothing it depends on changed); null when it has none.</summary>
    public static GameSettingsSchema? Schema(GameInstall install) => Schema(install, RealEnv);

    internal static GameSettingsSchema? Schema(GameInstall install, Func<string, string?> env, bool? underWine = null)
    {
        var wine = underWine ?? Platform.WineHost.Detected;
        if (PathFor(install, env) is not { } path)
        {
            return null;
        }
        var file = FileValues(path);
        // cfg.c profile_of and cfg_get: the Steam Deck's defaults (SteamDeck=1, which Steam sets there and Proton passes
        // on), else fullscreen when started from Big Picture or Gaming Mode. The launcher's environment stands for the
        // game's: the game it starts inherits it.
        var profile = env("MKW_DEVICE_PROFILE") ?? file.GetValueOrDefault("DEVICE_PROFILE") ?? "";
        var deck = profile == "deck" || (profile is "" or "auto" && env("SteamDeck") == "1");
        // Under Wine the launcher starts the game full screen (RecompLaunchEnv): the default there, kept on SteamOS and in gamescope.
        var console = !deck && (wine || env("SteamGamepadUI") == "1" || env("SteamTenfoot") == "1" || env("GAMESCOPE_WAYLAND_DISPLAY") is { Length: > 0 });
        var keptFullscreen = RecompLaunchEnv.ForcesFullscreen(wine, env);
        var size = file.GetValueOrDefault("WINDOW_SIZE") is { } s && WindowSize().IsMatch(s) ? s : "";
        var locked = Sections.Keys.Where(key => env("MKW_" + key) is not null).Select(key => key.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();
        var owned = RecompSetup.Drives(install);
        var version = install.Version!;
        // cfg.c reads MKW_SPIRV too (a developer's other shader folder); the launcher only knows the install's own.
        var noTonemap = Has016Rows(version) && !Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, TonemapFolder));
        // the texts are in the language in use: another language, another schema
        return Schemas.GetOrAdd($"{Strings.Generation}|{version}|{deck}|{console}|{size}|{string.Join(',', locked)}|{owned}|{noTonemap}|{keptFullscreen}", _ =>
        {
            var all = Localize(Build(version, deck, console, size));
            return locked.Length == 0 && !owned && !noTonemap && !keptFullscreen ? all : all with
            {
                Categories = [.. all.Categories.Select(c => c with
                {
                    Settings = [.. c.Settings.Select(x =>
                        locked.Contains(x.Id) ? x with { Locked = Strings.GameSettingsLockedByEnv("MKW_" + x.Id.ToUpperInvariant()) }
                        : keptFullscreen && x.Id == "fullscreen" ? x with { Locked = Strings.GameSettingsOwnedFullscreen, Default = "1" }
                        : owned && Owned.FirstOrDefault(o => o.Id == x.Id).Why is { } why ? x with { Locked = why }
                        : noTonemap && x.Id == "hdr_tonemap" ? x with { Locked = TonemapMissing }
                        : x)],
                })],
            };
        });
    }

    /// <summary>
    /// The settings MKWMP keeps for an mkw-recomp it installs and sets up itself
    /// (<see cref="RecompSetup.Drives"/>), with the reason the window shows on the locked row.
    /// <c>update_check = 0</c>: mkw-recomp's own updater follows its repository's newest release and
    /// would replace the one build this MKWMP is made for. <c>start_screen = skip</c>: a game
    /// started by hand or from Steam goes straight in, as it does from the launcher
    /// (<c>mkw.exe --play</c> never shows that screen, whatever this says).
    /// </summary>
    /// <summary>mkw-recomp's pad shortcut for its own menu: <c>minus_plus</c> (Minus and Plus held together, Select+Start on an Xbox pad) or <c>guide</c>.</summary>
    public const string MenuShortcut = "menu_shortcut";

    /// <summary>
    /// The <c>menu_shortcut</c> that leaves the player's overlay shortcut alone (launcher/docs/OVERLAY.md §1.1).
    /// mkw-recomp has no "off" (cfg.c takes <c>minus_plus|guide</c>; anything else acts as <c>minus_plus</c>), so:
    /// Guide alone gives mkw-recomp Select+Start; every other choice gives it Guide. For the default, Guide or
    /// Select + Start, Guide is the harmless one: the mod's pad filter takes Guide from every reading before
    /// mkw-recomp's shortcut looks (and Steam or the Game Bar usually take it first), while Select+Start as
    /// mkw-recomp's would also open its menu on a short Select given back to the game with Start held.
    /// </summary>
    public static string MenuShortcutFor(Overlay.OverlayShortcut shortcut) =>
        shortcut == Overlay.OverlayShortcut.Guide ? "minus_plus" : "guide";

    internal static (string Id, string Value, string Why)[] Owned =>
    [
        ("update_check", "0", Strings.GameSettingsOwnedUpdateCheck),
        ("start_screen", "skip", Strings.GameSettingsOwnedStartScreen),
    ];

    /// <summary>
    /// Writes <see cref="Owned"/> into mkw.ini when they are not there yet. Nothing happens for an
    /// install the launcher does not set up, while the game runs (it holds its settings and may
    /// write them back), or when the file cannot be written: the next install, setup or call tries
    /// again. Returns what it wrote, for the log.
    /// </summary>
    public static IReadOnlyList<string> EnforceOwned(IGameTarget target, GameInstall install) => EnforceOwned(target, install, RealEnv);

    internal static IReadOnlyList<string> EnforceOwned(IGameTarget target, GameInstall install, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!RecompSetup.Drives(install) || PathFor(install, env) is not { } path)
        {
            return [];
        }
        var file = FileValues(path);
        var edits = Owned.Where(o => file.GetValueOrDefault(o.Id.ToUpperInvariant()) != o.Value)
            .Select(o => new KeyValuePair<string, string?>(o.Id, o.Value)).ToList();
        if (edits.Count == 0 || target.IsRunning(install))
        {
            return [];
        }
        try
        {
            return Save(path, edits) ? [.. edits.Select(e => $"{e.Key} = {e.Value}")] : [];
        }
        catch (LauncherException)
        {
            return [];
        }
    }

    /// <summary>The edits applied to the file through a temporary one; false when nothing changed. Throws <see cref="LauncherException"/> with why it could not be written.</summary>
    private static bool Save(string path, IReadOnlyList<KeyValuePair<string, string?>> edits)
    {
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(folder))
            {
                throw new LauncherException(Strings.GameSettingsFolderGone(folder));
            }
            var exists = File.Exists(path);
            if (exists && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            {
                throw new LauncherException(Strings.GameSettingsReadOnly(IniName));
            }
            var before = exists ? RecompIni.Decode(File.ReadAllBytes(path)) : "";
            var after = RecompIni.Apply(before, edits, key => Sections.GetValueOrDefault(key));
            if (after == before)
            {
                return false;
            }
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

    /// <summary>
    /// The values the file sets, by setting id (a setting at its default is absent). Quality is
    /// told from the four settings it presets, as the game's menu does: "custom" when they match
    /// no preset.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(GameInstall install) => Read(install, RealEnv);

    internal static IReadOnlyDictionary<string, string> Read(GameInstall install, Func<string, string?> env)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (PathFor(install, env) is not { } path || Schema(install, env) is not { } schema)
        {
            return values;
        }
        var file = FileValues(path);
        foreach (var setting in schema.Settings)
        {
            if (file.TryGetValue(setting.Id.ToUpperInvariant(), out var value))
            {
                values[setting.Id] = value;
            }
        }
        var quality = schema.Find(Quality)!;
        var now = quality.Options.FirstOrDefault(o => o.Sets is { } sets && sets.All(p => (values.GetValueOrDefault(p.Key) ?? schema.Find(p.Key)!.Default) == p.Value))?.Value ?? "custom";
        if (now != quality.Default || values.ContainsKey(Quality))
        {
            values[Quality] = now;
        }
        return values;
    }

    /// <summary>
    /// Changes settings in mkw.ini (a null or empty value removes the key's line: back to the
    /// default), leaving every other line as it is, through a temporary file. Throws
    /// <see cref="LauncherException"/> for a setting or value the game does not take, a locked
    /// setting, a running game or a file that cannot be written.
    /// </summary>
    public static void Write(IGameTarget target, GameInstall install, IReadOnlyDictionary<string, string?> changes) => Write(target, install, changes, RealEnv);

    internal static void Write(IGameTarget target, GameInstall install, IReadOnlyDictionary<string, string?> changes, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(changes);
        if (PathFor(install, env) is not { } path || Schema(install, env) is not { } schema)
        {
            throw new LauncherException(Strings.GameSettingsNoneHere);
        }
        var edits = new List<KeyValuePair<string, string?>>();
        foreach (var (id, value) in changes)
        {
            var setting = schema.Find(id) ?? throw new LauncherException(Strings.GameSettingsNotASetting(id));
            if (setting.Locked is { } why)
            {
                throw new LauncherException(Strings.GameSettingLocked(setting.Label, why));
            }
            edits.Add(new(setting.Id, string.IsNullOrEmpty(value) ? null : setting.Normalize(value)));
        }
        if (edits.Count == 0)
        {
            return;
        }
        if (target.IsRunning(install))
        {
            throw new LauncherException(GameIsRunning);
        }
        Save(path, edits);
    }

    // ---- the texts in the player's language ------------------------------------------------

    /// <summary>A heading's key part: "Shaders and pipelines" is "shaders_and_pipelines".</summary>
    private static string Slug(string text) => NotWord().Replace(text.ToLowerInvariant(), "_").Trim('_');

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotWord();

    /// <summary>"2x", "16:9", "1080p", "60 Hz", "120 fps": the same in every language.</summary>
    [GeneratedRegex(@"^[\d\s.:x×]*(p|Hz|fps)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Figures();

    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
        { "FPS", "HDR", "CPU", "AMD FSR 1", "Nintendo", "Xbox", "PlayStation", "PsychoV35", "VSync", "NVIDIA DLSS", "AMD FSR 3.1", "MKW Recomp", "J", "K", "L", "M" };

    /// <summary>An option whose label is a figure, a product's name or a language's own name is not translated.</summary>
    private static bool Translatable(GameSetting setting, GameSettingOption option) =>
        !Figures().IsMatch(option.Label) && !Names.Contains(option.Label) && !(setting.Id is "lang" or "menu_lang" && option.Value.Length > 0);

    /// <summary>
    /// The catalog keys of a schema's texts (<c>gs.*</c> in <c>i18n/*.json</c>) with their English:
    /// <c>gs.page.&lt;page&gt;</c> and <c>.note</c>, <c>gs.group.&lt;heading&gt;</c>, <c>gs.&lt;id&gt;</c>,
    /// <c>gs.&lt;id&gt;.desc</c> and <c>gs.&lt;id&gt;.opt.&lt;value&gt;</c> ("auto" for the empty value).
    /// </summary>
    internal static IEnumerable<(string Key, string English)> Texts(GameSettingsSchema schema)
    {
        foreach (var page in schema.Categories)
        {
            yield return ($"gs.page.{page.Id}", page.Title);
            if (page.Note is { } note)
            {
                yield return ($"gs.page.{page.Id}.note", note);
            }
            foreach (var s in page.Settings)
            {
                if (s.Group is { } group)
                {
                    yield return ($"gs.group.{Slug(group)}", group);
                }
                yield return ($"gs.{s.Id}", s.Label);
                yield return ($"gs.{s.Id}.desc", s.Description);
                foreach (var o in s.Options.Where(o => Translatable(s, o)))
                {
                    yield return (OptionKey(s, o), o.Label);
                }
            }
        }
    }

    /// <summary>Every text of every schema this table builds (each runtime, the Steam Deck or not), once per key.</summary>
    internal static IReadOnlyDictionary<string, string> AllTexts()
    {
        var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var version in Runtimes)
        {
            foreach (var (key, english) in Texts(Build(version, deck: false, console: false, "")))
            {
                all[key] = english;
            }
        }
        return all;
    }

    private static string OptionKey(GameSetting s, GameSettingOption o) => $"gs.{s.Id}.opt.{(o.Value.Length == 0 ? "auto" : o.Value)}";

    /// <summary>
    /// The schema with its texts in the language in use (<see cref="Strings.T(string, string)"/>: English where the catalog has none).
    /// Public for the design-time launcher, whose sample of these settings goes through the same catalogs.
    /// </summary>
    public static GameSettingsSchema Localize(GameSettingsSchema schema) => schema with
    {
        Categories = [.. schema.Categories.Select(page => page with
        {
            Title = Strings.T($"gs.page.{page.Id}", page.Title),
            Note = page.Note is { } note ? Strings.T($"gs.page.{page.Id}.note", note) : null,
            Settings = [.. page.Settings.Select(s => s with
            {
                Label = Strings.T($"gs.{s.Id}", s.Label),
                Description = Strings.T($"gs.{s.Id}.desc", s.Description),
                Group = s.Group is { } group ? Strings.T($"gs.group.{Slug(group)}", group) : null,
                Options = [.. s.Options.Select(o => Translatable(s, o) ? o with { Label = Strings.T(OptionKey(s, o), o.Label) } : o)],
            })],
        })],
    };

    // ---- the table ------------------------------------------------------------------------

    private const bool Live = false;
    private const bool Restart = true;

    private static GameSettingOption O(string value, string label) => new(value, label);

    private static GameSettingOption Preset(string value, string label, params string[] rows) =>
        new(value, label, QualityRows.Zip(rows).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal));

    private static GameSettingCondition When(string setting, params string[] values) => new(setting, values);

    private static GameSettingOption[] Sizes(bool deck, string current)
    {
        var sizes = new List<string> { "1280x720", "1366x768", "1600x900", "1920x1080", "2560x1440", "3200x1800", "3840x2160" };
        foreach (var extra in new[] { deck ? "1280x800" : "", current })
        {
            if (extra.Length > 0 && !sizes.Contains(extra))
            {
                sizes.Add(extra);
            }
        }
        static long Area(string s) => s.Split('x') is [var w, var h] ? long.Parse(w, CultureInfo.InvariantCulture) * long.Parse(h, CultureInfo.InvariantCulture) : 0;
        return [.. sizes.OrderBy(Area).Select(s => O(s, s.Replace("x", " × ", StringComparison.Ordinal)))];
    }

    /// <summary>
    /// A row the game shows as a slider with Automatic below its lowest value (option_rows.cpp
    /// <c>opt_slider_auto</c>): "auto" or a whole number of nits, which <paramref name="pattern"/> bounds.
    /// </summary>
    private static GameSetting Nits(string id, string label, string description, string pattern) =>
        GameSetting.Text(id, label, description, "auto", Live, 5, pattern) with { Group = "Picture", VisibleWhen = When("hdr_tonemap", "psychov") };

    private static GameSettingsSchema Build(string version, bool deck, bool console, string windowSize)
    {
        var new016 = Has016Rows(version);
        var new017 = Has017Rows(version);
        GameSetting[] hdr = !new016 ? [] :
        [
            GameSetting.Choice("hdr_tonemap", "HDR tone mapper",
                "How the scene's brightness is fitted to the display in HDR. Vanilla is the game's own curve, which never goes above 1.5 times paper white. PsychoV35 is RenoDX's: the same mid-tones and colour grade, with highlights that roll off smoothly up to the display's peak. Applies after a restart, as do peak and paper white changes while it is on.",
                "vanilla", Restart, O("vanilla", "Vanilla"), O("psychov", "PsychoV35")) with { Group = "Picture", VisibleWhen = When("hdr", "auto") },
            Nits("hdr_peak", "HDR peak brightness",
                "The brightest the game draws, in nits (100 to 10000). Automatic (auto) uses the display's peak as Windows reports it. Only with the PsychoV35 tone mapper.",
                "auto|[1-9][0-9]{2,3}|10000"),
            Nits("hdr_white", "HDR paper white",
                "How bright white menus and the scene's mid-tones are, in nits (40 to 1000). Automatic (auto) uses Windows' SDR content brightness, which is often much brighter than a game wants; 203 nits is the HDR reference. Lower values leave the highlights more room. Only with the PsychoV35 tone mapper.",
                "auto|[4-9][0-9]|[1-9][0-9]{2}|1000"),
        ];
        GameSetting[] screen =
        [
            GameSetting.Toggle("fullscreen", "Fullscreen", "Fills the screen with the game; F11 toggles it.", false, Live),
            GameSetting.Choice("window_size", "Window size", "Size of the game window when not fullscreen.", "1920x1080", Live, Sizes(deck, windowSize))
                with { VisibleWhen = When("fullscreen", "0") },
            GameSetting.Choice("vk_present", "Presentation", "How finished frames reach the screen.", "mailbox", Live,
                O("mailbox", "Mailbox"), O("fifo", "VSync"), O("immediate", "Immediate")),
            GameSetting.Choice("picture_fit", "Picture shape",
                "When the window's shape differs from the game resolution's (a 16:9 game on the Steam Deck's 16:10 screen), keep the picture's proportions with black bars, or stretch it over the whole window.",
                "keep", Live, O("keep", "Keep (bars)"), O("stretch", "Stretch")) with { Group = "Picture" },
            GameSetting.Choice("aspect", "Aspect ratio",
                "Wider than 16:9 shows more of the course at the sides, for ultrawide screens. 16:9 is the console's; Window takes the window's or monitor's shape. Menus stay stretched for now.",
                "16:9", Restart, O("16:9", "16:9"), O("window", "Window"), O("21:9", "21:9"), O("32:9", "32:9"), O("48:9", "48:9")) with { Group = "Picture" },
            GameSetting.Choice("hud_position", "HUD position",
                "On a picture wider or taller than 16:9, keep the race HUD in the middle 16:9 area (the console's place) or put it at the screen's edges and corners.",
                "16:9", Restart, O("16:9", "16:9 area"), O("edges", "Screen edges")) with { Group = "Picture" },
            GameSetting.Choice("hdr", "HDR",
                "Automatic shows the game's own HDR picture when the display is in HDR (on Windows, Settings > Display > Use HDR), as the console does; its brightness follows the system's HDR settings. Off always shows the standard picture.",
                "auto", Live, O("auto", "Automatic"), O("off", "Off")) with { Group = "Picture" },
            .. hdr,
        ];

        var graphics = new List<GameSetting>
        {
            GameSetting.Choice(Quality, "Quality",
                "Sets the four settings below at once. Console looks like the Switch 2; Low and Medium are lighter for the graphics card, High and Very high sharper. Changing one of them makes it Custom. The internal resolution is what changes the frame rate most.",
                "console", Restart,
                Preset("low", "Low", "game", "quarter", "1", "1024"), Preset("medium", "Medium", "game", "half", "1.5", "2048"),
                Preset("console", "Console", "game", "full", "2", "console"), Preset("high", "High", "8", "full", "3", "4096"),
                Preset("ultra", "Very high", "16", "full", "4", "4096"), O("custom", "Custom")),
            GameSetting.Choice("aniso", "Texture filtering",
                "Sharpness of textures seen far away or at a low angle, such as the road ahead. Game uses what each texture asks for. Impact on frame rate: low.",
                "game", Restart, O("game", "Game"), O("2", "2x"), O("4", "4x"), O("8", "8x"), O("16", "16x")),
            GameSetting.Choice("tex_quality", "Texture quality",
                "Detail of the game's own textures. Half and Quarter skip their finest levels: less memory traffic on the graphics card, blurrier surfaces up close. Impact on frame rate: low.",
                "full", Restart, O("full", "Full"), O("half", "Half"), O("quarter", "Quarter")),
            GameSetting.Choice("detail_distance", "Detail distance",
                "How far objects keep their detailed models before switching to simpler ones. Console is the Switch 2's. Impact on frame rate: low.",
                "2", Restart, O("1", "Low"), O("1.5", "Medium"), O("2", "Console"), O("3", "High"), O("4", "Very high")),
            GameSetting.Choice("shadow_size", "Shadow quality",
                "Size of the map the game draws shadows from. Smaller is lighter for the graphics card, with blurrier shadow edges. Console is 3072 docked and 2048 handheld. Impact on frame rate: low.",
                "console", Restart, O("console", "Console"), O("1024", "1024"), O("2048", "2048"), O("3072", "3072"), O("4096", "4096")),
            GameSetting.Choice("resolution", "Internal resolution",
                "Lines the game renders its scenes at, then scaled to the window. Lower is faster: 720p suits integrated graphics. Original is the console's (up to 1440p).",
                "original", Restart, O("original", "Original"), O("540", "540p"), O("720", "720p"), O("900", "900p"), O("1080", "1080p"), O("1440", "1440p"), O("2160", "2160p"))
                with { Group = "Resolution" },
            GameSetting.Choice("upscaler", "Scaling to the window",
                "How the picture is scaled when the window is larger than the game resolution. FSR keeps edges and detail sharper than bilinear.",
                "bilinear", Live, O("fsr", "AMD FSR 1"), O("bilinear", "Bilinear")) with { Group = "Resolution" },
            GameSetting.Range("sharpness", "FSR sharpness", "Sharpening after FSR scaling; 0 turns it off.", 80, Live, 0, 100, 5, "%")
                with { Group = "Resolution", VisibleWhen = When("upscaler", "fsr") },
            GameSetting.Choice("operation_mode", "Console mode",
                "Whether the game thinks it runs docked or handheld. Docked, it draws the scene smaller and enlarges it with its own scaler; handheld, at the full internal resolution with smaller shadows: sharper, but more work for the graphics card.",
                "docked", Restart, O("docked", "Docked"), O("handheld", "Handheld")) with { Group = "Resolution" },
            GameSetting.Choice("fps", "Frame rate",
                "Frames per second the game aims for. Automatic lowers it from 60 to 30, 20 or 15 when frames take too long, so the race always runs at its real speed. At 20 or 15, Frame generation can show the frames in between. 20 and 15 apply only in races: menus, movies and loading screens go at 30 at most, as on the console.",
                "auto", Live, O("auto", "Automatic"), O("60", "60"), O("30", "30"), O("20", "20"), O("15", "15")) with { Group = "Performance" },
            GameSetting.Choice("logic_hz", "Logic rate",
                "How many times a second the game updates karts, physics and timers. 60 is the console's rate; 120, 144 or 240 make movement smoother on a high refresh rate screen but need a faster PC. Time trials and LAN games always run at 60.",
                "60", Restart, O("60", "60 Hz"), O("120", "120 Hz"), O("144", "144 Hz"), O("240", "240 Hz")) with { Group = "Performance" },
            GameSetting.Choice("framegen", "Frame generation",
                "Shows frames made between the game's own: 2x turns 30 fps into 60. Automatic makes only as many as divide your monitor's refresh rate evenly. Adds up to one frame of delay, and fast motion can smear. Not on the console.",
                "off", Live, O("off", "Off"), O("auto", "Automatic"), O("2", "2x"), O("3", "3x"), O("4", "4x"), O("120", "120 fps")) with { Group = "Performance" },
            GameSetting.Toggle("vrr", "Variable refresh screen",
                "Turn on if your screen has FreeSync or G-Sync and it is enabled. Off, Automatic frame generation only makes the frames that divide the refresh rate evenly, so motion does not judder.",
                false, Live) with { Group = "Performance" },
            GameSetting.Toggle("low_latency", "Low latency",
                "Starts each frame as late as your computer allows, so what you press reaches the screen sooner. Matters most at 20 or 15 fps and with Frame generation. Turn it off if the picture stutters.",
                true, Live) with { Group = "Performance" },
            GameSetting.Choice("perf_overlay", "Performance overlay",
                "Frame rate in the top right corner. Full adds frame times with a graph, CPU, GPU and memory load, temperatures and the render thread's waits. F10 cycles the modes.",
                "off", Live, O("off", "Off"), O("fps", "FPS"), O("full", "Full")) with { Group = "Performance" },
        };

        if (!HasLogicRate(version))
        {
            graphics.RemoveAll(s => s.Id == "logic_hz");
        }
        if (new017)
        {
            // settings_tabs.cpp tab_graphics: DLSS / FSR 3.1 under the FSR sharpness (gfx/vk/exec/vk_temporal.c); the game
            // shows them all, the launcher those the upscaler's choice uses. DLSS needs an NVIDIA RTX GPU, Reflex an NVIDIA
            // one, as their texts say; the game greys out what this PC cannot run ("Not available on this PC").
            string[] upscaled = ["dlss", "fsr3"];
            graphics.InsertRange(graphics.FindIndex(s => s.Id == "sharpness") + 1,
            [
                GameSetting.Choice("temporal", "AI upscaling",
                    "Renders the race at a lower resolution and rebuilds it from several frames: NVIDIA DLSS (RTX GPUs) or AMD FSR 3.1 (any GPU). Sharper and steadier edges than the game's own scaling; the HUD stays sharp.",
                    "off", Live, O("off", "Off"), O("dlss", "NVIDIA DLSS"), O("fsr3", "AMD FSR 3.1")) with { Group = "Resolution" },
                GameSetting.Choice("temporal_quality", "Upscaling quality",
                    "The share of the screen's resolution the race renders at: Native (DLAA) 100 %, Quality 67 %, Balanced 58 %, Performance 50 %, Ultra performance 33 %. Applies at the next start.",
                    "quality", Restart, O("native", "Native (DLAA)"), O("quality", "Quality"), O("balanced", "Balanced"), O("performance", "Performance"),
                    O("ultra", "Ultra performance")) with { Group = "Resolution", VisibleWhen = When("temporal", upscaled) },
                GameSetting.Choice("dlss_preset", "DLSS model",
                    "NVIDIA's model DLSS uses: the default of the DLSS version next to the game, or presets J, K, L, M.",
                    "default", Live, O("default", "Default"), O("j", "J"), O("k", "K"), O("l", "L"), O("m", "M"))
                    with { Group = "Resolution", VisibleWhen = When("temporal", "dlss") },
                GameSetting.Choice("cas", "CAS sharpening",
                    "AMD's contrast adaptive sharpening of the race after DLSS or FSR 3.1, before the HUD is drawn. Needs AI upscaling on.",
                    "off", Live, O("off", "Off"), O("on", "On")) with { Group = "Resolution", VisibleWhen = When("temporal", upscaled) },
                GameSetting.Range("cas_sharpness", "CAS strength", "How strongly CAS sharpens: 0 is the lightest touch, 100 the strongest.", 50, Live, 0, 100, 5, "%")
                    with { Group = "Resolution", VisibleWhen = When("cas", "on") },
            ]);
            graphics.InsertRange(graphics.FindIndex(s => s.Id == "framegen") + 1,
            [
                GameSetting.Choice("fg_engine", "Frame generation by",
                    "Who makes the frames between the game's own. The port's own works everywhere. NVIDIA DLSS Frame Generation (RTX 40 and 50) and AMD FSR 3.1 use the scene's depth and motion and need DLSS or FSR 3.1 upscaling on.",
                    "flow", Live, O("flow", "MKW Recomp"), O("dlss", "NVIDIA DLSS"), O("fsr3", "AMD FSR 3.1"))
                    with { Group = "Performance", VisibleWhen = When("framegen", "auto", "2", "3", "4", "120") },
                GameSetting.Choice("reflex", "NVIDIA Reflex",
                    "Starts each frame as late as the GPU allows, so what you press reaches the screen sooner. Boost also keeps the GPU's clocks up. NVIDIA GPUs only.",
                    "off", Live, O("off", "Off"), O("on", "On"), O("boost", "On + Boost")) with { Group = "Performance" },
            ]);
            // MKW_SHADOW_DRAWS (vk_exec_draw.c, read once at start): no menu row in the game; 1 by default, auto leaves the
            // shadow map out with 3 or more views (MKW_SHADOW_AUTO_RUNS), 0 always
            graphics.Insert(graphics.FindIndex(s => s.Id == "shadow_size") + 1, GameSetting.Choice("shadow_draws", "Shadow map",
                "Whether the game draws the map its shadows come from. Automatic leaves it out in 3 and 4 player split screen, where every view draws its own; Off leaves the shadows out everywhere, for the weakest graphics cards. Applies after a restart.",
                "1", Restart, O("1", "On"), O("auto", "Automatic"), O("0", "Off")));
        }

        var sound = new[]
        {
            GameSetting.Range("volume", "Volume", "Master volume of the game.", 100, Live, 0, 100, 5, "%"),
            GameSetting.Choice("audio_channels", "Speakers",
                "Automatic gives the game's 5.1 surround mix when the system's sound output has 6 channels or more, and stereo otherwise.",
                "auto", Restart, O("auto", "Automatic"), O("2", "Stereo"), O("6", "5.1 surround")),
            GameSetting.Toggle("background_mute", "Mute in background", "No sound while the game window is not in front.", false, Live),
        };

        var controls = new List<GameSetting>
        {
            GameSetting.Choice("pad_layout", "Button layout",
                "Which button confirms and accelerates, the same in the game and in this launcher. Nintendo: the right one (A on a Switch controller, B on an Xbox controller). Xbox: the bottom one (A on an Xbox controller, cross on a PlayStation controller, B on a Switch controller). Automatic: Nintendo for Switch controllers, Xbox for all others. The other of the two goes back.",
                "auto", Live, O("auto", "Automatic"), O("nintendo", "Nintendo (A right)"), O("xbox", "Xbox (A bottom)")),
            GameSetting.Choice("button_icons", "Button icons",
                "Which buttons the game's prompts show. Automatic follows the device you used last: the controller's own buttons (Xbox or PlayStation), or your keys and mouse. The icons follow your remapped buttons.",
                "auto", Live, O("auto", "Automatic"), O("nintendo", "Nintendo"), O("xbox", "Xbox"), O("playstation", "PlayStation"), O("keyboard", "Keyboard")),
            GameSetting.Choice("menu_shortcut", "Menu shortcut",
                "Which controller input opens the game's own menu (Start + Select or the Guide button). Keyboard: F1 or Esc. With the Guide button, turn off 'Guide button focuses Steam' in Steam and 'Allow your controller to open Game Bar' in Windows.",
                "minus_plus", Live, O("minus_plus", "Start + Select"), O("guide", "Guide button")),
            GameSetting.Toggle("motion", "Motion controls", "Steering by tilting the controller.", true, Live),
            GameSetting.Range("rumble_strength", "Vibration strength", "How strong the controller vibrates.", 130, Live, 0, 300, 10, "%"),
            GameSetting.Range("rumble_curve", "Vibration curve", "Lower values make weak vibration stronger.", 0.6, Live, 0.2, 1.5, 0.05),
            GameSetting.Toggle("rumble_triggers", "Trigger vibration (Xbox)", "Vibrates the triggers of an Xbox controller too.", false, Live),
            GameSetting.Toggle("switch2", "Switch 2 controllers (Bluetooth)",
                "Joy-Con 2 and Pro Controller 2 over Bluetooth. To pair one, hold its Sync button with the game open.", false, Restart),
            GameSetting.Toggle("dsu", "Motion from a DSU server",
                "The gyro of a controller that gives none to the game, such as one in XInput mode, read from a Cemuhook / DSU server (DS4Windows, BetterJoy...).",
                false, Live),
            GameSetting.Text("dsu_server", "DSU server", "The DSU server's address and port. Every DSU server uses port 26760.", "127.0.0.1:26760", Live, 64,
                @"[A-Za-z0-9.\-]+:[0-9]{1,5}") with { VisibleWhen = When("dsu", "1") },
            GameSetting.Range("dsu_slot", "DSU slot", "The server's controller slot that gives player 1 its motion (0 is the first).", 0, Live, 0, 3)
                with { VisibleWhen = When("dsu", "1") },
            GameSetting.Range("stick_deadzone", "Stick dead zone",
                "How far a stick must tilt before it counts. Raise it if the kart drifts with the stick at rest.", 8, Live, 0, 40, 1, "%"),
            GameSetting.Range("mouse_sens", "Mouse camera sensitivity", "How fast the mouse turns the camera.", 1, Live, 0, 10, 0.1),
        };
        if (new017)
        {
            // MKW_PAD_P2 (input.c scan_pads, #742): a cfg.c row in [input], no menu row in the game
            controls.Insert(controls.FindIndex(s => s.Id == "pad_layout") + 1, GameSetting.Toggle("pad_p2", "First controller to player 2",
                "The first controller plays as player 2 and the keyboard as player 1, for local play with a keyboard and a controller. Leave it off online: MKWMP's racer is player 1, so the controller would only work in the menus.",
                false, Live));
        }
        if (new016)
        {
            // settings_tabs.cpp tab_controls: between the stick dead zone and the mouse sensitivity
            controls.Insert(controls.Count - 1, GameSetting.Toggle("keyboard_mouse", "Keyboard and mouse",
                "Off: the game ignores the keyboard and the mouse (controllers only) and the mouse cursor stays free. Esc still opens this menu.",
                true, Live));
        }

        var general = new List<GameSetting>
        {
            GameSetting.Choice("lang", "Game language", "Language of the game's text and voices. Polish and Thai need a game update that ships them.", "", Restart,
                O("", "Automatic"), O("en-US", "English"), O("ja", "日本語"), O("fr", "Français"), O("de", "Deutsch"), O("it", "Italiano"), O("es", "Español"),
                O("nl", "Nederlands"), O("pt", "Português"), O("pt-BR", "Português (Brasil)"), O("ru", "Русский"), O("zh-CN", "简体中文"), O("zh-TW", "繁體中文"),
                O("ko", "한국어"), O("ca", "Català"), O("gl", "Galego"), O("eu", "Euskara"), O("tr", "Türkçe"), O("pl", "Polski"), O("th", "ไทย")),
            GameSetting.Choice("menu_lang", "Menu language", "Language of the game's own settings menu and overlay.", "", Live,
                O("", "Automatic (from the system)"), O("en", "English"), O("ja", "日本語"), O("fr", "Français"), O("de", "Deutsch"), O("it", "Italiano"),
                O("es", "Español"), O("nl", "Nederlands"), O("pt", "Português"), O("pt-BR", "Português (Brasil)"), O("ru", "Русский"), O("zh-Hans", "简体中文"),
                O("zh-Hant", "繁體中文"), O("ko", "한국어"), O("es-419", "Español (Latinoamérica)"), O("ca", "Català"), O("gl", "Galego"), O("eu", "Euskara"),
                O("pl", "Polski"), O("tr", "Türkçe"), O("uk", "Українська"), O("th", "ไทย")),
            GameSetting.Toggle("update_check", "mkw-recomp update check",
                "At start, mkw-recomp asks GitHub for a newer release of itself and offers it on its start screen. MKWMP works with specific mkw-recomp versions: a newer one may not play online until MKWMP supports it.",
                true, Live),
            GameSetting.Choice("start_screen", "mkw-recomp start screen",
                "Automatic skips it when the game starts from Steam's Big Picture or Gaming Mode, as on a console. It still opens when the game files need setting up, after an error, or when you hold View / Select (Minus) at start.",
                "auto", Live, O("auto", "Automatic"), O("always", "Always"), O("skip", "Skip")),
            GameSetting.Toggle("discord", "Discord status",
                "While the game runs, your Discord profile shows that you are playing, for how long, and whether you are racing or in the menus. Your Discord friends see it too.",
                false, Live),
        };
        if (new016)
        {
            // Not a row of the settings menu: the switch on the GameBanana tab of the start screen's Mods page.
            general.Add(GameSetting.Choice("gb_mature", "NSFW mods",
                "On the GameBanana tab of mkw-recomp's Mods page, mods that GameBanana rates as NSFW are blurred (Obfuscate) or shown. A blurred mod still names its content ratings.",
                "obfuscate", Live, O("obfuscate", "Obfuscate"), O("show", "Show")));
        }

        GameSetting Adv(string group, GameSetting s) => s with { Group = group };
        var optimisation = "Optimisation: ";
        var advanced = new[]
        {
            Adv("Scheduling", GameSetting.Choice("async_preempt", "Virtual core preemption", "Lets a game thread interrupt another one on the same virtual core.",
                "1", Restart, O("0", "Off"), O("1", "On"), O("2", "Inheritance only"))),
            Adv("Scheduling", GameSetting.Toggle("replay_async", "GPU frame prep on its own thread",
                "Prepares frames for the GPU on a separate thread instead of the game thread. Adds up to one frame of latency.", true, Restart)),
            Adv("Scheduling", GameSetting.Toggle("free_cores", "Virtual core gate", "Lets only one thread run per virtual core, as on the console.", false, Restart)),
            Adv("Scheduling", GameSetting.Toggle("timer_1ms", "1 ms Windows timer", "Raises the system timer resolution to 1 ms for steadier frame pacing.", true, Restart)),
            Adv("Shaders and pipelines", GameSetting.Choice("pipe_wait", "Waiting draw handling",
                "Default keeps the current scheduling with a reserved worker for waiting draws; Old (draw) uses the previous scheduling.",
                "default", Restart, O("default", "Default"), O("draw", "Old (draw)"))),
            Adv("Shaders and pipelines", GameSetting.Choice("pipe_skip", "Draws skipped while a pipeline builds", "Which draws are dropped instead of waiting for their pipeline.",
                "fullscreen", Restart, O("strict", "Strict"), O("any", "Any"), O("fullscreen", "Fullscreen passes"))),
            Adv("Shaders and pipelines", GameSetting.Toggle("pipe_prebuild", "Pipeline prebuild", "Builds known pipelines ahead of time.", true, Restart)),
            Adv("Shaders and pipelines", GameSetting.Toggle("gpl", "Graphics pipeline library",
                "Uses the Vulkan pipeline library extension; turn off if your driver misbehaves with it.", true, Restart)),
            Adv("Textures and memory", GameSetting.Choice("astc", "ASTC texture decoding", "Where compressed ASTC textures are decoded.",
                "auto", Restart, O("auto", "Automatic"), O("gpu", "GPU shader"), O("native", "Native"), O("cpu", "CPU"))),
            Adv("Textures and memory", GameSetting.Choice("memory", "GPU memory mode", "How game memory is shared with the GPU.",
                "auto", Restart, O("auto", "Automatic"), O("import", "Import"), O("mirror", "Mirror"), O("verify", "Verify"))),
            Adv("Textures and memory", GameSetting.Toggle("tex_alias", "Texture aliasing",
                "A new texture created over a live texture's memory starts with that content instead of empty.", true, Restart)),
            Adv("Render fixes", GameSetting.Toggle("fix_c0", "Constant buffer fix",
                "Fills the driver-reserved constant bank c0 (storage sizes, dispatch size, point size, base instance) instead of reading zeros.", true, Restart)),
            Adv("Render fixes", GameSetting.Toggle("fix_zero", "Zero new GPU memory", "New GPU memory pools start zero-filled, as Switch memory does.", true, Restart)),
            Adv("Render fixes", GameSetting.Toggle("bind_skip", "Skip repeated binds", optimisation + "skips bindings that did not change.", true, Restart)),
            Adv("Render fixes", GameSetting.Toggle("shader_memo", "Shader memo", optimisation + "remembers shader lookups.", true, Restart)),
            Adv("Render fixes", GameSetting.Toggle("pipe_memo", "Pipeline memo", optimisation + "reuses the previous draw's pipeline when its state repeats.", true, Restart)),
            Adv("Render fixes", GameSetting.Toggle("tex_memo", "Texture binding memo", optimisation + "remembers texture descriptors between draws.", true, Restart)),
            Adv("Render fixes", GameSetting.Toggle("desc_template", "Descriptor templates", optimisation + "updates descriptors in bulk.", true, Restart)),
            Adv("Render fixes", GameSetting.Toggle("push_skip", "Skip repeated push constants", optimisation + "skips push constants that did not change.", true, Restart)),
            Adv("Render fixes", GameSetting.Range("split_draws", "Early GPU submission",
                "Submits GPU work in parts every N draws between passes so the GPU starts earlier; 0 turns it off.", 64, Restart, 0, 1000, 8)),
            Adv("Vulkan", GameSetting.Toggle("vk_validation", "Validation layer",
                "Checks every Vulkan call and slows the game down a lot. Needs the Vulkan validation layer installed.", false, Restart)),
            Adv("Vulkan", GameSetting.Choice("vk_dyn_vertex", "Dynamic vertex state", "Forces dynamic vertex input on or off; the default depends on the GPU.",
                "", Restart, O("", "Automatic"), O("0", "Off"), O("1", "On"))),
            Adv("Vulkan", GameSetting.Toggle("vk_subgroup_pin", "Pin subgroup size", "Fixes the shader subgroup size.", true, Restart)),
            Adv("Diagnostics", GameSetting.Choice("trace", "Performance log", "Writes frame timings to a file.", "", Restart, O("", "Off"), O("perf", "On"))),
            Adv("Diagnostics", GameSetting.Toggle("flight", "Flight recorder", "Keeps a short history and saves it when a frame freezes.", false, Restart)),
            Adv("Diagnostics", GameSetting.Range("flight_spike_ms", "Freeze threshold", "Frame time that counts as a freeze.", 100, Restart, 20, 1000, 10, "ms")
                with { VisibleWhen = When("flight", "1") }),
        };

        var schema = new GameSettingsSchema(
        [
            new("screen", "Screen", "Fullscreen", screen),
            new("graphics", "Graphics", "Star", graphics),
            new("sound", "Sound", "Volume", sound),
            new("controls", "Controls", "Gamepad", controls),
            new("general", "General", "Globe", general),
            new("advanced", "Advanced", "Settings", advanced,
                "These settings are for testing and troubleshooting. Change them only when you know why.", Advanced: true),
        ],
            NextStart);
        if (!deck && !console)
        {
            return schema;
        }
        // The machine's defaults (below the file, above the table): what a setting falls back to here.
        var defaults = deck ? Deck : [("fullscreen", "1")];
        return schema with
        {
            Categories = [.. schema.Categories.Select(c => c with
            {
                Settings = [.. c.Settings.Select(s => defaults.FirstOrDefault(d => d.Key == s.Id).Value is { } value ? s with { Default = value } : s)],
            })],
        };
    }
}

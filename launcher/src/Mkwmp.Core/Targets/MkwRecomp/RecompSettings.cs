// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// mkw-recomp's own settings, as its menu shows them (runtime/core/base/cfg.c <c>metas</c>,
/// runtime/ui/menu/settings_tabs.cpp; texts from i18n/ui/mkw.pot, mkw-recomp 0.1.15): the Screen,
/// Graphics, Sound, Controls, General and Advanced tabs. They live in <c>&lt;data dir&gt;/mkw.ini</c>
/// (beside mkw.exe unless <c>game_dir</c> moved the installation; the same on Windows, under
/// Proton and in the Linux build), one <c>key = value</c> line each, absent while at its default.
/// <para>
/// Left out on purpose: the paths and the game's own bookkeeping (<c>romfs</c>, <c>exefs</c>,
/// <c>game_dir</c>, <c>mods</c>, <c>mods_played</c>, <c>mods_ack</c>, <c>player</c>,
/// <c>shortcut_asked</c>), which the game and MKWMP's install depend on; the Hacks tab (turbo,
/// unlock everything...), which has no place in online races; <c>lan</c> (LAN play turns the
/// title menu's Wireless Play into LAN Play, which may conflict with MKWMP's online play); and
/// what only the game can offer:
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
    public const string GameIsRunning = "Close the game first: it reads these settings when it starts";
    public const string NextStart = "Changes apply the next time the game starts.";
    public const string Quality = "quality";

    /// <summary>The runtimes whose settings this table describes (others name or read some keys differently).</summary>
    public static readonly IReadOnlyList<string> Runtimes = ["0.1.15", "0.1.15-mkwmp"];

    /// <summary>The settings a Quality preset sets, and the Steam Deck's defaults (cfg.c DECK).</summary>
    private static readonly string[] QualityRows = ["aniso", "tex_quality", "detail_distance", "shadow_size"];

    private static readonly (string Key, string Value)[] Deck = [("fullscreen", "1"), ("window_size", "1280x800"), ("vk_present", "fifo"), ("resolution", "720")];

    /// <summary>cfg.c <c>sections</c>: where a key the file lacks is added.</summary>
    private static readonly Dictionary<string, string> Sections = new(StringComparer.Ordinal)
    {
        ["FULLSCREEN"] = "display", ["WINDOW_SIZE"] = "display", ["PERF_OVERLAY"] = "display", ["VK_PRESENT"] = "display", ["UPSCALER"] = "display",
        ["HDR"] = "display", ["SHARPNESS"] = "display", ["PICTURE_FIT"] = "display", ["RESOLUTION"] = "display", ["ASPECT"] = "display",
        ["HUD_POSITION"] = "display", ["FPS"] = "display", ["LOGIC_HZ"] = "display", ["FRAMEGEN"] = "display", ["VRR"] = "display", ["LOW_LATENCY"] = "display",
        ["QUALITY"] = "graphics", ["ANISO"] = "graphics", ["TEX_QUALITY"] = "graphics", ["DETAIL_DISTANCE"] = "graphics", ["SHADOW_SIZE"] = "graphics",
        ["MENU_LANG"] = "language", ["LANG"] = "language", ["OPERATION_MODE"] = "language",
        ["VOLUME"] = "audio", ["AUDIO_CHANNELS"] = "audio", ["BACKGROUND_MUTE"] = "audio",
        ["MOTION"] = "input", ["RUMBLE_STRENGTH"] = "input", ["RUMBLE_CURVE"] = "input", ["RUMBLE_TRIGGERS"] = "input", ["PAD_LAYOUT"] = "input",
        ["BUTTON_ICONS"] = "input", ["MENU_SHORTCUT"] = "input", ["MOUSE_SENS"] = "input", ["STICK_DEADZONE"] = "input", ["SWITCH2"] = "input",
        ["DSU"] = "input", ["DSU_SERVER"] = "input", ["DSU_SLOT"] = "input",
        ["ASYNC_PREEMPT"] = "performance", ["REPLAY_ASYNC"] = "performance", ["FREE_CORES"] = "performance", ["TIMER_1MS"] = "performance",
        ["PIPE_WAIT"] = "performance", ["PIPE_SKIP"] = "performance", ["PIPE_PREBUILD"] = "performance", ["GPL"] = "performance", ["ASTC"] = "performance",
        ["MEMORY"] = "performance", ["TEX_ALIAS"] = "performance", ["FIX_C0"] = "performance", ["FIX_ZERO"] = "performance", ["BIND_SKIP"] = "performance",
        ["SHADER_MEMO"] = "performance", ["PIPE_MEMO"] = "performance", ["TEX_MEMO"] = "performance", ["DESC_TEMPLATE"] = "performance",
        ["PUSH_SKIP"] = "performance", ["SPLIT_DRAWS"] = "performance", ["VK_VALIDATION"] = "performance", ["VK_DYN_VERTEX"] = "performance",
        ["VK_SUBGROUP_PIN"] = "performance",
        ["TRACE"] = "diagnostics", ["FLIGHT"] = "diagnostics", ["FLIGHT_SPIKE_MS"] = "diagnostics",
        ["UPDATE_CHECK"] = "launcher", ["START_SCREEN"] = "launcher", ["DISCORD"] = "launcher",
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

    /// <summary>The schema for this install (the same object while nothing it depends on changed); null when it has none.</summary>
    public static GameSettingsSchema? Schema(GameInstall install) => Schema(install, RealEnv);

    internal static GameSettingsSchema? Schema(GameInstall install, Func<string, string?> env)
    {
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
        var console = !deck && (env("SteamGamepadUI") == "1" || env("SteamTenfoot") == "1" || env("GAMESCOPE_WAYLAND_DISPLAY") is { Length: > 0 });
        var size = file.GetValueOrDefault("WINDOW_SIZE") is { } s && WindowSize().IsMatch(s) ? s : "";
        var locked = Sections.Keys.Where(key => env("MKW_" + key) is not null).Select(key => key.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();
        return Schemas.GetOrAdd($"{deck}|{console}|{size}|{string.Join(',', locked)}", _ =>
        {
            var all = Build(deck, console, size);
            return locked.Length == 0 ? all : all with
            {
                Categories = [.. all.Categories.Select(c => c with
                {
                    Settings = [.. c.Settings.Select(x => locked.Contains(x.Id) ? x with { Locked = $"Set by the environment variable MKW_{x.Id.ToUpperInvariant()}" } : x)],
                })],
            };
        });
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
            throw new LauncherException("This game has no settings the launcher can change");
        }
        var edits = new List<KeyValuePair<string, string?>>();
        foreach (var (id, value) in changes)
        {
            var setting = schema.Find(id) ?? throw new LauncherException($"not a setting of this game: {id}");
            if (setting.Locked is { } why)
            {
                throw new LauncherException($"{setting.Label}: {why}");
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
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(folder))
            {
                throw new LauncherException($"The game's folder is gone: {folder}");
            }
            var exists = File.Exists(path);
            if (exists && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            {
                throw new LauncherException($"{IniName} is read-only: the game's settings are locked");
            }
            var before = exists ? RecompIni.Decode(File.ReadAllBytes(path)) : "";
            var after = RecompIni.Apply(before, edits, key => Sections.GetValueOrDefault(key));
            if (after == before)
            {
                return;
            }
            var temporary = path + ".mkwmp-tmp";
            File.WriteAllBytes(temporary, RecompIni.Encode(after));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"saving the game's settings: {e.Message}", e);
        }
    }

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

    private static GameSettingsSchema Build(bool deck, bool console, string windowSize)
    {
        var screen = new[]
        {
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
        };

        var graphics = new[]
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

        var sound = new[]
        {
            GameSetting.Range("volume", "Volume", "Master volume of the game.", 100, Live, 0, 100, 5, "%"),
            GameSetting.Choice("audio_channels", "Speakers",
                "Automatic gives the game's 5.1 surround mix when the system's sound output has 6 channels or more, and stereo otherwise.",
                "auto", Restart, O("auto", "Automatic"), O("2", "Stereo"), O("6", "5.1 surround")),
            GameSetting.Toggle("background_mute", "Mute in background", "No sound while the game window is not in front.", false, Live),
        };

        var controls = new[]
        {
            GameSetting.Choice("pad_layout", "Button layout",
                "Which button is A. Nintendo: the right one, as on a Switch. Xbox: the bottom one, as labelled on an Xbox controller (cross on a PlayStation controller). Automatic picks Nintendo for controllers with Nintendo labels and Xbox for the others.",
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

        var general = new[]
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

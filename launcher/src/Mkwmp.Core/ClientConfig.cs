// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Mkwmp.Core;

/// <summary>
/// client.toml: the one human-facing file, shared with the Rust launcher (same place, same keys,
/// same defaults), so a player can go back and forth between the two. It is the window's
/// <see cref="LauncherSettings"/> plus the three voice lists only the game writes (its devices and
/// the players muted from its menus, through the local API).
/// </summary>
internal sealed record ClientConfig
{
    public const string OfficialServer = "https://mkw.anarqz.dev";
    private const string Header = "# mkwmp launcher settings (edited by the launcher window).\n# Guide: https://anarqz.github.io/mkwmp/\n\n";

    /// <summary>What the game keeps of what it is sent (docs/contracts/LAUNCHER_API.md).</summary>
    public const int MaxDevices = 64;
    public const int MaxMutes = 500;

    public LauncherSettings Settings { get; init; } = new();
    public IReadOnlyList<MutedPlayer> Mutes { get; init; } = [];
    public IReadOnlyList<AudioDevice> Inputs { get; init; } = [];
    public IReadOnlyList<AudioDevice> Outputs { get; init; } = [];

    /// <summary>The same name rules as the server: 3-16 of [A-Za-z0-9_-].</summary>
    public static bool ValidName(string name) =>
        name is { Length: >= 3 and <= 16 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool ValidPin(string pin) => pin is { Length: 6 } && pin.All(char.IsAsciiDigit);

    /// <summary>
    /// The server as a base URL: a bare <c>host:port</c> means http (a LAN server), and no
    /// trailing slash, so paths can be appended.
    /// </summary>
    public static string ServerBase(string server)
    {
        var s = server.Trim();
        if (s.Length == 0)
        {
            return ""; // not set: "Set the server URL", not a request to "http://"
        }
        if (!s.Contains("://", StringComparison.Ordinal))
        {
            s = "http://" + s;
        }
        return s.TrimEnd('/');
    }

    /// <summary>The defaults for a first start: SteamOS starts the game from Steam, so no launch there.</summary>
    public static ClientConfig Default() => new() { Settings = new LauncherSettings { Launch = !IsSteamOs() } };

    /// <summary>
    /// SteamOS (the launcher then runs under Proton): Steam says so in the environment, and Wine
    /// shows the host's /etc/os-release as Z:\etc\os-release.
    /// </summary>
    public static bool IsSteamOs()
    {
        if (Environment.GetEnvironmentVariable("SteamDeck") == "1")
        {
            return true;
        }
        try
        {
            // Only under Wine: on Windows, Z: may be a network drive that takes seconds to say it is not there.
            return IsWine() && File.Exists(@"Z:\etc\os-release") && File.ReadAllText(@"Z:\etc\os-release").Contains("ID=steamos", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Wine's ntdll says so itself (<c>wine_get_version</c>).</summary>
    public static bool IsWine() =>
        System.Runtime.InteropServices.NativeLibrary.TryLoad("ntdll.dll", out var ntdll)
        && System.Runtime.InteropServices.NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);

    public static ClientConfig LoadOrDefault(string path) => File.Exists(path) ? Load(path) : Default();

    /// <summary>Reads the file. Keys it does not know are skipped: an older or newer launcher's file still loads.</summary>
    public static ClientConfig Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"read {path}: {e.Message}", e);
        }
        return Parse(text, path);
    }

    public static ClientConfig Parse(string text, string path = "client.toml")
    {
        TomlTable t;
        try
        {
            t = Toml.ToModel(text);
        }
        catch (TomlException e)
        {
            throw new LauncherException($"parse {path}: {e.Message}", e);
        }
        var d = new LauncherSettings();
        var bg = Table(t, "background");
        var voice = Table(t, "voice");
        var ui = Table(t, "ui");
        var settings = new LauncherSettings
        {
            Server = ServerBase(Str(t, "server", d.Server)),
            Name = Str(t, "name", ""),
            Pin = Str(t, "pin", ""),
            Target = Str(t, "target", d.Target),
            Game = Str(t, "game", ""),
            Launch = Bool(t, "launch", true),
            Seat = string.Equals(Str(t, "seat", "start"), "online", StringComparison.OrdinalIgnoreCase) ? SeatMode.Online : SeatMode.Start,
            LocalPort = Int(t, "local_port", d.LocalPort, 1, 65535),
            AutoUpdate = Bool(t, "update", true),
            Slot = Int(t, "slot", 1, 0, 255),
            LaunchCommand = Str(t, "launch_command", ""),
            Patch = Bool(t, "patch", true),
            AdultConsent = Bool(t, "adult_consent", false),
            Background = new BackgroundSettings
            {
                CloseToTray = Bool(bg, "close_to_tray", true),
                StartWithSystem = Bool(bg, "start_with_system", false),
                NotifyOnline = Bool(bg, "notify_online", true),
                NotifyPlaying = Bool(bg, "notify_playing", true),
                NotifyRequests = Bool(bg, "notify_requests", true),
                TrayHintShown = Bool(bg, "tray_hint_shown", false),
            },
            Voice = new VoiceSettings
            {
                Enabled = Bool(voice, "enabled", true),
                Input = Str(voice, "input", ""),
                Output = Str(voice, "output", ""),
                InputGain = Int(voice, "input_gain", 100, 0, 255),
                OutputGain = Int(voice, "output_gain", 100, 0, 255),
                Camera = Str(voice, "camera", ""),
                CameraMirror = Bool(voice, "camera_mirror", true),
                ChatIntroDone = Bool(voice, "chat_intro_done", false),
            },
            Ui = new UiSettings
            {
                Fullscreen = Bool(ui, "fullscreen", true),
                Theme = Str(ui, "theme", "light"),
                Locale = Str(ui, "locale", ""),
                SetupDone = Bool(ui, "setup_done", false),
            },
        };
        return new ClientConfig
        {
            Settings = settings,
            Mutes = [.. Rows(voice, "mutes").Select(r => new MutedPlayer(Str(r, "account", ""), Str(r, "name", ""))).Where(m => m.Account.Length > 0)],
            Inputs = [.. Rows(voice, "inputs").Select(r => new AudioDevice(Str(r, "id", ""), Str(r, "name", ""))).Where(x => x.Id.Length > 0)],
            Outputs = [.. Rows(voice, "outputs").Select(r => new AudioDevice(Str(r, "id", ""), Str(r, "name", ""))).Where(x => x.Id.Length > 0)],
        };
    }

    /// <summary>
    /// Writes the file whole (comments are not kept), through a temporary file and a rename: the
    /// game patch and a second launcher never read half of it.
    /// </summary>
    public void Save(string path)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToToml(), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new LauncherException($"write {path}: {e.Message}", e);
        }
    }

    public string ToToml()
    {
        var s = Settings;
        var b = new StringBuilder(Header);
        Line(b, "server", s.Server);
        Line(b, "name", s.Name);
        Line(b, "pin", s.Pin);
        Line(b, "target", s.Target);
        Line(b, "game", s.Game);
        Line(b, "launch", s.Launch);
        Line(b, "seat", s.Seat == SeatMode.Online ? "online" : "start");
        Line(b, "local_port", s.LocalPort);
        Line(b, "update", s.AutoUpdate);
        Line(b, "slot", s.Slot);
        Line(b, "launch_command", s.LaunchCommand);
        Line(b, "patch", s.Patch);
        Line(b, "adult_consent", s.AdultConsent);

        b.Append("\n[background]\n");
        Line(b, "close_to_tray", s.Background.CloseToTray);
        Line(b, "start_with_system", s.Background.StartWithSystem);
        Line(b, "notify_online", s.Background.NotifyOnline);
        Line(b, "notify_playing", s.Background.NotifyPlaying);
        Line(b, "notify_requests", s.Background.NotifyRequests);
        Line(b, "tray_hint_shown", s.Background.TrayHintShown);

        b.Append("\n[ui]\n");
        Line(b, "fullscreen", s.Ui.Fullscreen);
        Line(b, "theme", s.Ui.Theme);
        Line(b, "locale", s.Ui.Locale);
        Line(b, "setup_done", s.Ui.SetupDone);

        b.Append("\n[voice]\n");
        Line(b, "enabled", s.Voice.Enabled);
        Line(b, "input", s.Voice.Input);
        Line(b, "output", s.Voice.Output);
        Line(b, "input_gain", s.Voice.InputGain);
        Line(b, "output_gain", s.Voice.OutputGain);
        Line(b, "camera", s.Voice.Camera);
        Line(b, "camera_mirror", s.Voice.CameraMirror);
        Line(b, "chat_intro_done", s.Voice.ChatIntroDone);
        // Empty lists inline (what the Rust launcher's serializer writes and reads back); rows after.
        foreach (var (key, empty) in new[] { ("mutes", Mutes.Count == 0), ("inputs", Inputs.Count == 0), ("outputs", Outputs.Count == 0) })
        {
            if (empty)
            {
                b.Append(key).Append(" = []\n");
            }
        }
        foreach (var m in Mutes)
        {
            b.Append("\n[[voice.mutes]]\n");
            Line(b, "account", m.Account);
            Line(b, "name", m.Name);
        }
        foreach (var (key, list) in new[] { ("inputs", Inputs), ("outputs", Outputs) })
        {
            foreach (var x in list)
            {
                b.Append("\n[[voice.").Append(key).Append("]]\n");
                Line(b, "id", x.Id);
                Line(b, "name", x.Name);
            }
        }
        return b.ToString();
    }

    public bool SameAs(ClientConfig other) =>
        Settings == other.Settings && Mutes.SequenceEqual(other.Mutes) && Inputs.SequenceEqual(other.Inputs) && Outputs.SequenceEqual(other.Outputs);

    private static void Line(StringBuilder b, string key, string value) => b.Append(key).Append(" = ").Append(Quote(value)).Append('\n');

    private static void Line(StringBuilder b, string key, bool value) => b.Append(key).Append(" = ").Append(value ? "true" : "false").Append('\n');

    private static void Line(StringBuilder b, string key, int value) => b.Append(key).Append(" = ").Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');

    /// <summary>A TOML basic string.</summary>
    internal static string Quote(string value)
    {
        var b = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7f)
                    {
                        b.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        b.Append(c);
                    }
                    break;
            }
        }
        return b.Append('"').ToString();
    }

    private static TomlTable? Table(TomlTable? t, string key) => t is not null && t.TryGetValue(key, out var v) ? v as TomlTable : null;

    private static string Str(TomlTable? t, string key, string fallback) => t is not null && t.TryGetValue(key, out var v) && v is string s ? s : fallback;

    private static bool Bool(TomlTable? t, string key, bool fallback) => t is not null && t.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    private static int Int(TomlTable? t, string key, int fallback, int min, int max) =>
        t is not null && t.TryGetValue(key, out var v) && v is long n ? (int)Math.Clamp(n, min, max) : fallback;

    private static IEnumerable<TomlTable> Rows(TomlTable? t, string key)
    {
        if (t is null || !t.TryGetValue(key, out var v))
        {
            return [];
        }
        return v switch
        {
            TomlTableArray rows => rows,
            TomlArray inline => inline.OfType<TomlTable>(),
            _ => [],
        };
    }
}

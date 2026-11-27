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
    /// <summary>The project's own server. Not a default any more: a fresh install is offline until the player chooses a server.</summary>
    public const string OfficialServer = "https://mkw.anarqz.dev";
    private const string Header = "# mkwmp launcher settings (edited by the launcher window).\n# Guide: https://anarqz.github.io/mkwmp/\n\n";

    /// <summary>What the game keeps of what it is sent (docs/contracts/LAUNCHER_API.md).</summary>
    public const int MaxDevices = 64;
    public const int MaxMutes = 500;

    public LauncherSettings Settings { get; init; } = new();
    public IReadOnlyList<MutedPlayer> Mutes { get; init; } = [];
    public IReadOnlyList<AudioDevice> Inputs { get; init; } = [];
    public IReadOnlyList<AudioDevice> Outputs { get; init; } = [];

    /// <summary>
    /// The profile on every server this launcher registered or signed in on (<c>[servers."address"]</c>,
    /// the most recently used last): switching servers, or disconnecting, never loses a PIN.
    /// <see cref="LauncherSettings.Pin"/> is the one of the server in use.
    /// </summary>
    public IReadOnlyList<ServerProfile> Servers { get; init; } = [];

    /// <summary>What the file keeps of <see cref="HiddenServers"/>: more than any player hides by hand.</summary>
    public const int MaxHidden = 500;

    /// <summary>
    /// The servers the player took out of the server list on this PC (<c>[[server_list.hidden]]</c>: <c>id</c> is the
    /// server's public key, else its address; <c>name</c> and <c>address</c> are what it was called, for "Hidden
    /// servers"). Local only: nothing of it is sent anywhere.
    /// </summary>
    public IReadOnlyList<HiddenServer> HiddenServers { get; init; } = [];

    /// <summary>
    /// The linked Nextendo account and where PLAY goes online (<c>[nextendo]</c>; docs/contracts/NEXTENDO_API.md §2).
    /// Local only: nothing of it is sent to an MKWMP server. The sign-in itself (the refresh token) is not here: it is
    /// kept protected beside this file (<see cref="Net.NextendoVault"/>).
    /// </summary>
    public NextendoLink Nextendo { get; init; } = NextendoLink.None;

    /// <summary>The same server, however it was typed (scheme, trailing slash, letter case).</summary>
    public static bool SameServer(string a, string b) => string.Equals(ServerBase(a), ServerBase(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The PIN kept for <paramref name="name"/> on <paramref name="server"/>, or "" (a PIN belongs to one name on one server).</summary>
    public string PinFor(string server, string name) =>
        Servers.FirstOrDefault(p => SameServer(p.Server, server) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Pin ?? "";

    /// <summary>Whether the consent was given for <paramref name="name"/> on <paramref name="server"/> (false for a server or a name not seen before).</summary>
    public bool ConsentFor(string server, string name) =>
        Servers.FirstOrDefault(p => SameServer(p.Server, server) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Consent ?? false;

    /// <summary>
    /// The settings' own profile (server + name + PIN, when all three are there) written into
    /// <see cref="Servers"/>, as the last entry. Every load and save goes through it: it is also
    /// how the old single <c>pin</c> becomes the profile of the server it was used on.
    /// </summary>
    public ClientConfig Remembering()
    {
        var s = Settings;
        var server = ServerBase(s.Server);
        if (server.Length == 0 || !ValidName(s.Name) || !ValidPin(s.Pin))
        {
            return this;
        }
        var entry = new ServerProfile(server, s.Name, s.Pin, s.AdultConsent);
        if (Servers.Count > 0 && Servers[^1] == entry)
        {
            return this;
        }
        return this with { Servers = [.. Servers.Where(p => !SameServer(p.Server, server)), entry] };
    }

    /// <summary>Forgets the profile kept for a server (it was deleted there).</summary>
    public ClientConfig Forgetting(string server) => this with { Servers = [.. Servers.Where(p => !SameServer(p.Server, server))] };

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

    /// <summary>
    /// The defaults for a first start: offline (no server), and PLAY starts the game everywhere. On SteamOS too: the
    /// Steam shortcut is the launcher itself (tools/linux/install-mkwmp.sh), and the game runs as its child in the same
    /// Proton prefix. (The Rust launcher ran natively and the game had its own Steam shortcut, hence the old SteamOS
    /// default <c>launch = false</c>; see <see cref="Parse"/> for files that still have it.)
    /// </summary>
    public static ClientConfig Default() => new();

    public static ClientConfig LoadOrDefault(string path) => File.Exists(path) ? Load(path) : Default();

    /// <summary>Reads the file. Keys it does not know are skipped: an older or newer launcher's file still loads.</summary>
    public static ClientConfig Load(string path) => Load(path, Platform.WineHost.Detected);

    /// <param name="wine">The file is read under Wine / Proton: a <c>launch = false</c> the player did not choose is the old SteamOS default.</param>
    internal static ClientConfig Load(string path, bool wine)
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
        return Parse(text, path, wine);
    }

    /// <summary>
    /// <paramref name="wine"/>: the launcher used to write <c>launch = false</c> as its SteamOS default (it only ever
    /// thought it was on SteamOS under Proton), from when the game had a Steam shortcut of its own. Under Proton the
    /// shortcut is the launcher, so that left PLAY with nothing to start: such a value becomes the default again
    /// (<c>true</c>) unless <c>launch_chosen = true</c> says the player set it. Under Wine a <c>false</c> without it is
    /// taken for that old default (the SteamOS check could not even see the host's os-release from inside Steam's
    /// runtime container): a player who had switched "I start the game from Steam" on there before
    /// <c>launch_chosen</c> existed switches it on once more (docs/LINUX.md says so). On Windows it is kept.
    /// </summary>
    public static ClientConfig Parse(string text, string path = "client.toml", bool wine = false)
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
        var dev = Table(t, "dev");
        var chosen = Bool(t, "launch_chosen", false);
        var launch = Bool(t, "launch", true) || (wine && !chosen);
        var settings = new LauncherSettings
        {
            Server = ServerBase(Str(t, "server", "")),
            Name = Str(t, "name", ""),
            Pin = Str(t, "pin", ""),
            Target = Str(t, "target", d.Target),
            Game = Str(t, "game", ""),
            Launch = launch,
            LaunchChosen = chosen,
            RecompUpdateCheckRestored = Bool(t, "recomp_update_check_restored", false),
            RecompStartScreenRestored = Bool(t, "recomp_start_screen_restored", false),
            Seat = string.Equals(Str(t, "seat", "start"), "online", StringComparison.OrdinalIgnoreCase) ? SeatMode.Online : SeatMode.Start,
            LocalPort = Int(t, "local_port", d.LocalPort, 1, 65535),
            AutoUpdate = Bool(t, "update", true),
            Slot = Int(t, "slot", 1, 0, 255),
            LaunchCommand = Str(t, "launch_command", ""),
            Beholder = Str(t, "beholder", "").Trim(),
            ClosestServers = Bool(t, "closest_servers", true),
            Patch = Bool(t, "patch", true),
            DevPackage = Bool(t, "dev_package", false),
            DevRecompExes = Targets.MkwRecomp.Recomp.DevHashList(DevList(dev, "recomp_exe_sha256")),
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
                CameraName = Str(voice, "camera_name", ""),
                CameraMirror = Bool(voice, "camera_mirror", true),
                ChatIntroDone = Bool(voice, "chat_intro_done", false),
            },
            Ui = new UiSettings
            {
                Fullscreen = Bool(ui, "fullscreen", true),
                Theme = Str(ui, "theme", "light"),
                Contrast = Str(ui, "contrast", "normal"),
                Locale = Str(ui, "locale", ""),
                SetupDone = Bool(ui, "setup_done", false),
                OverlayShortcut = Overlay.OverlayShortcuts.Parse(Str(ui, "overlay_shortcut", "")),
            },
            Picker = PickerOf(Table(t, "picker")),
        };
        var servers = new List<ServerProfile>();
        if (Table(t, "servers") is { } table)
        {
            foreach (var (address, value) in table)
            {
                if (value is TomlTable row && ServerBase(address) is { Length: > 0 } server && ValidPin(Str(row, "pin", ""))
                    && !servers.Any(p => SameServer(p.Server, server)))
                {
                    servers.Add(new ServerProfile(server, Str(row, "name", ""), Str(row, "pin", ""), Bool(row, "consent", false)));
                }
            }
        }
        var hidden = new List<HiddenServer>();
        foreach (var row in Rows(Table(t, "server_list"), "hidden"))
        {
            if (Str(row, "id", "").Trim() is { Length: > 0 and <= 255 } id && hidden.Count < MaxHidden && !hidden.Any(h => h.Id == id))
            {
                hidden.Add(new HiddenServer(id, Str(row, "name", ""), Str(row, "address", "")));
            }
        }
        var nx = Table(t, "nextendo");
        return new ClientConfig
        {
            Nextendo = NextendoLink.Of(Str(nx, "play_on", ""), Long(nx, "pid"), Str(nx, "username", ""), Str(nx, "friend_code", "")),
            Servers = servers,
            HiddenServers = hidden,
            Settings = settings,
            Mutes = [.. Rows(voice, "mutes").Select(r => new MutedPlayer(Str(r, "account", ""), Str(r, "name", ""))).Where(m => m.Account.Length > 0)],
            Inputs = [.. Rows(voice, "inputs").Select(r => new AudioDevice(Str(r, "id", ""), Str(r, "name", ""))).Where(x => x.Id.Length > 0)],
            Outputs = [.. Rows(voice, "outputs").Select(r => new AudioDevice(Str(r, "id", ""), Str(r, "name", ""))).Where(x => x.Id.Length > 0)],
        }.Remembering();
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
        if (s.LaunchChosen)
        {
            Line(b, "launch_chosen", true); // only once the player set it (Settings › Game): see Parse
        }
        if (s.RecompUpdateCheckRestored)
        {
            Line(b, "recomp_update_check_restored", true);
        }
        if (s.RecompStartScreenRestored)
        {
            Line(b, "recomp_start_screen_restored", true);
        }
        Line(b, "seat", s.Seat == SeatMode.Online ? "online" : "start");
        Line(b, "local_port", s.LocalPort);
        Line(b, "update", s.AutoUpdate);
        Line(b, "slot", s.Slot);
        Line(b, "launch_command", s.LaunchCommand);
        Line(b, "beholder", s.Beholder);
        Line(b, "closest_servers", s.ClosestServers);
        Line(b, "patch", s.Patch);
        if (s.DevPackage)
        {
            Line(b, "dev_package", true); // a developer's switch: only ever written back, never added
        }
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
        Line(b, "contrast", s.Ui.Contrast);
        Line(b, "locale", s.Ui.Locale);
        Line(b, "setup_done", s.Ui.SetupDone);
        Line(b, "overlay_shortcut", Overlay.OverlayShortcuts.WireName(s.Ui.OverlayShortcut));

        b.Append("\n[voice]\n");
        Line(b, "enabled", s.Voice.Enabled);
        Line(b, "input", s.Voice.Input);
        Line(b, "output", s.Voice.Output);
        Line(b, "input_gain", s.Voice.InputGain);
        Line(b, "output_gain", s.Voice.OutputGain);
        Line(b, "camera", s.Voice.Camera);
        Line(b, "camera_name", s.Voice.CameraName);
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
        // Where the file picker was last used, per purpose (nothing is written until it was used).
        if (s.Picker.All.Count > 0)
        {
            b.Append("\n[picker]\n");
            foreach (var (purpose, folder) in s.Picker.All)
            {
                Line(b, purpose, folder);
            }
        }
        // A developer's switches (written by hand): only ever written back, never added.
        if (s.DevRecompExes.Length > 0)
        {
            b.Append("\n[dev]\n");
            Line(b, "recomp_exe_sha256", s.DevRecompExes);
        }
        // The linked Nextendo account (nothing is written while there is none): never a token.
        if (Nextendo.Linked)
        {
            b.Append("\n[nextendo]\n");
            Line(b, "play_on", Nextendo.PlayOnName);
            b.Append("pid = ").Append(Nextendo.Pid.ToString(CultureInfo.InvariantCulture)).Append('\n');
            Line(b, "username", Nextendo.Username);
            Line(b, "friend_code", Nextendo.FriendCode);
        }
        // The servers hidden from the server list (nothing is written while there are none).
        foreach (var h in HiddenServers)
        {
            b.Append("\n[[server_list.hidden]]\n");
            Line(b, "id", h.Id);
            Line(b, "name", h.Name);
            Line(b, "address", h.Address);
        }
        // The profile on each server (the top-level `pin` stays the one of `server`, as the Rust launcher reads it).
        foreach (var p in Servers)
        {
            b.Append("\n[servers.").Append(Quote(p.Server)).Append("]\n");
            Line(b, "name", p.Name);
            Line(b, "pin", p.Pin);
            Line(b, "consent", p.Consent);
        }
        return b.ToString();
    }

    public bool SameAs(ClientConfig other) =>
        Settings == other.Settings && Mutes.SequenceEqual(other.Mutes) && Inputs.SequenceEqual(other.Inputs) && Outputs.SequenceEqual(other.Outputs)
        && Servers.SequenceEqual(other.Servers) && HiddenServers.SequenceEqual(other.HiddenServers) && Nextendo == other.Nextendo;

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

    /// <summary><c>[picker]</c>: a folder per purpose; anything else in it is skipped.</summary>
    private static Files.PickerFolders PickerOf(TomlTable? table)
    {
        var folders = Files.PickerFolders.Empty;
        if (table is not null)
        {
            foreach (var (purpose, value) in table)
            {
                if (value is string folder)
                {
                    folders = folders.With(purpose, folder);
                }
            }
        }
        return folders;
    }

    /// <summary>A string (<c>"a, b"</c>) or an array of strings, as one comma-separated string; "" when neither.</summary>
    private static string DevList(TomlTable? t, string key) => t is not null && t.TryGetValue(key, out var v)
        ? v switch
        {
            string s => s,
            TomlArray a => string.Join(',', a.OfType<string>()),
            _ => "",
        }
        : "";

    private static TomlTable? Table(TomlTable? t, string key) => t is not null && t.TryGetValue(key, out var v) ? v as TomlTable : null;

    private static string Str(TomlTable? t, string key, string fallback) => t is not null && t.TryGetValue(key, out var v) && v is string s ? s : fallback;

    private static bool Bool(TomlTable? t, string key, bool fallback) => t is not null && t.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    private static int Int(TomlTable? t, string key, int fallback, int min, int max) =>
        t is not null && t.TryGetValue(key, out var v) && v is long n ? (int)Math.Clamp(n, min, max) : fallback;

    private static long Long(TomlTable? t, string key) => t is not null && t.TryGetValue(key, out var v) && v is long n ? n : 0;

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

/// <summary>
/// The profile kept for one server: the gamertag registered there, its PIN, and whether the
/// consent was given for that server (each server has its own policy; one that keeps consent
/// itself says so again at every sign-in).
/// </summary>
internal sealed record ServerProfile(string Server, string Name, string Pin, bool Consent = false);

/// <summary>
/// The Nextendo account linked on this PC, as client.toml keeps it (<c>[nextendo]</c>): who it is, and where PLAY
/// goes online. <see cref="PlayNetwork.Nextendo"/> only counts while an account is linked: without one the choice is
/// <see cref="PlayNetwork.Mkwmp"/>, whatever the file says.
/// </summary>
internal sealed record NextendoLink
{
    public const string MkwmpName = "mkwmp", NextendoName = "nextendo";

    private NextendoLink()
    {
    }

    public static NextendoLink None { get; } = new();

    public long Pid { get; private init; }

    public string Username { get; private init; } = "";

    public string FriendCode { get; private init; } = "";

    public PlayNetwork PlayOn { get; private init; }

    public bool Linked => Pid > 0 && Username.Length > 0;

    /// <summary>The wire name of <see cref="PlayOn"/>: <c>"mkwmp"</c> or <c>"nextendo"</c>.</summary>
    public string PlayOnName => PlayOn == PlayNetwork.Nextendo ? NextendoName : MkwmpName;

    /// <summary>What a file (or an answer of Nextendo's) holds, made consistent: no account, no Nextendo.</summary>
    public static NextendoLink Of(string? playOn, long pid, string? username, string? friendCode)
    {
        var name = Clean(username, 64);
        if (pid <= 0 || name.Length == 0)
        {
            return None;
        }
        return new NextendoLink
        {
            Pid = pid,
            Username = name,
            FriendCode = Clean(friendCode, 32),
            PlayOn = string.Equals(playOn?.Trim(), NextendoName, StringComparison.OrdinalIgnoreCase) ? PlayNetwork.Nextendo : PlayNetwork.Mkwmp,
        };
    }

    /// <summary>The same account, with another choice of network.</summary>
    public NextendoLink Playing(PlayNetwork network) => Linked ? this with { PlayOn = network } : None;

    /// <summary>The same choice, with what Nextendo says of the account now.</summary>
    public NextendoLink Named(long pid, string? username, string? friendCode) => Of(PlayOnName, pid, username, friendCode);

    /// <summary>A name as the session file can hold it: no quotes, backslashes or control characters.</summary>
    private static string Clean(string? text, int most) =>
        new([.. (text ?? "").Trim().Where(c => !char.IsControl(c) && c is not ('"' or '\\')).Take(most)]);
}

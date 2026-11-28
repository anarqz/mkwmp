// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text.Json;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Overlay;

/// <summary>
/// What may be done about a member of the current lobby, for the launcher's own page and the game's overlay alike.
/// </summary>
public static class LobbyRules
{
    /// <summary>Registered and 18+ confirmed: a report may be sent to the server's host (Settings › Online › Report Abuse's own rule).</summary>
    public static bool ReportAllowed(ILauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        return launcher.Settings.AdultConsent && launcher.Profile.SignedIn;
    }

    /// <summary>
    /// Report Abuse of the MKWMP server in use can name this member: they are someone else, met on that very server, by a
    /// player who may report there, under a name that server can know (a gamertag). On Nextendo Network, never: there the
    /// player copies a report for Nextendo's staff (<see cref="LobbyReport"/>).
    /// </summary>
    public static bool CanReportToHost(ILauncher launcher, LobbyView lobby, LobbyMember member)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(member);
        return !member.Local && lobby is { InLobby: true, OnNextendo: false } && launcher.OnlineEnabled && ReportAllowed(launcher)
            && ClientConfig.ValidName(member.Name) && !string.Equals(member.Name, launcher.Settings.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(lobby.Server, Launcher.HostOf(launcher.Settings.Server), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Nextendo Network", or "MKWMP server · mkw.example.net", in the player's language.</summary>
    public static string Network(LobbyView lobby)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        return lobby.OnNextendo ? Strings.T("nextendo.title")
            : lobby.Server.Length > 0 ? Strings.T("lobby.backend.mkwmp") + " · " + lobby.Server : Strings.T("lobby.backend.mkwmp");
    }
}

/// <summary>
/// The in-game overlay (docs/contracts/LAUNCHER_API.md "In-game overlay"), ported from Infinity's v4 overlay: the
/// overlay page is drawn by the MKWMP mod inside the game's own frames while the game keeps running, with the screen,
/// the focus and its full screen. The launcher decides and the mod draws: this class gives the mod the panes' model,
/// already in the player's language, with the theme's colours (GET /local/v1/overlay/view, a long poll on
/// <c>seq</c>), and carries out what the player asks there through <see cref="ILauncher"/>
/// (POST /local/v1/overlay/action). The outcomes are the launcher's own toasts, which the mod draws from
/// GET /local/v1/toasts.
/// <list type="bullet">
/// <item>Game: the running game, where it plays, Back to Game, Quit Game (asked first).</item>
/// <item>Lobby: the players of the CURRENT lobby (<see cref="ILauncher.Lobby"/>, in memory only): their name, the
/// player's own row, a flag on whoever used more than one name in this online session, and Report.</item>
/// </list>
/// While the mod polls (<see cref="GameDraws"/>), the launcher does not put its own window over that game: the
/// shortcut is the mod's. Every member is safe from any thread.
/// </summary>
public sealed class GameOverlay
{
    /// <summary>The version of the view's and the actions' shapes (<c>v</c> in the view).</summary>
    public const int Version = 1;

    /// <summary>The longest GET /local/v1/overlay/view waits for a change.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);

    /// <summary>No poll for this long: the game has no in-game overlay (any more); the launcher's own overlay works again.</summary>
    public static readonly TimeSpan Fallback = TimeSpan.FromSeconds(15);

    /// <summary>An action's answer waits for the launcher this long; after it the answer is 202 and the outcome is a toast.</summary>
    public static readonly TimeSpan ActionWait = TimeSpan.FromSeconds(15);

    /// <summary>The panes the mod may say it shows ("" : it is closed).</summary>
    public static readonly IReadOnlyList<string> Panes = ["game", "lobby"];

    /// <summary>The light theme's colours (Themes/Colors.Light.xaml), by role: what a launcher without a window sends.</summary>
    public static IReadOnlyDictionary<string, string> LightPalette { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["background"] = "#FFEBEBEB", ["surface"] = "#FFFFFFFF", ["surfaceAlt"] = "#FFF6F6F6", ["modal"] = "#FFF0F0F0", ["text"] = "#FF2B2B2B",
        ["textMuted"] = "#FF6C6C6C", ["textFaint"] = "#FFAEAEAE", ["accent"] = "#FF1E64E6", ["accentSoft"] = "#1F1E64E6", ["onAccent"] = "#FFFFFFFF",
        ["rule"] = "#FFC6C6C6", ["bar"] = "#FF2B2B2B", ["good"] = "#FF16A34A", ["warn"] = "#FFD98200", ["bad"] = "#FFE0352B", ["dim"] = "#73000000",
        ["focusFill"] = "#FFFFFFFF", ["track"] = "#FFCFCFCF", ["knob"] = "#FFFFFFFF", ["hover"] = "#0D000000", ["pressed"] = "#1A000000",
        ["glyph"] = "#FF2B2B2B", ["onGlyph"] = "#FFEBEBEB", ["toast"] = "#FFFFFFFF", ["outline"] = "#FF8A8A8A",
    };

    /// <summary>The dark theme's colours (Themes/Colors.Dark.xaml), by role.</summary>
    public static IReadOnlyDictionary<string, string> DarkPalette { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["background"] = "#FF2A2A2D", ["surface"] = "#FF434349", ["surfaceAlt"] = "#FF3A3A3F", ["modal"] = "#FF3E3E44", ["text"] = "#FFF3F3F3",
        ["textMuted"] = "#FFB1B1B6", ["textFaint"] = "#FF737378", ["accent"] = "#FF5CB8FF", ["accentSoft"] = "#2E5CB8FF", ["onAccent"] = "#FF0F1A26",
        ["rule"] = "#FF5E5E64", ["bar"] = "#FFF3F3F3", ["good"] = "#FF3DD27A", ["warn"] = "#FFFFB933", ["bad"] = "#FFFF6B61", ["dim"] = "#99000000",
        ["focusFill"] = "#FF4E4E55", ["track"] = "#FF646469", ["knob"] = "#FFFFFFFF", ["hover"] = "#12FFFFFF", ["pressed"] = "#24FFFFFF",
        ["glyph"] = "#FFF3F3F3", ["onGlyph"] = "#FF2A2A2D", ["toast"] = "#FF4E4E55", ["outline"] = "#FF8E8E94",
    };

    /// <summary>The window's brush keys of the roles (Themes/Colors.*.xaml): the window reads its live theme with them.</summary>
    public static IReadOnlyList<(string Role, string Key)> PaletteKeys { get; } =
    [
        ("background", "Brush.Background"), ("surface", "Brush.Surface"), ("surfaceAlt", "Brush.SurfaceAlt"), ("modal", "Brush.Modal"),
        ("text", "Brush.Text"), ("textMuted", "Brush.TextMuted"), ("textFaint", "Brush.TextFaint"), ("accent", "Brush.Accent"),
        ("accentSoft", "Brush.AccentSoft"), ("onAccent", "Brush.OnAccent"), ("rule", "Brush.Rule"), ("bar", "Brush.Bar"), ("good", "Brush.Good"),
        ("warn", "Brush.Warn"), ("bad", "Brush.Bad"), ("dim", "Brush.Dim"), ("focusFill", "Brush.Focus.Fill"), ("track", "Brush.Track"),
        ("knob", "Brush.Knob"), ("hover", "Brush.Hover"), ("pressed", "Brush.Pressed"), ("glyph", "Brush.Glyph"), ("onGlyph", "Brush.OnGlyph"),
        ("toast", "Brush.Toast"), ("outline", "Brush.Outline"),
    ];

    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private ILauncher? _launcher;
    private Action<Toast>? _notify;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _key = "";
    private long _seq;
    private long? _seenAt;
    private int _waiting;
    private Dictionary<string, string>? _palette;
    private string _pane = "";
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);

    public GameOverlay(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// The launcher it shows (its views) and acts on; <paramref name="notify"/> shows a toast of the launcher's (the outcome of
    /// an action decided here). Once, from the launcher's constructor.
    /// </summary>
    public void Attach(ILauncher launcher, Action<Toast> notify)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(notify);
        lock (_gate)
        {
            _launcher = launcher;
            _notify = notify;
        }
        launcher.Changed += (_, _) => RefreshIfDrawn();
        Strings.Changed += RefreshIfDrawn;
    }

    /// <summary>The launcher is closing: the language's changes are no longer followed.</summary>
    public void Detach() => Strings.Changed -= RefreshIfDrawn;

    /// <summary>A change of the launcher's: the view is built again only while a mod polls (the next poll builds it anyway).</summary>
    private void RefreshIfDrawn()
    {
        lock (_gate)
        {
            if (DrawsLocked)
            {
                RefreshLocked();
            }
        }
    }

    /// <summary>
    /// Puts a text on the clipboard (the window's: the clipboard is the UI thread's); false when it could not. Null (a launcher
    /// without a window): Copy Report is refused in the game.
    /// </summary>
    public Func<string, bool>? CopyText { get; set; }

    /// <summary>
    /// The mod draws the page: it is waiting in a poll now, or asked within <see cref="Fallback"/>. The launcher's own
    /// shortcut watcher and its window over the game then stay off for that game (the shortcut is the mod's).
    /// </summary>
    public bool GameDraws
    {
        get
        {
            lock (_gate)
            {
                return DrawsLocked;
            }
        }
    }

    private bool DrawsLocked => _waiting > 0 || (_seenAt is { } at && _clock.GetElapsedTime(at) < Fallback);

    /// <summary>The overlay is open in the game now (the mod said which pane it shows, and still polls).</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _pane.Length > 0 && DrawsLocked;
            }
        }
    }

    /// <summary>The pane the mod shows ("" while closed).</summary>
    public string Pane
    {
        get
        {
            lock (_gate)
            {
                return _pane;
            }
        }
    }

    public long Seq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary>
    /// The theme's colours by role (<see cref="PaletteKeys"/>), "#AARRGGBB": the window sets its live theme (contrast included);
    /// null: <see cref="LightPalette"/> or <see cref="DarkPalette"/> after the settings' theme.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Palette
    {
        get
        {
            lock (_gate)
            {
                return _palette;
            }
        }
        set
        {
            lock (_gate)
            {
                if (value is not null && _palette is not null && value.Count == _palette.Count
                    && value.All(p => _palette.TryGetValue(p.Key, out var v) && v == p.Value))
                {
                    return;
                }
                _palette = value is null ? null : new Dictionary<string, string>(value, StringComparer.Ordinal);
                RefreshLocked();
            }
        }
    }

    /// <summary>Builds the view again; a change moves <c>seq</c> on and answers the waiting poll.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            RefreshLocked();
        }
    }

    /// <summary>The answer GET /local/v1/overlay/view gives now (the harness writes it as the mod's test fixture).</summary>
    public string Answer()
    {
        lock (_gate)
        {
            RefreshLocked();
            return AnswerLocked();
        }
    }

    private string AnswerLocked() => "{\"seq\":" + _seq.ToString(CultureInfo.InvariantCulture) + (_key.Length > 2 ? "," + _key[1..] : "}");

    private void RefreshLocked()
    {
        if (_launcher is not { } launcher)
        {
            return;
        }
        var key = Build(launcher);
        if (key != _key)
        {
            _key = key;
            _seq++;
            var done = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            done.TrySetResult();
        }
    }

    // ---- the view ---------------------------------------------------------------------------

    private static string T(string key) => Strings.T(key);

    private static string F(string key, params (string Name, string? Value)[] args) => Strings.F(key, args);

    private static string N(long n) => Strings.N(n);

    /// <summary>What a disc says, as the launcher's Avatar: one or two letters ("KartFan" is "KF", "luna" is "L", "Ana Lu" is "AL"); "?" without a name.</summary>
    internal static string InitialsOf(string name)
    {
        var parts = (name ?? "").Trim().Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => First(parts[0]) + Second(parts[0]),
            _ => First(parts[0]) + First(parts[^1]),
        };

        static string First(string s) => StringInfo.GetNextTextElement(s).ToUpperInvariant();

        static string Second(string s)
        {
            for (var i = 1; i < s.Length; i++)
            {
                if (char.IsUpper(s[i]) || char.IsDigit(s[i]))
                {
                    return s[i].ToString();
                }
            }
            return "";
        }
    }

    /// <summary>The disc's colour, "#AARRGGBB", as the launcher's Avatar draws it: one of ten by the name's FNV-1a hash (the same on every run and machine).</summary>
    internal static string DiscOf(string name)
    {
        uint[] discs = [0xFF2F6BFF, 0xFF7A4DFF, 0xFFE2498A, 0xFFF0443A, 0xFFF08A1A, 0xFF12A86B, 0xFF0E9DB8, 0xFF5B5BD6, 0xFFC0399F, 0xFF4E8F2D];
        var hash = 2166136261u;
        foreach (var c in (name ?? "").Trim())
        {
            hash = (hash ^ c) * 16777619u;
        }
        return "#" + discs[hash % (uint)discs.Length].ToString("X8", CultureInfo.InvariantCulture);
    }
    /// <summary>The whole view but <c>seq</c>, as JSON (what decides "changed").</summary>
    private string Build(ILauncher l)
    {
        var lobby = l.Lobby;
        var view = new
        {
            v = Version,
            shortcut = l.Settings.Ui.OverlayShortcut.WireName(),
            clock = _clock.GetLocalNow().ToString("t", Strings.Culture.Name.Length > 0 ? Strings.Culture : CultureInfo.CurrentCulture),
            status = (object?)null, // reserved (Infinity: the header's network and battery); only the clock is drawn
            start = lobby.InLobby ? "lobby" : "game", // the pane the page opens on: in a lobby, its players right away
            palette = _palette ?? (string.Equals(l.Settings.Ui.Theme, "dark", StringComparison.OrdinalIgnoreCase) ? DarkPalette : LightPalette),
            text = new
            {
                title = "MKWMP",
                ok = T("hint.ok"),
                back = T("hint.back"),
                backToGame = T("home.overlay.back"),
                close = T("common.close"),
                cancel = T("common.cancel"),
                select = T("picker.hint.select"),
            },
            entries = new[]
            {
                new { key = "game", title = T("home.game"), icon = "gamepad", separator = false, locked = false, soon = false },
                new { key = "lobby", title = T("lobby.title"), icon = "friends", separator = false, locked = false, soon = false },
            },
            busy = _busy.Order(StringComparer.Ordinal).ToList(),
            game = GameView(l),
            lobby = LobbyPane(l, lobby),
            report = ReportView(l, lobby),
        };
        return PlainJson.Serialize(view);
    }

    private static object GameView(ILauncher l)
    {
        var nextendo = l.Nextendo.Active;
        var online = nextendo || l.OnlineEnabled;
        return new
        {
            name = "MKW",
            status = nextendo ? T("settings.online.nextendo") : l.OnlineEnabled ? Launcher.HostOf(l.Settings.Server) : T("home.offline"),
            backToGame = T("home.overlay.back"),
            quit = new
            {
                label = T("home.overlay.quit"),
                title = T("quitgame.title"),
                body = online && !nextendo ? T("quitgame.body") + " " + T("quitgame.seat") : T("quitgame.body"),
                yes = T("quitgame.yes"),
                no = T("common.cancel"),
            },
        };
    }

    private static object LobbyPane(ILauncher l, LobbyView lobby)
    {
        var players = lobby.Members.Select(m => new
        {
            id = m.Id,
            name = m.Name.Length > 0 ? m.Name : T("lobby.no_name"),
            initials = InitialsOf(m.Name),
            disc = DiscOf(m.Name),
            you = m.Local,
            renamed = m.Renamed,
            renamedLabel = m.Renamed ? F("lobby.used_names", ("n", N(m.Names.Count))) : "",
            renamedBody = m.Renamed ? F("lobby.player.warn", ("n", N(m.Names.Count))) : "",
            names = m.Names.Select(n => n.Name).ToList(),
            playerId = m.Uid.Length <= 128 ? m.Uid : m.Uid[..128],
            canReport = LobbyRules.CanReportToHost(l, lobby, m),
            canCopyReport = !m.Local,
        }).ToList();
        return new
        {
            title = T("lobby.title"),
            subtitle = lobby.InLobby ? F("lobby.players", ("n", N(lobby.Members.Count))) + " · " + LobbyRules.Network(lobby) : "",
            inLobby = lobby.InLobby,
            network = lobby.OnNextendo ? "nextendo" : "mkwmp",
            note = lobby.InLobby ? "" : T("lobby.empty"), // out of a lobby: the pane's one line
            you = T("lobby.you"),
            players,
            player = new
            {
                namesHeader = T("lobby.player.names"), // "NAMES · {n}": the mod puts the count in
                idLabel = T("lobby.player.id"),
                idCaption = T("lobby.player.id_caption"),
                report = T("settings.chat.report"),
                copyReport = T("lobby.report"),
                copyReportCaption = T(lobby.OnNextendo ? "lobby.report.caption.nextendo" : "lobby.report.caption.mkwmp"),
                no = T("common.cancel"),
            },
        };
    }

    /// <summary>
    /// Report to the server's host (Settings › Report Abuse's reasons): null on Nextendo Network and offline. Enabled when a
    /// report may be sent now; else <c>why</c> says why (not registered, or one is already waiting for review).
    /// </summary>
    private static object? ReportView(ILauncher l, LobbyView lobby)
    {
        if (lobby.OnNextendo || !l.OnlineEnabled || l.Report is not { } r)
        {
            return null;
        }
        var why = !LobbyRules.ReportAllowed(l) ? T("settings.report.gate")
            : r.Open is { } open ? F("settings.report.waiting", ("name", open.Reported), ("reason", r.Reasons.FirstOrDefault(x => x.Code == open.Reason)?.Label ?? open.Reason))
            : r.Loaded != true ? (r.Busy || r.Loaded is null ? Strings.ReportChecking : Strings.ReportCantCheck)
            : "";
        return new
        {
            label = T("settings.chat.report"),
            caption = T("settings.report.caption"),
            title = T("settings.report.title"),
            what = T("settings.report.what"),
            send = T("settings.report.send"),
            no = T("common.cancel"),
            enabled = why.Length == 0 && !r.Busy && r.Reasons.Count > 0,
            why,
            reasons = r.Reasons.Select(x => new { code = x.Code, label = x.Label }).ToList(),
        };
    }

    // ---- the routes (LocalApi) ------------------------------------------------------------

    /// <summary>GET /local/v1/overlay/view[?wait=S&amp;seq=N]: now, or when the view is no longer N (at most S seconds later).</summary>
    internal async Task<LocalResponse> ViewAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancel)
    {
        var wait = TimeSpan.Zero;
        if (query.TryGetValue("wait", out var rawWait))
        {
            if (!int.TryParse(rawWait, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return Invalid("wait: seconds");
            }
            wait = TimeSpan.FromSeconds(Math.Min(seconds, MaxWait.TotalSeconds));
        }
        long? known = null;
        if (query.TryGetValue("seq", out var rawSeq))
        {
            if (!long.TryParse(rawSeq, NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
            {
                return Invalid("seq: a number");
            }
            known = seq;
        }
        Task changed;
        TimeSpan until;
        lock (_gate)
        {
            _seenAt = _clock.GetTimestamp();
            if (_launcher is null)
            {
                return Unavailable();
            }
            RefreshLocked();
            if (wait == TimeSpan.Zero || known != _seq)
            {
                return new LocalResponse(200, AnswerLocked());
            }
            changed = _changed.Task;
            // the clock on the page changes at the next minute: the answer comes then at the latest
            var local = _clock.GetLocalNow();
            var minute = TimeSpan.FromSeconds(60 - local.Second) - TimeSpan.FromMilliseconds(local.Millisecond) + TimeSpan.FromMilliseconds(50);
            until = minute < wait ? minute : wait;
            _waiting++;
        }
        try
        {
            await changed.WaitAsync(until, _clock, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // nothing changed (or only the clock): the state again
        }
        finally
        {
            lock (_gate)
            {
                _waiting--;
                _seenAt = _clock.GetTimestamp();
            }
        }
        lock (_gate)
        {
            RefreshLocked();
            return new LocalResponse(200, AnswerLocked());
        }
    }

    /// <summary>
    /// POST /local/v1/overlay/action with <c>{"action": "...", "args": {...}}</c> (LAUNCHER_API.md "In-game overlay"): 200
    /// <c>{"ok":true}</c> when the launcher did it (its toast says how it went), 202 when it is still at it after
    /// <see cref="ActionWait"/>; 409 when it cannot be done now (the message says why, in the player's language); 404 when
    /// what it names is not there (any more); 400 when it is not an action.
    /// </summary>
    internal async Task<LocalResponse> ActionAsync(ReadOnlyMemory<byte> body, CancellationToken cancel)
    {
        if (Json.Parse(body.Span) is not { } parsed)
        {
            return Invalid("not JSON");
        }
        JsonElement? root = parsed;
        var action = root.At("action").Str();
        var args = root.At("args");
        if (args is { } a && a.ValueKind != JsonValueKind.Object)
        {
            return Invalid("args: an object");
        }
        string Arg(string key) => LocalApi.Text(args.At(key).Str() ?? "").Trim();
        ILauncher l;
        lock (_gate)
        {
            _seenAt = _clock.GetTimestamp();
            if (_launcher is null)
            {
                return Unavailable();
            }
            l = _launcher;
        }
        var lobby = l.Lobby;
        switch (action)
        {
            case "show":
            {
                var pane = Arg("pane");
                if (pane.Length > 0 && !Panes.Contains(pane))
                {
                    return Invalid("pane: " + string.Join(", ", Panes) + " or \"\"");
                }
                lock (_gate)
                {
                    _pane = pane;
                    RefreshLocked();
                }
                return Ok();
            }
            case "report":
            {
                // the player's Report (the mod asks first: the reason, then Send Report); one report waits at a time
                if (lobby.Member(Arg("id")) is not { Local: false } member)
                {
                    return Gone("no such player in the lobby");
                }
                if (lobby.OnNextendo || !l.OnlineEnabled)
                {
                    return Refused(T("lobby.report.caption.nextendo"));
                }
                if (!LobbyRules.ReportAllowed(l))
                {
                    return Refused(T("settings.report.gate"));
                }
                if (!LobbyRules.CanReportToHost(l, lobby, member))
                {
                    return Refused(T("lobby.report.caption.mkwmp"));
                }
                var reason = Arg("reason");
                if (l.Report.Reasons.All(r => r.Code != reason))
                {
                    return Invalid("reason: one of the view's report.reasons");
                }
                if (l.Report.Open is not null)
                {
                    return Refused(T("settings.report.one"));
                }
                return await RunAsync("report", () => l.SendReportAsync(member.Name, reason, LobbyReport.Brief(member, Strings.ReportDetailsMax)), cancel).ConfigureAwait(false);
            }
            case "copyReport":
            {
                // a text for Nextendo's staff or the server's host: it goes to the clipboard and nowhere else
                if (lobby.Member(Arg("id")) is not { Local: false } member)
                {
                    return Gone("no such player in the lobby");
                }
                if (CopyText is not { } copy || !copy(LobbyReport.Text(lobby, member, l.Version, _clock.GetUtcNow())))
                {
                    Notify(new Toast(T("lobby.copy_failed"), "", ToastKind.Error) { Always = true });
                    return Refused(T("lobby.copy_failed"));
                }
                Notify(new Toast(T("lobby.report.copied"), lobby.OnNextendo
                    ? T("lobby.report.copied.nextendo")
                    : F("lobby.report.copied.mkwmp", ("server", lobby.Server.Length > 0 ? lobby.Server : T("lobby.backend.mkwmp"))), ToastKind.Success) { Always = true });
                return Ok();
            }
            case "quitGame":
                // the mod asked first and ends the game itself through the runtime (its quit entry) once this answers
                return l.Session.GameRunning || DrawsNow() ? Ok() : Gone("no game runs");
            default:
                return Invalid("action: show, report, copyReport or quitGame");
        }
    }

    private bool DrawsNow()
    {
        lock (_gate)
        {
            return DrawsLocked;
        }
    }

    private void Notify(Toast toast)
    {
        Action<Toast>? notify;
        lock (_gate)
        {
            notify = _notify;
        }
        notify?.Invoke(toast);
    }

    /// <summary>
    /// Runs one of the launcher's actions (marked busy meanwhile: the mod greys its button); its answer once it is done, or 202
    /// after <see cref="ActionWait"/>. The same action twice at once: the second is refused.
    /// </summary>
    private async Task<LocalResponse> RunAsync(string name, Func<Task> work, CancellationToken cancel)
    {
        lock (_gate)
        {
            if (!_busy.Add(name))
            {
                return Refused("busy");
            }
            RefreshLocked();
        }
        var task = Task.Run(work, CancellationToken.None);
        _ = task.ContinueWith(_ =>
        {
            lock (_gate)
            {
                _busy.Remove(name);
                RefreshLocked();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await task.WaitAsync(ActionWait, cancel).ConfigureAwait(false);
            return Ok();
        }
        catch (TimeoutException)
        {
            return new LocalResponse(202, """{"state":"pending"}""");
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return new LocalResponse(500, JsonSerializer.Serialize(new { state = "error", message = e.Message }));
        }
    }

    private static LocalResponse Ok() => new(200, """{"ok":true}""");

    private static LocalResponse Unavailable() => new(503, JsonSerializer.Serialize(new { state = "unavailable", message = "the launcher is starting" }));

    private static LocalResponse Refused(string message) => new(409, PlainJson.Serialize(new { state = "refused", message }));

    private static LocalResponse Gone(string message) => new(404, JsonSerializer.Serialize(new { state = "gone", message }));

    private static LocalResponse Invalid(string message) => new(400, JsonSerializer.Serialize(new { state = "invalid", message }));
}

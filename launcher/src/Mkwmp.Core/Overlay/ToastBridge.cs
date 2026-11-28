// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text.Json;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Overlay;

/// <summary>
/// One toast as the game draws it (GET /local/v1/toasts): what the launcher's ToastHost shows, in its order. Times
/// are on <see cref="ToastBridge.NowMs"/>'s clock; the answer turns them into "ms from now".
/// </summary>
/// <param name="Id">"t&lt;n&gt;", once per launcher run.</param>
/// <param name="LeavesAtMs">When it starts to go (its stay is over).</param>
/// <param name="StayMs">Its whole time on screen.</param>
public sealed record GameToast(string Id, ToastKind Kind, string Title, string Body, long LeavesAtMs, long StayMs);

/// <summary>Everything the game draws: the toasts in ToastHost's order, and the colours.</summary>
/// <param name="Palette">The theme's colours by role (toast, rule, accent, good, warn, bad, text, textMuted, track, glyph, onGlyph, onAccent, surface), "#AARRGGBB".</param>
public sealed record GameToastSnapshot(IReadOnlyList<GameToast> Toasts, IReadOnlyDictionary<string, string> Palette)
{
    /// <summary>The dark theme's colours (Themes/Colors.Dark.xaml): what a launcher without a window shows.</summary>
    public static IReadOnlyDictionary<string, string> DarkPalette { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["toast"] = "#FF4E4E55", ["rule"] = "#FF5E5E64", ["accent"] = "#FF5CB8FF", ["good"] = "#FF3DD27A", ["warn"] = "#FFFFB933",
        ["bad"] = "#FFFF6B61", ["text"] = "#FFF3F3F3", ["textMuted"] = "#FFB1B1B6", ["track"] = "#FF646469", ["glyph"] = "#FFF3F3F3",
        ["onGlyph"] = "#FF2A2A2D", ["onAccent"] = "#FF0F1A26", ["surface"] = "#FF434349",
    };

    /// <summary>Nothing to draw (after the dark palette: static fields start in order).</summary>
    public static GameToastSnapshot Empty { get; } = new([], DarkPalette);
}

/// <summary>What the game asks of a toast (POST /local/v1/toasts/{id}/action).</summary>
public enum GameToastAction
{
    /// <summary>Reserved (Infinity's invite toasts): no toast of this launcher is accepted; always refused.</summary>
    Accept,

    /// <summary>Reserved, as <see cref="Accept"/>.</summary>
    Decline,

    /// <summary>Take it off the screen now.</summary>
    Dismiss,
}

/// <summary>One action the game asked for, for the window to carry out.</summary>
public sealed record GameToastRequest(string Id, GameToastAction Action);

/// <summary>
/// The launcher's toasts drawn by the game (the MKWMP mod, docs/contracts/LAUNCHER_API.md "In-game toasts"): the
/// launcher decides (which toasts, their order, their times, their words and colours); the mod only draws them in the
/// game's own frame, at the top right, without the focus ever moving. Ported from Infinity's v4 overlay.
/// <list type="bullet">
/// <item>The window publishes what its ToastHost shows while the launcher's window is behind the game (<see cref="Publish"/>), an
/// empty list otherwise. A launcher without a window (the overlay host) has the bridge show the core's own toasts instead
/// (<see cref="Offer"/>), with the same kinds and times.</item>
/// <item>Every change of what is shown moves <c>seq</c> on and ends the mod's long poll.</item>
/// <item>The mod's polls are the sign that the game draws them (<see cref="GameDraws"/>): the window over the game is then not
/// shown; when no poll came for <see cref="Fallback"/> it is again.</item>
/// </list>
/// Every member is safe from any thread; <see cref="Requested"/> fires on the local API's thread.
/// </summary>
public sealed class ToastBridge
{
    /// <summary>The longest GET /local/v1/toasts waits for a change.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);

    /// <summary>No poll for this long: the game does not draw the toasts (any more), the window over the game shows them.</summary>
    public static readonly TimeSpan Fallback = TimeSpan.FromSeconds(15);

    /// <summary>At most this many toasts of the bridge's own (as the window's ToastHost).</summary>
    public const int MaxOwn = 4;

    private static readonly string[] NoKeys = [];

    private readonly TimeProvider _clock;
    private readonly long _epoch;
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GameToastSnapshot _shown = GameToastSnapshot.Empty;
    private string _key = "";
    private long _seq;
    private long? _seenAt;
    private int _waiting;
    private bool _window;
    private readonly List<GameToast> _own = [];
    private long _ownNext;

    public ToastBridge(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _epoch = clock.GetTimestamp();
        _key = Key(_shown);
    }

    /// <summary>The game asked for an action on a toast it shows (a dismiss), already checked against what is shown.</summary>
    public event EventHandler<GameToastRequest>? Requested;

    /// <summary>The bridge's clock, in milliseconds: <see cref="GameToast.LeavesAtMs"/> is on it.</summary>
    public long NowMs => (long)_clock.GetElapsedTime(_epoch).TotalMilliseconds;

    /// <summary>
    /// The game draws the toasts: it is waiting in a poll now, or asked within <see cref="Fallback"/>. The window
    /// over the game then stays hidden.
    /// </summary>
    public bool GameDraws
    {
        get
        {
            lock (_gate)
            {
                return _waiting > 0 || (_seenAt is { } at && _clock.GetElapsedTime(at) < Fallback);
            }
        }
    }

    /// <summary>What the game is shown now (a copy of the last <see cref="Publish"/>).</summary>
    public GameToastSnapshot Shown
    {
        get
        {
            lock (_gate)
            {
                return _shown;
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

    /// <summary>The window shows the toasts from now on and publishes them itself: the bridge's own toasts (<see cref="Offer"/>) stop.</summary>
    public void AttachWindow()
    {
        lock (_gate)
        {
            _window = true;
            _own.Clear();
        }
    }

    /// <summary>
    /// What the game draws from now on (the window: its ToastHost's list while it is behind the game, else none). A
    /// snapshot equal to the last one changes nothing; any other moves <c>seq</c> on and answers the waiting poll.
    /// </summary>
    public void Publish(GameToastSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var key = Key(snapshot);
        lock (_gate)
        {
            if (key == _key)
            {
                return;
            }
            _shown = snapshot;
            _key = key;
            Bump();
        }
    }

    /// <summary>
    /// A toast of the core, for a launcher without a window (the overlay host): shown by the game as the window would
    /// (at most <see cref="MaxOwn"/>, the oldest goes; its kind's time). Nothing once a window attached.
    /// </summary>
    public void Offer(Toast toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        lock (_gate)
        {
            if (_window)
            {
                return;
            }
            var now = NowMs;
            _own.RemoveAll(t => t.LeavesAtMs <= now);
            var stay = (long)Toast.StayFor(toast.Kind, toast.Always).TotalMilliseconds;
            _own.Add(new GameToast("t" + (++_ownNext).ToString(CultureInfo.InvariantCulture), toast.Kind, toast.Title, toast.Body, now + stay, stay));
            while (_own.Count > MaxOwn)
            {
                _own.RemoveAt(0);
            }
            PublishOwn();
        }
    }

    /// <summary>Under the gate: the bridge's own toasts as the snapshot (those whose time is over are gone).</summary>
    private void PublishOwn()
    {
        var now = NowMs;
        _own.RemoveAll(t => t.LeavesAtMs <= now);
        var snapshot = _shown with { Toasts = [.. _own] };
        var key = Key(snapshot);
        if (key != _key)
        {
            _shown = snapshot;
            _key = key;
            Bump();
        }
    }

    /// <summary>Under the gate: a new state for the waiting poll.</summary>
    private void Bump()
    {
        _seq++;
        var done = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.TrySetResult();
    }

    /// <summary>What decides "changed": everything but the clock (times are absolute here).</summary>
    private static string Key(GameToastSnapshot s) => JsonSerializer.Serialize(new
    {
        toasts = s.Toasts.Select(t => new { t.Id, t.Kind, t.Title, t.Body, t.LeavesAtMs, t.StayMs }),
        palette = s.Palette.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value),
    });

    /// <summary>The wire name of a kind: info, success, warning, error or friend.</summary>
    public static string KindName(ToastKind kind) => kind switch
    {
        ToastKind.Success => "success",
        ToastKind.Warning => "warning",
        ToastKind.Error => "error",
        ToastKind.FriendOnline or ToastKind.FriendPlaying or ToastKind.FriendRequest => "friend",
        _ => "info",
    };

    /// <summary>The icon of a kind (the launcher's Icon.* geometries: info, check, warning, error, user, play, userPlus).</summary>
    public static string IconName(ToastKind kind) => kind switch
    {
        ToastKind.Success => "check",
        ToastKind.Warning => "warning",
        ToastKind.Error => "error",
        ToastKind.FriendOnline => "user",
        ToastKind.FriendPlaying => "play",
        ToastKind.FriendRequest => "userPlus",
        _ => "info",
    };

    /// <summary>The answer GET /local/v1/toasts gives now (the harness writes it as the mod's test fixture).</summary>
    public string Answer()
    {
        lock (_gate)
        {
            return AnswerJson();
        }
    }

    /// <summary>Under the gate: the answer, its times from now.</summary>
    private string AnswerJson()
    {
        if (!_window)
        {
            PublishOwn(); // a toast of the bridge's own whose time is over is gone (and seq moved on)
        }
        var now = NowMs;
        var s = _shown;
        return PlainJson.Serialize(new
        {
            v = 1,
            seq = _seq,
            toasts = s.Toasts.Select(t => new
            {
                id = t.Id,
                kind = KindName(t.Kind),
                title = t.Title,
                body = t.Body,
                icon = IconName(t.Kind),
                expiresInMs = Math.Max(0, t.LeavesAtMs - now),
                stayMs = t.StayMs,
            }),
            suppress = NoKeys, // reserved (Infinity: the keys a hold-to-accept toast keeps from the game); always empty here
            palette = s.Palette,
        });
    }

    // ---- the routes (LocalApi) ---------------------------------------------------------------

    /// <summary>GET /local/v1/toasts[?wait=S&amp;seq=N]: now, or when what is shown is no longer N (at most S seconds later).</summary>
    internal async Task<LocalResponse> StateAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancel)
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
            if (!_window)
            {
                PublishOwn();
            }
            if (wait == TimeSpan.Zero || known != _seq)
            {
                return new LocalResponse(200, AnswerJson());
            }
            changed = _changed.Task;
            until = wait;
            // a toast of the bridge's own that goes before the wait ends: the answer comes then (its time is the bridge's to keep)
            if (!_window && _own.Count > 0)
            {
                var first = TimeSpan.FromMilliseconds(Math.Max(0, _own.Min(t => t.LeavesAtMs) - NowMs) + 1);
                until = first < until ? first : until;
            }
            _waiting++;
        }
        try
        {
            await changed.WaitAsync(until, _clock, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // nothing changed: the same state again (the bridge's own may have gone meanwhile)
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
            return new LocalResponse(200, AnswerJson());
        }
    }

    /// <summary>
    /// POST /local/v1/toasts/{id}/action with <c>{"action":"accept"|"decline"|"dismiss"}</c>. 200 <c>{"ok":true}</c> when it goes to
    /// the launcher; 404 when no such toast is shown (it went meanwhile); 409 when it cannot do that (accept and decline: this
    /// launcher has no toast to answer).
    /// </summary>
    internal LocalResponse Action(string id, ReadOnlyMemory<byte> body)
    {
        if (Json.Parse(body.Span) is not { } parsed)
        {
            return Invalid("not JSON");
        }
        GameToastAction? action = ((JsonElement?)parsed).At("action").Str() switch
        {
            "accept" => GameToastAction.Accept,
            "decline" => GameToastAction.Decline,
            "dismiss" => GameToastAction.Dismiss,
            _ => null,
        };
        if (action is not { } what)
        {
            return Invalid("action: accept, decline or dismiss");
        }
        lock (_gate)
        {
            _seenAt = _clock.GetTimestamp();
            if (!_shown.Toasts.Any(t => t.Id == id))
            {
                return new LocalResponse(404, JsonSerializer.Serialize(new { state = "gone", message = "no such toast is shown" }));
            }
            if (what != GameToastAction.Dismiss)
            {
                return new LocalResponse(409, JsonSerializer.Serialize(new { state = "refused", message = "this toast is only dismissed" }));
            }
            if (!_window)
            {
                _own.RemoveAll(t => t.Id == id);
                PublishOwn();
                return new LocalResponse(200, """{"ok":true}""");
            }
        }
        Requested?.Invoke(this, new GameToastRequest(id, what));
        return new LocalResponse(200, """{"ok":true}""");
    }

    private static LocalResponse Invalid(string message) =>
        new(400, JsonSerializer.Serialize(new { state = "invalid", message }));
}

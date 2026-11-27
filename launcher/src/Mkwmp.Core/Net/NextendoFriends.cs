// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text.Json;

namespace Mkwmp.Core.Net;

/// <summary>What <c>GET /api/friends</c> answers: the friends, the requests received, the requests sent.</summary>
internal sealed record NextendoFriendList(IReadOnlyList<NextendoFriend> Friends, IReadOnlyList<NextendoPerson> Incoming, IReadOnlyList<NextendoPerson> Sent)
{
    /// <summary>The most a list keeps (Nextendo's own limit is 300 friends).</summary>
    public const int Most = 500;

    /// <summary>
    /// <c>{friends:[…], requests:[…], sent:[…]}</c>; null when it is not that. <c>sent</c> may be missing (an older
    /// server): none then. Entries without a usable <c>pid</c> are skipped, and nobody is listed twice.
    /// </summary>
    public static NextendoFriendList? Parse(JsonElement? v)
    {
        if (v.At("friends") is not { ValueKind: JsonValueKind.Array } friends)
        {
            return null;
        }
        var list = new List<NextendoFriend>();
        var seen = new HashSet<long>();
        foreach (var entry in friends.EnumerateArray())
        {
            if (Friend(entry) is { } friend && seen.Add(friend.Pid) && list.Count < Most)
            {
                list.Add(friend);
            }
        }
        return new NextendoFriendList(list, People(v, "requests") ?? [], People(v, "sent") ?? []);
    }

    /// <summary>The array under <paramref name="key"/> as people (an entry may be an object, or a bare pid); null when there is no such array.</summary>
    public static IReadOnlyList<NextendoPerson>? People(JsonElement? v, string key)
    {
        if (v.At(key) is not { ValueKind: JsonValueKind.Array } entries)
        {
            return null;
        }
        var people = new List<NextendoPerson>();
        var seen = new HashSet<long>();
        foreach (var entry in entries.EnumerateArray())
        {
            var person = entry.ValueKind == JsonValueKind.Object ? Person(entry)
                : NextendoApi.Id(entry) is { } pid and > 0 ? new NextendoPerson(pid, "", "")
                : null;
            if (person is not null && seen.Add(person.Pid) && people.Count < Most)
            {
                people.Add(person);
            }
        }
        return people;
    }

    public static NextendoPerson? Person(JsonElement? v) =>
        NextendoApi.Id(v.At("pid")) is { } pid and > 0 ? new NextendoPerson(pid, NameOf(v), Clean(v.At("friend_code").Str(), 32)) : null;

    private static string NameOf(JsonElement? v) =>
        Clean(v.At("name").Str(), 64) is { Length: > 0 } shown ? shown : Clean(v.At("username").Str(), 64);

    /// <summary>Where a presence may name the game (the live API and the older server differ).</summary>
    private static readonly string[] TitleKeys = ["title", "app_name", "game", "name"];

    public static NextendoFriend? Friend(JsonElement? v)
    {
        if (Person(v) is not { } who)
        {
            return null;
        }
        var banned = v.At("banned").Bool() ?? false;
        var presence = v.At("presence");
        var state = banned ? FriendState.Offline : (presence.At("status").Long() ?? 0) switch
        {
            >= 2 => FriendState.Playing,
            1 => FriendState.Online,
            _ => FriendState.Offline,
        };
        var title = TitleKeys.Select(k => presence.At(k).Str()).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var playing = state == FriendState.Playing;
        return new NextendoFriend(who.Pid, who.Name, who.FriendCode, state,
            playing ? GameName(presence.At("app_id").Str() ?? presence.At("title_id").Str(), title) : "",
            playing ? Clean(presence.At("app_detail").Str() ?? presence.At("detail").Str(), 64) : "",
            v.At("favorite").Bool() ?? false, banned);
    }

    /// <summary>
    /// What a friend plays, as the window says it: "MKW" for the game (its title id, or its title), another game's
    /// own title as Nextendo gives it, "" when it gives none. The game's full name and its maker's are never shown.
    /// </summary>
    internal static string GameName(string? appId, string? title)
    {
        var name = Clean(title, 48);
        if (string.Equals(appId?.Trim(), NextendoPresence.MkwTitleId, StringComparison.OrdinalIgnoreCase)
            || name.Contains("Kart World", StringComparison.OrdinalIgnoreCase))
        {
            return "MKW";
        }
        return name.Contains("Nintendo", StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    /// <summary>A text of someone else's as one short line: no control characters, no more than <paramref name="most"/>.</summary>
    internal static string Clean(string? text, int most)
    {
        var kept = new string([.. (text ?? "").Where(c => !char.IsControl(c)).Take(most)]);
        // never half of a character that takes two
        return (kept.Length > 0 && char.IsHighSurrogate(kept[^1]) ? kept[..^1] : kept).Trim();
    }
}

internal enum NextendoFriendEventKind
{
    /// <summary>A friend who was offline is online.</summary>
    Online,

    /// <summary>A friend started playing (<see cref="NextendoFriendEvent.Game"/> says what, when known).</summary>
    Playing,

    /// <summary>Someone new asks to be a friend.</summary>
    Request,

    /// <summary>Someone the player asked is a friend now.</summary>
    Accepted,

    /// <summary>Counts instead of names: the first list after the start, or many friends at once.</summary>
    Summary,
}

/// <param name="Name">Who (not for <see cref="NextendoFriendEventKind.Summary"/>).</param>
/// <param name="Online">Summary: the friends online or playing (0: not said).</param>
/// <param name="Requests">Summary: the requests waiting (0: not said).</param>
internal sealed record NextendoFriendEvent(NextendoFriendEventKind Kind, string Name = "", string Game = "", int Online = 0, int Requests = 0)
{
    /// <summary>The notification the player gets. Its kind is one the shell and the tray already know.</summary>
    public Toast Toast() => Kind switch
    {
        NextendoFriendEventKind.Online => new Toast(Strings.IsOnline(Who), Network, ToastKind.FriendOnline),
        NextendoFriendEventKind.Playing => new Toast(Game.Length > 0 ? Strings.NextendoFriendPlaying(Who, Game) : Strings.IsPlaying(Who), Network, ToastKind.FriendPlaying),
        NextendoFriendEventKind.Request => new Toast(Strings.NextendoFriendRequest(Who), Strings.FriendRequestBody, ToastKind.FriendRequest),
        NextendoFriendEventKind.Accepted => new Toast(Strings.NextendoFriendAccepted(Who), Network, ToastKind.FriendRequest),
        _ when Online > 0 && Requests > 0 => new Toast(Strings.NextendoFriendsOnline(Online), Strings.NextendoFriendRequests(Requests), ToastKind.FriendOnline),
        _ when Requests > 0 => new Toast(Strings.NextendoFriendRequests(Requests), Strings.FriendRequestBody, ToastKind.FriendRequest),
        _ => new Toast(Strings.NextendoFriendsOnline(Online), Network, ToastKind.FriendOnline),
    };

    private const string Network = "Nextendo Network";

    /// <summary>"A player" for someone Nextendo gave no name for.</summary>
    private string Who => Name.Length > 0 ? Name : Strings.NextendoSomeone;
}

/// <summary>
/// Notifications for Nextendo friends, from successive lists (there is no push: the list is asked again and again).
/// The first list after the start says one thing at most ("Friends online: 3", with the requests waiting): nobody
/// who was already there is announced by name. After it: a friend who comes online, who starts playing, a new
/// request, and someone who accepted the player's own. Each (person, kind) at most once per <see cref="Debounce"/>
/// (a presence that flickers would otherwise notify all evening), and more than <see cref="AtOnce"/> friends in one
/// list become one count. Never anything about the player themself.
/// </summary>
internal sealed class NextendoFriendWatch
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMinutes(5);

    /// <summary>More friends than this coming online in one list (the connection was away) are one notification.</summary>
    public const int AtOnce = 3;

    private readonly Dictionary<(long Pid, NextendoFriendEventKind Kind), DateTimeOffset> _told = [];
    private Dictionary<long, FriendState>? _states;
    private HashSet<long> _incoming = [];
    private HashSet<long> _sent = [];

    public List<NextendoFriendEvent> Diff(NextendoFriendList list, BackgroundSettings prefs, long self, DateTimeOffset now)
    {
        var friends = list.Friends.Where(f => f.Pid != self).ToList();
        var states = friends.ToDictionary(f => f.Pid, f => f.State);
        var incoming = list.Incoming.Where(p => p.Pid != self).ToList();
        var (before, askedBefore, sentBefore) = (_states, _incoming, _sent);
        (_states, _incoming, _sent) = (states, [.. incoming.Select(p => p.Pid)], [.. list.Sent.Select(p => p.Pid)]);
        var presence = prefs.NotifyOnline || prefs.NotifyPlaying;
        if (before is null)
        {
            var online = presence ? friends.Count(f => f.State != FriendState.Offline) : 0;
            var requests = prefs.NotifyRequests ? incoming.Count : 0;
            return online + requests > 0 ? [new NextendoFriendEvent(NextendoFriendEventKind.Summary, Online: online, Requests: requests)] : [];
        }

        var events = new List<(long Pid, NextendoFriendEvent Event)>();
        foreach (var friend in friends)
        {
            if (!before.TryGetValue(friend.Pid, out var was))
            {
                // new in the list: said only when the player had asked them; how they are now is not news
                if (sentBefore.Contains(friend.Pid) && prefs.NotifyRequests)
                {
                    events.Add((friend.Pid, new NextendoFriendEvent(NextendoFriendEventKind.Accepted, friend.Name)));
                }
            }
            else if (friend.State == FriendState.Playing && was != FriendState.Playing && prefs.NotifyPlaying)
            {
                events.Add((friend.Pid, new NextendoFriendEvent(NextendoFriendEventKind.Playing, friend.Name, friend.Game)));
            }
            else if (friend.State == FriendState.Online && was == FriendState.Offline && prefs.NotifyOnline)
            {
                events.Add((friend.Pid, new NextendoFriendEvent(NextendoFriendEventKind.Online, friend.Name)));
            }
        }
        if (prefs.NotifyRequests)
        {
            events.AddRange(incoming.Where(p => !askedBefore.Contains(p.Pid)).Select(p => (p.Pid, new NextendoFriendEvent(NextendoFriendEventKind.Request, p.Name))));
        }
        events.RemoveAll(e =>
        {
            var key = (e.Pid, e.Event.Kind);
            if (_told.TryGetValue(key, out var at) && now - at < Debounce && now >= at)
            {
                return true;
            }
            _told[key] = now;
            return false;
        });
        var told = events.Select(e => e.Event).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var arrivals = told.Count(e => e.Kind is NextendoFriendEventKind.Online or NextendoFriendEventKind.Playing);
        if (arrivals > AtOnce)
        {
            told.RemoveAll(e => e.Kind is NextendoFriendEventKind.Online or NextendoFriendEventKind.Playing);
            told.Add(new NextendoFriendEvent(NextendoFriendEventKind.Summary, Online: friends.Count(f => f.State != FriendState.Offline)));
        }
        return told;
    }
}

/// <summary>One look at the friends list: what it holds now, what is new in it, what went wrong, and when to look again.</summary>
internal sealed record NextendoPoll(NextendoFriendList? List, IReadOnlyList<NextendoFriendEvent> Events, NextendoException? Error, TimeSpan Next);

/// <summary>
/// The linked account's friends on Nextendo Network, kept current by asking (Nextendo has no push channel): every
/// <see cref="Every"/> while the launcher runs, every <see cref="Fast"/> for <see cref="FastFor"/> after the player
/// did something (<see cref="Hurry"/>: the other side's answer shows soon), and slower and slower while it fails
/// (<see cref="Backoff"/>). A 429 is obeyed: nothing is asked before its <c>Retry-After</c> is over, not even by an
/// action's refresh. One look at a time. Nothing here is logged: names, pids and friend codes never reach a log.
/// </summary>
internal sealed class NextendoRoster(NextendoAccount account, NextendoApi api, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Fast = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FastFor = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetryFirst = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RetryMost = TimeSpan.FromMinutes(5);

    /// <summary>The sign-in is no longer good: nothing to ask until the player links again (which wakes the loop).</summary>
    public static readonly TimeSpan SignedOutWait = TimeSpan.FromMinutes(5);

    /// <summary>"Players you met" and the blocked list are asked for when the panel opens, at most this often.</summary>
    public static readonly TimeSpan ExtrasEvery = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _one = new(1, 1);
    private NextendoFriendWatch _watch = new();
    private NextendoFriendList? _list;
    private int _failures;
    private DateTimeOffset _fastUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _waitUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _extrasAt = DateTimeOffset.MinValue;

    /// <summary>The wait after the n-th failure in a row: 30 s, 1 min, 2 min, 4 min, then 5 min.</summary>
    internal static TimeSpan Backoff(int failures) =>
        failures <= 1 ? RetryFirst : failures >= 5 ? RetryMost : TimeSpan.FromTicks(Math.Min(RetryMost.Ticks, RetryFirst.Ticks << (failures - 1)));

    /// <summary>The player did something (or opened the panel): the list is asked more often for a minute.</summary>
    public void Hurry() => _fastUntil = clock.GetUtcNow() + FastFor;

    /// <summary>Another account, or none: nothing of the last one stays, and the next list is a first one.</summary>
    public async Task ResetAsync()
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            (_watch, _list, _failures) = (new NextendoFriendWatch(), null, 0);
            (_fastUntil, _waitUntil, _extrasAt) = (DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Asks for the list once. Never throws for Nextendo's failures: they are in the answer, with how long to wait.</summary>
    /// <param name="self">The linked account's pid: it is never an event.</param>
    public async Task<NextendoPoll> PollAsync(BackgroundSettings prefs, long self, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (now < _waitUntil)
            {
                return new NextendoPoll(_list, [], null, _waitUntil - now); // Nextendo said to wait
            }
            try
            {
                var list = await account.CallAsync(access => api.FriendsAsync(access, cancel), cancel).ConfigureAwait(false);
                (_list, _failures) = (list, 0);
                now = clock.GetUtcNow();
                return new NextendoPoll(list, _watch.Diff(list, prefs, self, now), null, now < _fastUntil ? Fast : Every);
            }
            catch (NextendoException e)
            {
                _failures++;
                var wait = e.Kind switch
                {
                    NextendoError.Limited => e.RetryAfter > Backoff(_failures) ? e.RetryAfter : Backoff(_failures),
                    NextendoError.SignedOut or NextendoError.Scope => SignedOutWait,
                    _ => Backoff(_failures),
                };
                if (e.Kind == NextendoError.Limited)
                {
                    _waitUntil = clock.GetUtcNow() + wait;
                }
                return new NextendoPoll(_list, [], e, wait);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// The players met lately and the blocked list (null: not asked now, or it did not come: what was known stays).
    /// At most once per <see cref="ExtrasEvery"/> unless <paramref name="force"/> (after a block or an unblock).
    /// </summary>
    public async Task<(IReadOnlyList<NextendoPerson>? Recent, IReadOnlyList<NextendoPerson>? Blocked)> ExtrasAsync(bool force, CancellationToken cancel)
    {
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (now < _waitUntil || (!force && now < _extrasAt))
            {
                return (null, null);
            }
            _extrasAt = now + ExtrasEvery;
            return (await QuietAsync(access => api.RecentPlayersAsync(access, cancel), cancel).ConfigureAwait(false),
                await QuietAsync(access => api.BlockedAsync(access, cancel), cancel).ConfigureAwait(false));
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<IReadOnlyList<NextendoPerson>?> QuietAsync(Func<string, Task<IReadOnlyList<NextendoPerson>>> ask, CancellationToken cancel)
    {
        try
        {
            return await account.CallAsync(ask, cancel).ConfigureAwait(false);
        }
        catch (NextendoException e)
        {
            if (e.Kind == NextendoError.Limited)
            {
                _waitUntil = clock.GetUtcNow() + e.RetryAfter;
            }
            return null;
        }
    }

    public void Dispose() => _one.Dispose();
}

/// <summary>What Nextendo is told of the player: 1 online, 2 playing a game (its title id), with a short line.</summary>
internal sealed record NextendoPresenceState(int Status, string AppId, string Detail);

/// <summary>
/// Publishes the player's presence on Nextendo Network (<c>POST /api/presence</c>). Nextendo forgets a presence 90 s
/// after it was said, so it is said again every <see cref="Every"/>, and at once when it changes; when nothing must
/// be shown any more (<see cref="StepAsync"/> with null: the launcher closes, the account is unlinked, PLAY goes to
/// MKWMP servers, or the player does not want to be shown) "offline" is said once. A failure waits before the next
/// try (<see cref="NextendoRoster.Backoff"/>; a 429's <c>Retry-After</c> is obeyed).
/// </summary>
internal sealed class NextendoPresence(NextendoAccount account, NextendoApi api, TimeProvider clock) : IDisposable
{
    /// <summary>
    /// The game's title id (16 hex digits), as its own files name it: the id of the title in the player's dump, and
    /// the folder name mods for it use (Targets/MkwRecomp/ModImport.cs, CoverArtTests' synthetic game).
    /// </summary>
    public const string MkwTitleId = "0400c3f00006e000";

    public static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

    /// <summary>In the launcher, not in a race.</summary>
    public static NextendoPresenceState InLauncher { get; } = new(1, "", "");

    /// <summary>The game runs, online on Nextendo. The line is English and plain: it is shown to other people, in their language's app.</summary>
    public static NextendoPresenceState InGame { get; } = new(2, MkwTitleId, "Online");

    private readonly SemaphoreSlim _one = new(1, 1);
    private NextendoPresenceState? _said;
    private DateTimeOffset _nextAt = DateTimeOffset.MinValue;
    private int _failures;

    /// <summary>What Nextendo was last told with success (null: nothing, or offline).</summary>
    internal NextendoPresenceState? Said => _said;

    /// <summary>
    /// Says <paramref name="want"/> when it is new or due again; null says "offline" when something else was said
    /// before. Cheap to call often: most calls ask nothing. True when Nextendo was called.
    /// </summary>
    public async Task<bool> StepAsync(NextendoPresenceState? want, CancellationToken cancel)
    {
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (want is null)
            {
                if (_said is null)
                {
                    return false;
                }
                (_said, _failures, _nextAt) = (null, 0, DateTimeOffset.MinValue);
                try
                {
                    await account.CallAsync(async access =>
                    {
                        await api.PresenceAsync(access, 0, "", "", cancel).ConfigureAwait(false);
                        return true;
                    }, cancel).ConfigureAwait(false);
                }
                catch (NextendoException)
                {
                    // 90 s without a word says the same
                }
                return true;
            }
            if (now < _nextAt && (want == _said || _failures > 0))
            {
                return false; // said and not due yet, or waiting after a failure
            }
            try
            {
                await account.CallAsync(async access =>
                {
                    await api.PresenceAsync(access, want.Status, want.AppId, want.Detail, cancel).ConfigureAwait(false);
                    return true;
                }, cancel).ConfigureAwait(false);
                (_said, _failures, _nextAt) = (want, 0, clock.GetUtcNow() + Every);
            }
            catch (NextendoException e)
            {
                _failures++;
                var wait = e.Kind switch
                {
                    NextendoError.Limited => e.RetryAfter > NextendoRoster.Backoff(_failures) ? e.RetryAfter : NextendoRoster.Backoff(_failures),
                    NextendoError.SignedOut or NextendoError.Scope => NextendoRoster.SignedOutWait,
                    _ => NextendoRoster.Backoff(_failures),
                };
                _nextAt = clock.GetUtcNow() + wait;
            }
            return true;
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose() => _one.Dispose();
}

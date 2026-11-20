// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// Notifications for friends: "Ana is online", "Ana is playing", "Bia wants to be your friend".
/// Successive friends lists are compared; the first list after start is only remembered (friends
/// already online then do not notify), and each (friend, kind) notifies at most once per
/// <see cref="Debounce"/>: a friend with a flaky connection would otherwise toast all evening.
/// </summary>
internal sealed class FriendWatch
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMinutes(5);

    private readonly Dictionary<(string Name, ToastKind Kind), long> _sent = [];
    private Dictionary<string, FriendState>? _states;
    private HashSet<string> _incoming = [];

    public static Toast ToastFor(ToastKind kind, string name) => kind switch
    {
        ToastKind.FriendOnline => new Toast(Strings.IsOnline(name), Strings.FriendOnlineBody, kind),
        ToastKind.FriendPlaying => new Toast(Strings.IsPlaying(name), Strings.FriendPlayingBody, kind),
        _ => new Toast(Strings.WantsToBeFriend(name), Strings.FriendRequestBody, ToastKind.FriendRequest),
    };

    /// <summary>The events between the previous list and <paramref name="list"/>, filtered by the player's settings and the debounce, by name.</summary>
    public List<(ToastKind Kind, string Name)> Diff(FriendList list, BackgroundSettings prefs, TimeProvider clock, long now)
    {
        var states = list.Friends.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().State, StringComparer.Ordinal);
        var incoming = list.Incoming.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var previous = _states;
        _states = states;
        if (previous is null)
        {
            _incoming = incoming;
            return []; // the first list: only remember it
        }
        var events = new List<(ToastKind Kind, string Name)>();
        foreach (var (name, state) in states)
        {
            var before = previous.GetValueOrDefault(name, FriendState.Offline);
            if (state == FriendState.Playing && before != FriendState.Playing)
            {
                events.Add((ToastKind.FriendPlaying, name));
            }
            else if (state == FriendState.Online && before == FriendState.Offline)
            {
                events.Add((ToastKind.FriendOnline, name));
            }
        }
        events.AddRange(incoming.Where(n => !_incoming.Contains(n)).Select(n => (ToastKind.FriendRequest, n)));
        _incoming = incoming;
        events.RemoveAll(e => e.Kind switch
        {
            ToastKind.FriendOnline => !prefs.NotifyOnline,
            ToastKind.FriendPlaying => !prefs.NotifyPlaying,
            _ => !prefs.NotifyRequests,
        });
        events.RemoveAll(e =>
        {
            var key = (e.Name.ToLowerInvariant(), e.Kind);
            if (_sent.TryGetValue(key, out var at) && clock.GetElapsedTime(at, now) < Debounce)
            {
                return true;
            }
            _sent[key] = now;
            return false;
        });
        events.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return events;
    }
}

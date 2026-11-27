// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text.Json;

namespace Mkwmp.Core.Net;

/// <summary>
/// What Nextendo's live status (<c>GET /api/status/live</c>, scope <c>friends</c>; the same data as its Status page:
/// <c>{updated, games:[{titles:[title id…], players:[…], lobbies:[{count, max, state, kind, mode, players:[…]}],
/// roomsAvailable?}]}</c>) says of the game: how many players are in its lobbies. A lobby's <c>count</c> is who is in
/// it ("Race · Open · 3/24" is 3); every state counts (searching, open, in a match, full): all of them are players in
/// a lobby, which is what separates them from the ones who are only online. The public <c>/api/online-counts</c> has
/// only "online", which is not what is asked, so a link without the friends scope shows no count.
/// </summary>
internal static class NextendoLive
{
    /// <summary>
    /// The players in the lobbies of <paramref name="titleId"/>: 0 when the answer lists no game of that id (nobody
    /// there); null when the answer is not a live status, or says the rooms of that game are not known now
    /// (<c>roomsAvailable: false</c>, or no <c>lobbies</c> list): what is not known is not shown as a number.
    /// </summary>
    internal static int? InLobbies(JsonElement? body, string titleId)
    {
        if (body.At("games") is not { ValueKind: JsonValueKind.Array } games)
        {
            return null;
        }
        long total = 0;
        foreach (var game in games.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object || !Serves(game, titleId))
            {
                continue;
            }
            if (((JsonElement?)game).At("roomsAvailable").Bool() == false || ((JsonElement?)game).At("lobbies") is not { ValueKind: JsonValueKind.Array } lobbies)
            {
                return null;
            }
            foreach (var lobby in lobbies.EnumerateArray())
            {
                total += Players(lobby);
            }
        }
        return (int)Math.Min(total, int.MaxValue);
    }

    private static bool Serves(JsonElement game, string titleId) =>
        ((JsonElement?)game).At("titles") is { ValueKind: JsonValueKind.Array } titles
        && titles.EnumerateArray().Any(t => string.Equals(t.GetString(), titleId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Who is in a lobby: its <c>count</c>, else the players it lists; never below 0.</summary>
    private static long Players(JsonElement lobby)
    {
        if (lobby.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        if (((JsonElement?)lobby).At("count").Long() is { } count)
        {
            return Math.Max(0, count);
        }
        return ((JsonElement?)lobby).At("players") is { ValueKind: JsonValueKind.Array } players ? players.GetArrayLength() : 0;
    }
}

/// <summary>One look at the live status: the players in the game's lobbies (null: not known), or what went wrong, and when to look again.</summary>
internal sealed record NextendoLobbyPoll(int? Players, NextendoException? Error, TimeSpan Next);

/// <summary>
/// Keeps the number of players in the game's lobbies on Nextendo Network, by asking <c>/api/status/live</c> with the
/// access token of the linked account (<see cref="Every"/>; the caller decides when asking is worth it: only while
/// the number is on screen). A 429's <c>Retry-After</c> is obeyed, a failure waits longer and longer
/// (<see cref="NextendoRoster.Backoff"/>), a sign-in that ended or lacks the permission waits
/// <see cref="NextendoRoster.SignedOutWait"/>. A number that cannot be renewed stays for <see cref="Stale"/>, then it is
/// not known any more. One look at a time.
/// </summary>
internal sealed class NextendoLobbies(NextendoAccount account, NextendoApi api, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(45);

    /// <summary>How long the last number is still shown after Nextendo stopped answering.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _one = new(1, 1);
    private (int? Players, DateTimeOffset At) _last;
    private int _failures;
    private DateTimeOffset _waitUntil = DateTimeOffset.MinValue;

    /// <summary>Another account, or none: nothing stays.</summary>
    public async Task ResetAsync()
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            (_last, _failures, _waitUntil) = ((null, DateTimeOffset.MinValue), 0, DateTimeOffset.MinValue);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Asks once. Never throws for Nextendo's failures: they are in the answer, with how long to wait.</summary>
    public async Task<NextendoLobbyPoll> PollAsync(CancellationToken cancel)
    {
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            if (now < _waitUntil)
            {
                return new NextendoLobbyPoll(Kept(now), null, _waitUntil - now); // Nextendo said to wait
            }
            try
            {
                var players = await account.CallAsync(access => api.LobbyPlayersAsync(access, NextendoPresence.MkwTitleId, cancel), cancel).ConfigureAwait(false);
                (_last, _failures) = ((players, clock.GetUtcNow()), 0);
                return new NextendoLobbyPoll(players, null, Every);
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
                now = clock.GetUtcNow();
                if (e.Kind == NextendoError.Limited)
                {
                    _waitUntil = now + wait;
                }
                if (e.Kind is NextendoError.SignedOut or NextendoError.Scope)
                {
                    _last = (null, DateTimeOffset.MinValue); // no permission: the old number is nobody's
                }
                return new NextendoLobbyPoll(Kept(now), e, wait);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private int? Kept(DateTimeOffset now) => now - _last.At <= Stale ? _last.Players : null;

    public void Dispose() => _one.Dispose();
}

/// <summary>"Players in lobbies" on the Play card, for the MKWMP servers.</summary>
public static class LobbyPlayers
{
    /// <summary>
    /// An MKWMP server's seats are the players who are playing on it (a seat is held from PLAY until STOP, and the
    /// lobby is where the game of a seated player goes): its seated count. Null while the server is not known to be up.
    /// </summary>
    public static int? FromServer(ServerView server) => server.State == ServerState.Online ? Math.Max(0, server.Seated) : null;
}

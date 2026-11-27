// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core;

/// <summary>A friend on Nextendo Network. <paramref name="Pid"/> is what tells two friends apart: names there are not unique.</summary>
/// <param name="Name">The name Nextendo shows for them (their display name, else their username).</param>
/// <param name="Game">What they play, when Nextendo says: "MKW" for the game, another game's own title, or "".</param>
/// <param name="Detail">A line of their game's about what they do in it ("" mostly).</param>
/// <param name="Favorite">Marked as a favourite by the player (kept on the account: the same everywhere).</param>
/// <param name="Banned">The account was closed by Nextendo: it stays in the list until removed, always offline.</param>
/// <param name="Account">Their id on the game's servers (16 lowercase hex digits): what the game knows a friend by. "" while Nextendo does not say it. Never shown.</param>
public sealed record NextendoFriend(long Pid, string Name, string FriendCode, FriendState State, string Game, string Detail, bool Favorite, bool Banned = false, string Account = "");

/// <summary>Someone who is not (yet) a friend: who asked, who was asked, who was met in a race, who is blocked.</summary>
/// <param name="Name">"" when Nextendo gave none.</param>
/// <param name="FriendCode">"" when Nextendo gave none: such a player cannot be added from here.</param>
/// <param name="Account">Their id on the game's servers (16 lowercase hex digits); "" while Nextendo does not say it. Never shown.</param>
public sealed record NextendoPerson(long Pid, string Name, string FriendCode, string Account = "");

/// <summary>The friends of the linked Nextendo account, for the window (<see cref="ILauncher.NextendoFriends"/>).</summary>
/// <param name="Enabled">PLAY goes to Nextendo Network with a linked account: friends are Nextendo's (else an MKWMP server's, or locked).</param>
/// <param name="Loaded">False until the first list arrives.</param>
/// <param name="Sent">The requests the player sent that were not answered yet (Nextendo has no way to take one back).</param>
/// <param name="Recent">Players met in online games lately who are not friends, asked or blocked: "Players you met".</param>
/// <param name="Note">The outcome of the last action, or what keeps the list from loading.</param>
/// <param name="ShowOnline">Friends see the player online while the launcher runs (Settings › Nextendo).</param>
public sealed record NextendoFriendsView(
    bool Enabled,
    bool Loaded,
    IReadOnlyList<NextendoFriend> Friends,
    IReadOnlyList<NextendoPerson> Incoming,
    IReadOnlyList<NextendoPerson> Sent,
    IReadOnlyList<NextendoPerson> Recent,
    IReadOnlyList<NextendoPerson> Blocked,
    string? Note,
    bool NoteIsError,
    bool Busy,
    bool ShowOnline)
{
    private static readonly IReadOnlyList<NextendoFriend> NoFriends = [];
    private static readonly IReadOnlyList<NextendoPerson> Nobody = [];

    /// <summary>Not on Nextendo, or nobody signed in.</summary>
    public static NextendoFriendsView Off { get; } = new(false, false, NoFriends, Nobody, Nobody, Nobody, Nobody, null, false, false, true);

    public int OnlineCount => Friends.Count(f => f.State != FriendState.Offline);

    /// <summary>
    /// The friends in the order the window lists them: who plays, who is online, then who is offline; in each group
    /// the favourites first, then by name.
    /// </summary>
    public IEnumerable<NextendoFriend> Ordered(StringComparer names) =>
        Friends.OrderBy(f => f.State).ThenByDescending(f => f.Favorite).ThenBy(f => f.Name, names).ThenBy(f => f.Pid);
}

/// <summary>
/// A Nextendo friend code as the player types it and as Nextendo writes it: <c>SW-1234-5678-9012</c>.
/// </summary>
public static class NextendoFriendCode
{
    /// <summary>The most a field takes: the code with its dashes and a few spaces.</summary>
    public const int MaxTyped = 24;

    /// <summary>
    /// The code as Nextendo writes it, from what was typed or pasted ("sw 1234 5678 9012", "1234-5678-9012",
    /// "123456789012"); null when that is not a friend code: twelve digits, with nothing else than spaces, dashes,
    /// dots and an optional "SW" in front.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        var text = (typed ?? "").Trim();
        if (text.StartsWith("SW", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        Span<char> digits = stackalloc char[12];
        var n = 0;
        foreach (var c in text)
        {
            // full-width digits, as an input method of East Asia may type them
            var d = c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c;
            if (char.IsAsciiDigit(d))
            {
                if (n == 12)
                {
                    return null;
                }
                digits[n++] = d;
            }
            else if (!(char.IsWhiteSpace(c) || c is '-' or '.' or '‐' or '–' or '—' or '－'))
            {
                return null;
            }
        }
        if (n != 12)
        {
            return null;
        }
        var all = new string(digits);
        return $"SW-{all[..4]}-{all[4..8]}-{all[8..]}";
    }

    public static bool IsValid(string? typed) => Normalize(typed) is not null;
}

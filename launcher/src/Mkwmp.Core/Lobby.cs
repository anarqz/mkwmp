// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text;
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>A name a lobby member's game showed, with when it was first seen in this online session.</summary>
public sealed record LobbyName(string Name, DateTimeOffset First);

/// <summary>
/// A player of the lobby the game is in now. <paramref name="Uid"/> is the id the server gave them (it stays when
/// they rename); <paramref name="Name"/> is what their own game shows now ("" when it shows none).
/// </summary>
/// <param name="Id">A short handle for this online session ("p3"): what a page or the game's overlay names the member by.</param>
/// <param name="Local">The player of this PC.</param>
/// <param name="Names">Every name seen under this id since the online session began, oldest first.</param>
public sealed record LobbyMember(string Id, string Uid, string Name, bool Local, IReadOnlyList<LobbyName> Names)
{
    /// <summary>More than one name under the same id in this online session.</summary>
    public bool Renamed => Names.Count > 1;
}

/// <summary>
/// The current lobby, for the window and the game's overlay (<see cref="ILauncher.Lobby"/>): who is in it now. In
/// memory only.
/// </summary>
/// <param name="InLobby">The game told a roster and has not left online since.</param>
/// <param name="OnNextendo">The lobby is on Nextendo Network (else on an MKWMP server).</param>
/// <param name="Server">"nextendo.network" / "mkw.example.net"; "" when not known.</param>
/// <param name="Members">This PC's player first, then the others in the game's order.</param>
public sealed record LobbyView(bool InLobby, bool OnNextendo, string Server, IReadOnlyList<LobbyMember> Members)
{
    private static readonly IReadOnlyList<LobbyMember> Nobody = [];

    public static LobbyView Empty { get; } = new(false, false, "", Nobody);

    /// <summary>The member with that handle; null when they are not in the lobby (any more).</summary>
    public LobbyMember? Member(string id) => Members.FirstOrDefault(m => m.Id == id);
}

/// <summary>What a roster did to the current lobby: for the log (counts only) and the window.</summary>
internal sealed record LobbyOutcome(bool Changed, bool Started, bool Ended, bool Match, int Members);

/// <summary>
/// The current lobby (docs/contracts/LAUNCHER_API.md, <c>POST /local/v1/roster</c>): who is in the game's online
/// lobby now, and for this online session (from the first roster to "left", the game's end or another network) the
/// names each id used, so a player who renames mid-session is flagged.
/// <para>
/// IN MEMORY ONLY. Nothing is written to disk, logged (but counts) or sent anywhere; the player shares it by copying a
/// report (<see cref="LobbyReport"/>). Every member is safe from any thread.
/// </para>
/// </summary>
internal sealed class CurrentLobby(TimeProvider clock)
{
    public const int MaxMembers = 32;
    public const int MaxUidBytes = 1024;
    public const int MaxNameBytes = 64;
    public const int MaxKeyBytes = 128;

    /// <summary>Ids remembered in one online session; the ones after are shown without their older names.</summary>
    public const int MaxPlayers = 256;

    /// <summary>Names kept per id.</summary>
    public const int MaxNames = 64;

    /// <summary>The folder of the removed "Previous Races" history, beside client.toml.</summary>
    public const string LegacyFolderName = "races";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Known> _known = new(StringComparer.Ordinal);
    private LobbyView _view = LobbyView.Empty;
    private bool _open;
    private bool _nextendo;
    private string _server = "";
    private int _next;
    private IReadOnlyList<string> _lastIdentities = [];

    private sealed class Known(string id)
    {
        public string Id { get; } = id;
        public List<LobbyName> Names { get; } = [];
    }

    public LobbyView View
    {
        get
        {
            lock (_gate)
            {
                return _view;
            }
        }
    }

    /// <summary>One roster from the game. Never throws.</summary>
    public LobbyOutcome Apply(Roster roster, bool nextendo, string server)
    {
        ArgumentNullException.ThrowIfNull(roster);
        lock (_gate)
        {
            if (roster.Event == Roster.Left)
            {
                var left = EndCore();
                return new LobbyOutcome(left, false, left, false, 0);
            }
            var ended = false;
            if (_open && (_nextendo != nextendo || !string.Equals(_server, server, StringComparison.OrdinalIgnoreCase)))
            {
                ended = EndCore(); // another network: another online session
            }
            var started = false;
            if (!_open)
            {
                if (roster.Members.Count == 0)
                {
                    return new LobbyOutcome(ended, false, ended, false, 0); // nobody with an id: no lobby yet
                }
                (_open, _nextendo, _server, started) = (true, nextendo, server ?? "", true);
            }
            var now = clock.GetUtcNow();
            var members = new List<LobbyMember>();
            foreach (var member in roster.Members.Take(MaxMembers))
            {
                if (members.Any(m => m.Uid == member.Uid))
                {
                    continue; // said twice: once
                }
                if (!_known.TryGetValue(member.Uid, out var known))
                {
                    known = new Known("p" + (++_next).ToString(CultureInfo.InvariantCulture));
                    if (_known.Count < MaxPlayers)
                    {
                        _known[member.Uid] = known;
                    }
                }
                if (member.Name.Length > 0 && known.Names.All(n => n.Name != member.Name) && known.Names.Count < MaxNames)
                {
                    known.Names.Add(new LobbyName(member.Name, now));
                }
                members.Add(new LobbyMember(known.Id, member.Uid, member.Name, member.Local, [.. known.Names]));
            }
            var view = new LobbyView(true, _nextendo, _server, [.. members.OrderByDescending(m => m.Local)]);
            var changed = started || ended || !Same(view, _view);
            _view = view;
            return new LobbyOutcome(changed, started, ended, roster.Event == Roster.Match, members.Count);
        }
    }

    /// <summary>The game ended, or the player left online: the lobby and every name of the session are forgotten. True when there was one.</summary>
    public bool End()
    {
        lock (_gate)
        {
            return EndCore();
        }
    }

    private bool EndCore()
    {
        if (!_open)
        {
            return false;
        }
        _lastIdentities = IdentitiesCore();
        _known.Clear();
        (_open, _next, _server, _view) = (false, 0, "", LobbyView.Empty);
        return true;
    }

    private static bool Same(LobbyView a, LobbyView b) => a.InLobby == b.InLobby && a.OnNextendo == b.OnNextendo && a.Server == b.Server
        && a.Members.Count == b.Members.Count
        && a.Members.Zip(b.Members).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name && p.First.Local == p.Second.Local
            && p.First.Names.Count == p.Second.Names.Count);

    private List<string> IdentitiesCore() =>
        [.. _known.SelectMany(p => p.Value.Names.Select(n => n.Name).Prepend(p.Key)).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The ids and names of this online session (of the one that just ended, when none is on): what a minidump of the
    /// game may hold of other players, for the crash record to leave out. In memory only.
    /// </summary>
    public IReadOnlyList<string> Identities()
    {
        lock (_gate)
        {
            return _open ? IdentitiesCore() : _lastIdentities;
        }
    }

    /// <summary>
    /// Deletes what the removed "Previous Races" left on this PC: the <c>races</c> folder beside client.toml, whole.
    /// The number of files deleted (0: there was none). A folder that is a link is only unlinked. Never throws.
    /// </summary>
    public static int RemoveLegacyHistory(string configPath)
    {
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", LegacyFolderName);
            var info = new DirectoryInfo(folder);
            if (!info.Exists)
            {
                return 0;
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete(); // a link someone put there: the link goes, not what it points at
                return 0;
            }
            var files = info.EnumerateFiles("*", SearchOption.AllDirectories).Count();
            info.Delete(recursive: true);
            return files;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0; // the next start tries again
        }
    }
}

/// <summary>
/// The text a player copies to report someone of their lobby (Current Lobby › Report): the id, every name seen under
/// it in this online session with when it was first seen, the network, the launcher's version. Plain English (whoever
/// reviews it reads it), times in UTC. It is made only when the player asks, and goes nowhere but the clipboard.
/// </summary>
public static class LobbyReport
{
    private static string At(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>"Nextendo Network (nextendo.network)" / "MKWMP server mkw.example.net".</summary>
    public static string Network(LobbyView lobby)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        return lobby.OnNextendo
            ? lobby.Server.Length > 0 ? $"Nextendo Network ({lobby.Server})" : "Nextendo Network"
            : lobby.Server.Length > 0 ? $"MKWMP server {lobby.Server}" : "MKWMP server";
    }

    private static string Quote(string name) => name.Length == 0 ? "(no name)" : "\"" + name + "\"";

    public static string Text(LobbyView lobby, LobbyMember player, string launcherVersion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(player);
        var b = new StringBuilder();
        b.Append("MKW online player report (from the MKWMP launcher, current lobby)\n");
        b.Append("Player ID: ").Append(player.Uid).Append('\n');
        b.Append("Name shown now: ").Append(Quote(player.Name)).Append('\n');
        b.Append("Network: ").Append(Network(lobby)).Append('\n');
        b.Append("Time of this report: ").Append(At(now)).Append('\n');
        b.Append("Launcher: MKWMP ").Append(launcherVersion).Append('\n');
        b.Append('\n');
        b.Append(CultureInfo.InvariantCulture, $"Names shown under this Player ID in this online session ({player.Names.Count}):\n");
        foreach (var name in player.Names)
        {
            b.Append("- ").Append(Quote(name.Name)).Append(": first seen ").Append(At(name.First)).Append('\n');
        }
        if (player.Names.Count == 0)
        {
            b.Append("- (their game showed no name)\n");
        }
        b.Append('\n');
        b.Append("The Player ID is the id the game server gave this player; the names are what their game showed.\n");
        return b.ToString();
    }

    /// <summary>The same in one short line, for a details box of <paramref name="max"/> characters: the id, then the names.</summary>
    public static string Brief(LobbyMember player, int max)
    {
        ArgumentNullException.ThrowIfNull(player);
        var text = $"Player ID {player.Uid}. Names used: {string.Join(", ", player.Names.Select(n => n.Name))}";
        return text.Length <= max ? text : text[..Math.Max(0, max)];
    }
}

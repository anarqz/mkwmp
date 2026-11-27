// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>A name a player's game showed, with when it was first and last seen.</summary>
public sealed record RaceName(string Name, DateTimeOffset First, DateTimeOffset Last);

/// <summary>
/// A player met online: <see cref="Uid"/> is the id the server gave them (it stays when they rename), the names are
/// what their own game showed, oldest first. <see cref="Local"/>: the player of this PC.
/// </summary>
public sealed record RacePlayer(string Uid, bool Local, IReadOnlyList<RaceName> Names)
{
    /// <summary>The name seen last ("" when the game never showed one).</summary>
    public string Latest => Names.Count == 0 ? "" : Names.MaxBy(n => n.Last)!.Name;

    /// <summary>More than one name under the same id.</summary>
    public bool Renamed => Names.Count > 1;
}

/// <summary>A member of a match, as named when it started.</summary>
public sealed record RaceMember(string Uid, string Name);

/// <summary>A match that started: when, the referee's key, and who was in it.</summary>
public sealed record RaceMatch(DateTimeOffset Time, string Key, IReadOnlyList<RaceMember> Members);

/// <summary>
/// One online session (docs/contracts/LAUNCHER_API.md, "Previous races"): from the first roster to "left", or to ten
/// minutes without one. <see cref="Id"/> is its file's name without the extension.
/// </summary>
public sealed record RaceSession(string Id, DateTimeOffset Started, DateTimeOffset Ended, string Backend, string Server,
    IReadOnlyList<RaceMatch> Matches, IReadOnlyList<RacePlayer> Players)
{
    public const string Nextendo = "nextendo";
    public const string Mkwmp = "mkwmp";

    public bool OnNextendo => Backend == Nextendo;

    /// <summary>How many players used more than one name in it.</summary>
    public int Renamed => Players.Count(p => p.Renamed);
}

/// <summary>A session a player was in: how they were named there, and the matches they were in.</summary>
public sealed record RaceSighting(RaceSession Session, RacePlayer Player, IReadOnlyList<RaceMatch> Matches);

/// <summary>Everything kept about one id: every name across the stored sessions (oldest first) and the sessions, newest first.</summary>
public sealed record RacePlayerHistory(string Uid, bool Local, IReadOnlyList<RaceName> Names, IReadOnlyList<RaceSighting> Sightings)
{
    public string Latest => Names.Count == 0 ? "" : Names.MaxBy(n => n.Last)!.Name;

    public bool Renamed => Names.Count > 1;
}

/// <summary>What a roster did to the history: for the log (counts only) and the window.</summary>
internal sealed record RosterOutcome(bool Changed, bool Started, bool Ended, bool Match, int Members, string? SaveError);

/// <summary>
/// "Previous races": who was in each online lobby, by the id the server gave them, next to the name their game showed.
/// One JSON file per online session in <c>races\</c> beside client.toml, <c>lobby-YYYY-MM-DD-HH-MM-SS.json</c> (the
/// local time of the first roster). Kept <see cref="Keep"/>, <see cref="MaxFiles"/> files at most; pruned at start and
/// when a file is made.
/// <para>
/// LOCAL ONLY. Nothing of it is in a bug report, a crash record, a log line or anything sent to a server
/// (<see cref="IsPrivate"/>, <see cref="RemoveFrom"/>): the player shares it by copying a report (<see cref="RaceReport"/>).
/// </para>
/// </summary>
internal sealed partial class RaceHistory(string folder, TimeProvider clock)
{
    public const string FolderName = "races";
    public const int MaxMembers = 32;
    public const int MaxUidBytes = 1024;
    public const int MaxNameBytes = 64;
    public const int MaxKeyBytes = 128;

    /// <summary>Matches in one file; one more starts another file.</summary>
    public const int MaxMatches = 200;

    /// <summary>Ids in one file; the ones after are not kept.</summary>
    public const int MaxPlayers = 256;

    /// <summary>Names kept per id in one file.</summary>
    public const int MaxNames = 64;
    public const int MaxFiles = 200;

    /// <summary>A file larger than this is not ours (the caps keep one far below).</summary>
    private const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>A session ends after this long without a roster.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    private readonly Lock _gate = new();
    private Open? _open;
    private bool _saveFailed;

    [GeneratedRegex(@"^lobby-(\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2})(-\d{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public string Folder { get; } = folder;

    /// <summary>The session being written: its players and matches so far.</summary>
    private sealed class Open(string id, DateTimeOffset started, string backend, string server)
    {
        public string Id { get; } = id;
        public DateTimeOffset Started { get; } = started;
        public DateTimeOffset Last { get; set; } = started;
        public string Backend { get; } = backend;
        public string Server { get; } = server;
        public List<(string Uid, bool Local, List<RaceName> Names)> Players { get; } = [];
        public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);
        public List<(DateTimeOffset Time, string Key, List<(int Player, string Name)> Members)> Matches { get; } = [];
    }

    // ---- what is never shared -------------------------------------------------------------

    /// <summary>A file in a <c>races</c> folder, or a session file wherever it lies: never copied into a report.</summary>
    public static bool IsPrivate(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        var parts = path.Split('\\', '/');
        return (parts.Length >= 2 && string.Equals(parts[^2], FolderName, StringComparison.OrdinalIgnoreCase))
            || (parts[^1].StartsWith("lobby-", StringComparison.OrdinalIgnoreCase) && parts[^1].Contains(".json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Takes any trace of the history out of a report's folder before it is zipped (nothing puts it there; this makes sure). Never throws.</summary>
    public static void RemoveFrom(string bundle)
    {
        try
        {
            if (!Directory.Exists(bundle))
            {
                return;
            }
            foreach (var dir in Directory.GetDirectories(bundle, FolderName, SearchOption.AllDirectories))
            {
                Directory.Delete(dir, recursive: true);
            }
            foreach (var file in Directory.GetFiles(bundle, "lobby-*", SearchOption.AllDirectories).Where(IsPrivate))
            {
                File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // what could be removed is removed
        }
    }

    // ---- writing --------------------------------------------------------------------------

    /// <summary>One roster from the game. Never throws: a file that cannot be written is said once (<see cref="RosterOutcome.SaveError"/>).</summary>
    public RosterOutcome Record(Roster roster, string backend, string server)
    {
        ArgumentNullException.ThrowIfNull(roster);
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_open is { } stale && (now - stale.Last >= Quiet || now < stale.Last - Quiet))
            {
                _open = null; // ten minutes without a roster: that session ended at its last one
            }
            if (roster.Event == Roster.Left)
            {
                if (_open is not { } leaving)
                {
                    return new RosterOutcome(false, false, false, false, 0, null);
                }
                leaving.Last = now;
                _open = null;
                return new RosterOutcome(true, false, true, false, 0, Save(leaving));
            }
            var match = roster.Event == Roster.Match;
            if (_open is { } other && (other.Backend != backend || !string.Equals(other.Server, server, StringComparison.OrdinalIgnoreCase)
                || (match && other.Matches.Count >= MaxMatches)))
            {
                _open = null; // another network, or a full file: the next one
            }
            var started = false;
            if (_open is null)
            {
                if (roster.Members.Count == 0)
                {
                    return new RosterOutcome(false, false, false, false, 0, null); // nobody with an id: no session yet
                }
                PruneCore(now, MaxFiles - 1);
                _open = new Open(NewId(now), now, backend, server);
                started = true;
            }
            var open = _open;
            open.Last = now;
            var members = new List<(int Player, string Name)>();
            foreach (var member in roster.Members.Take(MaxMembers))
            {
                if (!open.Index.TryGetValue(member.Uid, out var at))
                {
                    if (open.Players.Count >= MaxPlayers)
                    {
                        continue;
                    }
                    at = open.Players.Count;
                    open.Players.Add((member.Uid, member.Local, []));
                    open.Index[member.Uid] = at;
                }
                var player = open.Players[at];
                if (member.Local && !player.Local)
                {
                    open.Players[at] = player = (player.Uid, true, player.Names);
                }
                if (member.Name.Length > 0)
                {
                    var known = player.Names.FindIndex(n => n.Name == member.Name);
                    if (known >= 0)
                    {
                        player.Names[known] = player.Names[known] with { Last = now };
                    }
                    else if (player.Names.Count < MaxNames)
                    {
                        player.Names.Add(new RaceName(member.Name, now, now));
                    }
                }
                members.Add((at, member.Name));
            }
            if (match)
            {
                var same = roster.Key.Length == 0 ? -1 : open.Matches.FindIndex(m => m.Key == roster.Key);
                if (same >= 0)
                {
                    open.Matches[same] = (open.Matches[same].Time, roster.Key, members); // said twice: the same match
                }
                else
                {
                    open.Matches.Add((now, roster.Key, members));
                }
            }
            return new RosterOutcome(true, started, false, match, members.Count, Save(open));
        }
    }

    /// <summary>The error text the first time a save fails (then null until one works again).</summary>
    private string? Save(Open open)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = PathOf(open.Id);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, Serialize(open));
            File.Move(temporary, path, overwrite: true);
            _saveFailed = false;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var first = !_saveFailed;
            _saveFailed = true;
            return first ? e.GetType().Name : null; // not the message: it names the file, and the log is shared
        }
    }

    private string PathOf(string id) => Path.Combine(Folder, id + ".json");

    private string NewId(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, clock.LocalTimeZone);
        var id = "lobby-" + local.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture);
        if (!File.Exists(PathOf(id)))
        {
            return id;
        }
        for (var n = 2; n < 100; n++)
        {
            var next = id + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (!File.Exists(PathOf(next)))
            {
                return next;
            }
        }
        return id + "-99";
    }

    private static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static byte[] Serialize(Open open)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("started", Stamp(open.Started));
            w.WriteString("ended", Stamp(open.Last));
            w.WriteString("backend", open.Backend);
            w.WriteString("server", open.Server);
            w.WriteStartArray("players");
            foreach (var (uid, local, names) in open.Players)
            {
                w.WriteStartObject();
                w.WriteString("uid", uid);
                w.WriteBoolean("local", local);
                w.WriteStartArray("names");
                foreach (var name in names)
                {
                    w.WriteStartObject();
                    w.WriteString("name", name.Name);
                    w.WriteString("first", Stamp(name.First));
                    w.WriteString("last", Stamp(name.Last));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("matches");
            foreach (var (time, key, members) in open.Matches)
            {
                w.WriteStartObject();
                w.WriteString("time", Stamp(time));
                w.WriteString("key", key);
                w.WriteStartArray("members");
                foreach (var (player, name) in members)
                {
                    w.WriteStartObject();
                    w.WriteNumber("p", player);
                    w.WriteString("name", name);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    // ---- reading --------------------------------------------------------------------------

    /// <summary>The stored sessions, newest first. A file that cannot be read or is not a session is left out.</summary>
    public IReadOnlyList<RaceSession> Sessions()
    {
        lock (_gate)
        {
            return [.. Ids().Select(LoadCore).OfType<RaceSession>().OrderByDescending(s => s.Started).ThenByDescending(s => s.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>One session by its id; null when it is gone, unreadable, or not an id.</summary>
    public RaceSession? Load(string id)
    {
        lock (_gate)
        {
            return LoadCore(id);
        }
    }

    /// <summary>Everything kept about one id, across every stored session; null when it is in none.</summary>
    public RacePlayerHistory? Player(string uid) => PlayerIn(Sessions(), uid);

    internal static RacePlayerHistory? PlayerIn(IReadOnlyList<RaceSession> sessions, string uid)
    {
        var sightings = new List<RaceSighting>();
        var names = new List<RaceName>();
        var local = false;
        foreach (var session in sessions.OrderByDescending(s => s.Started))
        {
            if (session.Players.FirstOrDefault(p => p.Uid == uid) is not { } player)
            {
                continue;
            }
            local |= player.Local;
            sightings.Add(new RaceSighting(session, player, [.. session.Matches.Where(m => m.Members.Any(x => x.Uid == uid))]));
            foreach (var name in player.Names)
            {
                var at = names.FindIndex(n => n.Name == name.Name);
                if (at < 0)
                {
                    names.Add(name);
                }
                else
                {
                    names[at] = new RaceName(name.Name, names[at].First < name.First ? names[at].First : name.First, names[at].Last > name.Last ? names[at].Last : name.Last);
                }
            }
        }
        return sightings.Count == 0 ? null : new RacePlayerHistory(uid, local, [.. names.OrderBy(n => n.First)], sightings);
    }

    /// <summary>The ids and names of the newest session: what a minidump of the game may hold of other players.</summary>
    public IReadOnlyList<string> RecentIdentities()
    {
        try
        {
            var sessions = Sessions();
            return sessions.Count > 0
                ? [.. sessions[0].Players.SelectMany(p => p.Names.Select(n => n.Name).Prepend(p.Uid)).Distinct(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private List<string> Ids()
    {
        try
        {
            return Directory.Exists(Folder)
                ? [.. Directory.EnumerateFiles(Folder, "lobby-*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Where(id => IdPattern().IsMatch(id))]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private RaceSession? LoadCore(string id)
    {
        if (id is null || !IdPattern().IsMatch(id))
        {
            return null;
        }
        try
        {
            var info = new FileInfo(PathOf(id));
            return info.Exists && info.Length <= MaxFileBytes ? Parse(id, File.ReadAllBytes(info.FullName)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Time(JsonElement? e) =>
        e.Str() is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    /// <summary>A session file's content; null when it is not one. Tolerant (a file may have been edited by hand) and capped as a roster is.</summary>
    internal static RaceSession? Parse(string id, ReadOnlySpan<byte> utf8)
    {
        try
        {
            if (utf8.StartsWith("﻿"u8))
            {
                utf8 = utf8[3..];
            }
            if (Json.Parse(utf8) is not { ValueKind: JsonValueKind.Object } root)
            {
                return null;
            }
            JsonElement? v = root;
            if (Time(v.At("started")) is not { } started || v.At("players") is not { ValueKind: JsonValueKind.Array } playersJson)
            {
                return null;
            }
            var players = new List<RacePlayer>();
            // the place of each entry of the file in `players` (-1: left out), for the matches' "p"
            var places = new List<int>();
            foreach (var p in playersJson.EnumerateArray())
            {
                JsonElement? player = p;
                var uid = player.At("uid").Str();
                if (uid is not { Length: > 0 } || players.Count >= MaxPlayers || players.Any(x => x.Uid == uid))
                {
                    places.Add(-1);
                    continue;
                }
                var names = new List<RaceName>();
                if (player.At("names") is { ValueKind: JsonValueKind.Array } namesJson)
                {
                    foreach (var n in namesJson.EnumerateArray())
                    {
                        JsonElement? name = n;
                        if (name.At("name").Str() is { Length: > 0 } text && names.Count < MaxNames && names.All(x => x.Name != text))
                        {
                            var first = Time(name.At("first")) ?? started;
                            var last = Time(name.At("last")) ?? first;
                            names.Add(new RaceName(text, first, last < first ? first : last));
                        }
                    }
                }
                places.Add(players.Count);
                players.Add(new RacePlayer(uid, player.At("local").Bool() ?? false, names));
            }
            var matches = new List<RaceMatch>();
            if (v.At("matches") is { ValueKind: JsonValueKind.Array } matchesJson)
            {
                foreach (var m in matchesJson.EnumerateArray())
                {
                    JsonElement? match = m;
                    if (matches.Count >= MaxMatches || Time(match.At("time")) is not { } time)
                    {
                        continue;
                    }
                    var members = new List<RaceMember>();
                    if (match.At("members") is { ValueKind: JsonValueKind.Array } membersJson)
                    {
                        foreach (var x in membersJson.EnumerateArray())
                        {
                            JsonElement? member = x;
                            if (member.At("p").Long() is { } at && at >= 0 && at < places.Count && places[(int)at] >= 0 && members.Count < MaxMembers)
                            {
                                members.Add(new RaceMember(players[places[(int)at]].Uid, member.At("name").Str() ?? ""));
                            }
                        }
                    }
                    matches.Add(new RaceMatch(time, match.At("key").Str() ?? "", members));
                }
            }
            var ended = Time(v.At("ended")) ?? started;
            var backend = v.At("backend").Str() == RaceSession.Nextendo ? RaceSession.Nextendo : RaceSession.Mkwmp;
            return new RaceSession(id, started, ended < started ? started : ended, backend, v.At("server").Str() ?? "", matches, players);
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException or ArgumentException)
        {
            return null; // text that is not UTF-8, or a shape no reader expects
        }
    }

    // ---- retention ------------------------------------------------------------------------

    /// <summary>Deletes the sessions older than <see cref="Keep"/> and all but the newest <see cref="MaxFiles"/>; the number deleted.</summary>
    public int Prune()
    {
        lock (_gate)
        {
            return PruneCore(clock.GetUtcNow(), MaxFiles);
        }
    }

    /// <summary>When a session started, from its file's name (local time).</summary>
    private DateTimeOffset StartOf(string id)
    {
        var stamp = IdPattern().Match(id).Groups[1].Value;
        if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return DateTimeOffset.MinValue; // a date no calendar has: the oldest of all
        }
        var zone = clock.LocalTimeZone;
        try
        {
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        catch (ArgumentException)
        {
            return new DateTimeOffset(local, TimeSpan.Zero);
        }
    }

    private int PruneCore(DateTimeOffset now, int keep)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(Folder))
            {
                return 0;
            }
            var open = _open?.Id;
            var byAge = Ids().Select(id => (Id: id, Start: StartOf(id))).OrderByDescending(x => x.Start).ThenByDescending(x => x.Id, StringComparer.Ordinal).ToList();
            for (var i = 0; i < byAge.Count; i++)
            {
                if (byAge[i].Id != open && (i >= keep || now - byAge[i].Start > Keep))
                {
                    File.Delete(PathOf(byAge[i].Id));
                    deleted++;
                }
            }
            foreach (var left in Directory.EnumerateFiles(Folder, "lobby-*.json.tmp"))
            {
                File.Delete(left); // a write that never finished
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the next start tries again
        }
        return deleted;
    }

    /// <summary>"Clear history": every session file goes, and the session under way is forgotten. The number deleted.</summary>
    public int Clear()
    {
        lock (_gate)
        {
            _open = null;
            var deleted = 0;
            try
            {
                if (Directory.Exists(Folder))
                {
                    foreach (var file in Directory.EnumerateFiles(Folder, "lobby-*").Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // what could be deleted is
            }
            return deleted;
        }
    }
}

/// <summary>
/// The text a player copies to report someone (Previous races › Report): the id, every name with its times, the
/// sessions and matches, the network, the launcher's version. Plain English (whoever reviews it reads it), times in
/// UTC. It is made only when the player asks, and goes nowhere but the clipboard.
/// </summary>
public static class RaceReport
{
    private static string At(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>"Nextendo Network (nextendo.network)" / "MKWMP server mkw.example.net".</summary>
    public static string Network(RaceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.OnNextendo
            ? session.Server.Length > 0 ? $"Nextendo Network ({session.Server})" : "Nextendo Network"
            : session.Server.Length > 0 ? $"MKWMP server {session.Server}" : "MKWMP server";
    }

    private static string Quote(string name) => name.Length == 0 ? "(no name)" : "\"" + name + "\"";

    public static string Text(RacePlayerHistory player, string launcherVersion)
    {
        ArgumentNullException.ThrowIfNull(player);
        var b = new StringBuilder();
        b.Append("MKW online player report (from the MKWMP launcher's local history)\n");
        b.Append("Player ID: ").Append(player.Uid).Append('\n');
        b.Append("Network: ").Append(string.Join("; ", player.Sightings.Select(s => Network(s.Session)).Distinct(StringComparer.Ordinal))).Append('\n');
        b.Append("Launcher: MKWMP ").Append(launcherVersion).Append('\n');
        b.Append('\n');
        b.Append(CultureInfo.InvariantCulture, $"Names shown under this Player ID ({player.Names.Count}):\n");
        foreach (var name in player.Names)
        {
            b.Append("- ").Append(Quote(name.Name)).Append(": first seen ").Append(At(name.First)).Append(", last seen ").Append(At(name.Last)).Append('\n');
        }
        if (player.Names.Count == 0)
        {
            b.Append("- (their game showed no name)\n");
        }
        b.Append('\n');
        b.Append(CultureInfo.InvariantCulture, $"Sessions where this Player ID was seen ({player.Sightings.Count}):\n");
        foreach (var seen in player.Sightings)
        {
            b.Append("- ").Append(At(seen.Session.Started)).Append(" to ").Append(At(seen.Session.Ended)).Append(", ").Append(Network(seen.Session));
            if (seen.Player.Names.Count > 0)
            {
                b.Append(", as ").Append(string.Join(", ", seen.Player.Names.Select(n => Quote(n.Name))));
            }
            b.Append('\n');
            foreach (var match in seen.Matches)
            {
                var named = match.Members.FirstOrDefault(m => m.Uid == player.Uid)?.Name ?? "";
                b.Append("  - match at ").Append(At(match.Time)).Append(" as ").Append(Quote(named));
                if (match.Key.Length > 0)
                {
                    b.Append(" (match key ").Append(match.Key).Append(')');
                }
                b.Append('\n');
            }
        }
        b.Append('\n');
        b.Append("The Player ID is the id the game server gave this player; the names are what their game showed.\n");
        return b.ToString();
    }

    /// <summary>The same in one short line, for a details box of <paramref name="max"/> characters: the id, then the names.</summary>
    public static string Brief(RacePlayerHistory player, int max)
    {
        ArgumentNullException.ThrowIfNull(player);
        var text = $"Player ID {player.Uid}. Names used: {string.Join(", ", player.Names.Select(n => n.Name))}";
        return text.Length <= max ? text : text[..Math.Max(0, max)];
    }
}

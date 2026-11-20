// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text;

namespace Mkwmp.Core.Diagnostics;

/// <summary>
/// One record of <c>&lt;game folder&gt;\mkwmp-crash.log</c>, as the game process wrote it
/// (native/probe/src/crash.h has the format): between <c>=== MKWMP CRASH &lt;id&gt; ===</c> and
/// <c>=== END &lt;id&gt; ===</c>, one <c>key: value</c> a line, then indented blocks. An old record
/// the game cut down to its summary (<see cref="Compacted"/>) has only the id, the summary and the time.
/// </summary>
/// <param name="Id">Crash id: <c>&lt;UTC yyyymmddThhmmssZ&gt;-&lt;pid&gt;[-n]</c>.</param>
/// <param name="Summary"><c>ACCESS_VIOLATION in mkwgame.dll+0x1234 (players=4, host=1, mesh=created)</c>.</param>
/// <param name="CodeName"><c>ACCESS_VIOLATION</c>; "" when the record has no exception line.</param>
/// <param name="Where">The fault's (or the blamed caller's) <c>module+0xoffset</c>, from the summary.</param>
/// <param name="Players">The room's size as the lobby numbers it, else from the Pia endpoints (<see cref="PlayersExact"/> false), else null.</param>
/// <param name="Host">This player has the lowest ussid (the mesh host); null when the game process did not know.</param>
/// <param name="Mesh"><c>created</c> / <c>joined</c>: how this player got into the Pia mesh; "" unknown.</param>
/// <param name="Lobby">The lobby's UUID ("" unknown), as the server logs it.</param>
/// <param name="Ussid">This player's ussid ("" unknown).</param>
/// <param name="Uid">This player's user id (UUID) on the server ("" unknown).</param>
/// <param name="State">The <c>state:</c> block: versions (<c>mkwmp</c>, <c>recomp</c>, <c>game</c>, <c>loader</c>) and the session's named values.</param>
/// <param name="Frames">The host stack, top first: <c>module+0xoffset</c>.</param>
/// <param name="GuestFrames">The guest (game code) return addresses, top first: <c>main+0x…</c>.</param>
/// <param name="Dump">The minidump's file name beside the log; null when the record names none.</param>
/// <param name="Text">The record as written (what goes into a report).</param>
public sealed record CrashRecord(
    string Id,
    string Summary,
    DateTimeOffset? Time,
    string Stage,
    string Code,
    string CodeName,
    string Where,
    string Access,
    string Reason,
    int? Players,
    bool PlayersExact,
    bool? Host,
    string Mesh,
    string Lobby,
    string Ussid,
    string Uid,
    string Os,
    string Gpu,
    IReadOnlyDictionary<string, string> State,
    IReadOnlyList<string> Frames,
    IReadOnlyList<string> GuestFrames,
    string Guest,
    string? Dump,
    bool Compacted,
    string Text)
{
    public const string FileName = "mkwmp-crash.log";
    private const string Start = "=== MKWMP CRASH ";
    private const string End = "=== END ";

    /// <summary>Largest crash log read (the game caps it near 1 MiB; a foreign file is not parsed).</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>A crash id as the game writes it, and nothing a file name must not hold.</summary>
    public static bool IdOk(string id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Every record of the folder's crash log, oldest first; empty when there is none or it cannot be read.</summary>
    public static IReadOnlyList<CrashRecord> ReadAll(string gameFolder)
    {
        try
        {
            var path = Path.Combine(gameFolder, FileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return [];
            }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>The records of a crash log's text. A record cut short (the process died while writing) still counts: it has what was written.</summary>
    public static IReadOnlyList<CrashRecord> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = new List<CrashRecord>();
        List<string>? lines = null;
        string id = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(Start, StringComparison.Ordinal))
            {
                Close();
                id = line[Start.Length..].TrimEnd('=', ' ');
                lines = [line];
            }
            else if (lines is not null)
            {
                lines.Add(line);
                if (line.StartsWith(End, StringComparison.Ordinal))
                {
                    Close();
                }
            }
        }
        Close();
        return records;

        void Close()
        {
            if (lines is not null && IdOk(id))
            {
                records.Add(One(id, lines));
            }
            lines = null;
        }
    }

    private static CrashRecord One(string id, List<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        var frames = new List<string>();
        var guest = new List<string>();
        var block = "";
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("  ", StringComparison.Ordinal))
            {
                var item = line.Trim();
                switch (block)
                {
                    case "state" when item.Split('=', 2) is [var k, var v]:
                        state[k] = v;
                        break;
                    case "stack" when item.Split(' ', 2) is [_, var frame]:
                        frames.Add(frame);
                        break;
                    case "guest_stack" when item.Split(' ', 2) is [_, var frame]:
                        guest.Add(frame);
                        break;
                }
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || line.StartsWith("===", StringComparison.Ordinal))
            {
                continue;
            }
            block = line[..colon];
            fields.TryAdd(block, line[(colon + 1)..].Trim());
        }
        string Get(string key) => fields.GetValueOrDefault(key, "");

        var summary = Get("summary");
        // "<CODE> in <where> (players=…, host=…, mesh=…)"
        var (codeName, where) = ("", "");
        var open = summary.LastIndexOf(" (", StringComparison.Ordinal);
        var head = open > 0 ? summary[..open] : summary;
        if (head.Split(" in ", 2) is [var name, var at])
        {
            (codeName, where) = (name, at);
        }
        var room = Pairs(Get("room"));
        var inSummary = Pairs(open > 0 ? summary[(open + 2)..].TrimEnd(')').Replace(",", "", StringComparison.Ordinal) : "");
        var players = room.GetValueOrDefault("players") ?? inSummary.GetValueOrDefault("players") ?? "?";
        var host = room.GetValueOrDefault("host") ?? inSummary.GetValueOrDefault("host") ?? "?";
        var exception = Get("exception").Split(' ', 2);
        DateTimeOffset? time = DateTimeOffset.TryParse(Get("time_utc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
        var dump = Get("dump").Split(' ', 2)[0];
        return new CrashRecord(
            id,
            summary,
            time,
            Get("stage"),
            exception[0],
            exception.Length > 1 ? exception[1] : codeName,
            where,
            Get("access"),
            Get("reason"),
            int.TryParse(players.TrimStart('~'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null,
            !players.StartsWith('~'),
            host switch { "1" => true, "0" => false, _ => null },
            inSummary.GetValueOrDefault("mesh") is { } mesh && mesh != "?" ? mesh : "",
            Known(room.GetValueOrDefault("lobby")),
            Known(room.GetValueOrDefault("ussid")),
            Known(room.GetValueOrDefault("uid") ?? state.GetValueOrDefault("uid")),
            Get("os"),
            Get("gpu"),
            state,
            frames,
            guest,
            Get("guest"),
            dump.StartsWith("mkwmp-crash-", StringComparison.Ordinal) && dump.EndsWith(".dmp", StringComparison.Ordinal) && dump == Path.GetFileName(dump) ? dump : null,
            !fields.ContainsKey("exception"),
            string.Join("\n", lines) + "\n");

        static string Known(string? value) => value is null or "?" ? "" : value;
    }

    /// <summary><c>a=1 b=two</c> as a dictionary.</summary>
    private static Dictionary<string, string> Pairs(string text)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Split('=', 2) is [var k, var v])
            {
                pairs[k] = v;
            }
        }
        return pairs;
    }
}

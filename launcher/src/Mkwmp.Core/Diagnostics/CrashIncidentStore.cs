// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text;

namespace Mkwmp.Core.Diagnostics;

/// <summary>
/// One crash of the game as the launcher knows it: a record the game process wrote
/// (<see cref="Record"/>), or only an abnormal end the launcher saw (a fail-fast, a kill by the
/// system: nothing in the process could write then), with the exit code and any minidump left
/// since the game started.
/// </summary>
/// <param name="Id">The record's crash id, else <c>exit-&lt;UTC stamp&gt;-&lt;code&gt;</c>.</param>
public sealed record CrashIncident(string Id, DateTimeOffset Time, CrashRecord? Record, uint? ExitCode, string? Dump)
{
    /// <summary>One line for the screen and the issue title's first half: <c>ACCESS_VIOLATION in mkwgame.dll+0x1234</c>, or the exit code.</summary>
    public string What => Record is { CodeName.Length: > 0 } r
        ? r.Where.Length > 0 ? $"{r.CodeName} in {r.Where}" : r.CodeName
        : Record is { Summary.Length: > 0 } old ? old.Summary
        : ExitCode is { } code ? $"exit code {CodeText(code)}" : "unknown";

    /// <summary><c>0xC0000409 (FAIL_FAST)</c>, or the plain number for a small code.</summary>
    public static string CodeText(uint code) => code < 0x10000
        ? code.ToString(CultureInfo.InvariantCulture)
        : $"0x{code:X8}{(Name(code) is { } name ? $" ({name})" : "")}";

    private static string? Name(uint code) => code switch
    {
        0xC0000005 => "ACCESS_VIOLATION",
        0xC000001D => "ILLEGAL_INSTRUCTION",
        0xC00000FD => "STACK_OVERFLOW",
        0xC0000094 => "INT_DIVIDE_BY_ZERO",
        0xC0000374 => "HEAP_CORRUPTION",
        0xC0000409 => "FAIL_FAST",
        0xC0000602 => "FAIL_FAST_EXCEPTION",
        0xC0000417 => "CRT_INVALID_PARAMETER",
        0xC000013A => "CONTROL_C_EXIT",
        0xC0000142 => "DLL_INIT_FAILED",
        0xC0000135 => "DLL_NOT_FOUND",
        0xE04D4B57 => "TERMINATE",
        _ => null,
    };
}

/// <summary>
/// Finds the game's crashes and remembers which ones the player already answered, so the same
/// crash is never offered twice (Infinity Launcher's pattern, OD-241). The records and dumps stay
/// where the game wrote them (<c>&lt;game folder&gt;\mkwmp-crash.log</c>, <c>mkwmp-crash-&lt;id&gt;.dmp</c>);
/// the launcher keeps two small files in its own folder: <c>crash-handled.txt</c> (ids, newest
/// <see cref="MaxHandled"/>) and <c>crash-exits.txt</c> (abnormal ends that left no record).
/// Nothing here is ever sent anywhere.
/// </summary>
public sealed class CrashIncidentStore(string launcherFolder)
{
    public const int MaxHandled = 256;
    public const int MaxExits = 16;
    public const string HandledFile = "crash-handled.txt";
    public const string ExitsFile = "crash-exits.txt";

    private readonly Lock _gate = new();
    private string HandledPath => Path.Combine(launcherFolder, HandledFile);
    private string ExitsPath => Path.Combine(launcherFolder, ExitsFile);

    /// <summary>
    /// The exit codes that mean the game did not end by itself: an NTSTATUS (0x8… / 0xC…: an
    /// unhandled exception, a fail-fast), mkw-recomp's own crash reporter (71), abort() (3) and
    /// the probe's terminate exception. Any other code (0, and the runtime's own small error
    /// codes) is a crash only when the game also left a record.
    /// </summary>
    public static bool AbnormalExit(int? exitCode) =>
        exitCode is { } code && (unchecked((uint)code) >= 0x80000000 || code is 71 or 3);

    /// <summary>
    /// After the game ended: the new crash, or null when it ended well. New means a record not
    /// handled yet whose time is not before <paramref name="started"/> (a minute of slack for
    /// clocks), else an abnormal <paramref name="exitCode"/>, which is then kept in
    /// <c>crash-exits.txt</c> with any dump written since the start.
    /// </summary>
    public CrashIncident? Detect(string gameFolder, DateTimeOffset started, int? exitCode, DateTimeOffset now)
    {
        var handled = Handled();
        var fresh = Incidents(gameFolder).Where(i => i.Record is not null && !handled.Contains(i.Id) && i.Time >= started.AddMinutes(-1)).ToList();
        if (fresh.Count > 0)
        {
            return fresh[0];
        }
        if (!AbnormalExit(exitCode))
        {
            return null;
        }
        var code = unchecked((uint)exitCode!.Value);
        var incident = new CrashIncident($"exit-{now.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{code:x}", now, null, code, NewestDump(gameFolder, started));
        lock (_gate)
        {
            var lines = ReadLines(ExitsPath).Append(string.Join('\t', incident.Id, now.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                code.ToString(CultureInfo.InvariantCulture), incident.Dump ?? "")).TakeLast(MaxExits);
            WriteLines(ExitsPath, lines);
        }
        return incident;
    }

    /// <summary>Every crash not answered yet, newest first: the game's records, then the ends that left none.</summary>
    public IReadOnlyList<CrashIncident> Pending(string? gameFolder)
    {
        var handled = Handled();
        return [.. Incidents(gameFolder).Where(i => !handled.Contains(i.Id))];
    }

    /// <summary>The crash with this id, answered or not; null when it is gone.</summary>
    public CrashIncident? Find(string? gameFolder, string id) => Incidents(gameFolder).FirstOrDefault(i => i.Id == id);

    /// <summary>Every crash known, newest first.</summary>
    public IReadOnlyList<CrashIncident> Incidents(string? gameFolder)
    {
        var all = new List<CrashIncident>();
        if (gameFolder is { Length: > 0 })
        {
            foreach (var record in CrashRecord.ReadAll(gameFolder))
            {
                var dump = record.Dump is { } name && File.Exists(Path.Combine(gameFolder, name)) ? name : null;
                all.Add(new CrashIncident(record.Id, record.Time ?? TimeOfId(record.Id) ?? DateTimeOffset.MinValue, record, null, dump));
            }
        }
        foreach (var line in ReadLines(ExitsPath))
        {
            if (line.Split('\t') is [var id, var time, var code, var dump] && CrashRecord.IdOk(id)
                && DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                && uint.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var exit))
            {
                var there = dump.Length > 0 && dump == Path.GetFileName(dump) && gameFolder is { Length: > 0 } && File.Exists(Path.Combine(gameFolder, dump));
                all.Add(new CrashIncident(id, when, null, exit, there ? dump : null));
            }
        }
        return [.. all.OrderByDescending(i => i.Time)];
    }

    /// <summary>The player answered these (Report or Ignore): never offered again. Throws IOException when the list cannot be saved.</summary>
    public void MarkHandled(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            var keys = ReadLines(HandledPath).ToList();
            foreach (var id in ids.Where(CrashRecord.IdOk))
            {
                keys.Remove(id);
                keys.Add(id);
            }
            try
            {
                Directory.CreateDirectory(launcherFolder);
                var temporary = HandledPath + ".tmp";
                File.WriteAllText(temporary, string.Join('\n', keys.TakeLast(MaxHandled)) + "\n", new UTF8Encoding(false));
                File.Move(temporary, HandledPath, overwrite: true);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new IOException(e.Message, e);
            }
        }
    }

    public bool IsHandled(string id) => Handled().Contains(id);

    private HashSet<string> Handled()
    {
        lock (_gate)
        {
            return ReadLines(HandledPath).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>The newest <c>mkwmp-crash-*.dmp</c> written since <paramref name="since"/> (a record-less end may still have one).</summary>
    private static string? NewestDump(string gameFolder, DateTimeOffset since)
    {
        try
        {
            return new DirectoryInfo(gameFolder).EnumerateFiles("mkwmp-crash-*.dmp")
                .Where(f => f.LastWriteTimeUtc >= since.UtcDateTime.AddMinutes(-1))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.Name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The UTC time a crash id starts with (<c>20261120T183245Z-…</c>).</summary>
    internal static DateTimeOffset? TimeOfId(string id) =>
        id.Length >= 16 && DateTimeOffset.TryParseExact(id[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    private static List<string> ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? [.. File.ReadAllLines(path).Select(l => l.Trim('\r', ' ')).Where(l => l.Length > 0).TakeLast(1024)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void WriteLines(string path, IEnumerable<string> lines)
    {
        try
        {
            Directory.CreateDirectory(launcherFolder);
            File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the incident is still shown this session; it is only not remembered across a restart
        }
    }
}

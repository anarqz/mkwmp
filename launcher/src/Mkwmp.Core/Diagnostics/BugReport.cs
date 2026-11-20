// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core.Diagnostics;

/// <summary>
/// A report ready to file: the GitHub issue to open, the same text in full (for the clipboard:
/// the link carries only what fits in a URL), and the zip to drag into the issue.
/// </summary>
/// <param name="Crash">A crash report (label <c>crash</c>); false: "Found a bug?" (label <c>bug</c>).</param>
/// <param name="Title">The issue's title.</param>
/// <param name="Url">The new-issue link, title and text filled in, at most <see cref="BugReportBuilder.MaxUrl"/> characters.</param>
/// <param name="Text">The whole text (Markdown), never cut.</param>
/// <param name="Zip">The zip's path; null when it could not be written (<paramref name="ZipProblem"/> says why).</param>
/// <param name="OnDesktop">The zip is on the Desktop (else in the launcher's <c>logs</c> folder).</param>
public sealed record BugReport(bool Crash, string Title, string Url, string Text, string? Zip, bool OnDesktop, string? ZipProblem);

/// <summary>What the launcher shows of a crash the player has not answered yet (<see cref="ILauncher.PendingCrash"/>).</summary>
/// <param name="Id">The crash id (<see cref="ILauncher.PrepareReportAsync"/>, <see cref="ILauncher.DismissCrashAsync"/>).</param>
/// <param name="What"><c>ACCESS_VIOLATION in mkwgame.dll+0x1234</c>, or the exit code when the game left no record.</param>
/// <param name="Players">Players in the room; null when not known (or offline).</param>
/// <param name="Host">This player was the mesh host; null when not known.</param>
/// <param name="HasDump">A minidump goes into the report.</param>
/// <param name="Earlier">Other crashes not answered yet (answered together with this one).</param>
public sealed record CrashView(string Id, string What, DateTimeOffset Time, int? Players, bool? Host, bool HasRecord, bool HasDump, int Earlier);

/// <summary>Builds <see cref="BugReport"/>s: the texts are pure (tested as such), the zip is <see cref="SupportBundle"/>'s with the crash files added.</summary>
public static class BugReportBuilder
{
    public const string NewIssue = "https://github.com/anarqz/mkwmp/issues/new";

    /// <summary>The longest link made: GitHub (and the browsers in between) take about 8 KB of URL.</summary>
    public const int MaxUrl = 8000;

    public const int TopFrames = 8;

    /// <summary><c>Crash: ACCESS_VIOLATION in mkwgame.dll+0x1234 (4 players, host)</c>.</summary>
    public static string CrashTitle(CrashIncident crash)
    {
        ArgumentNullException.ThrowIfNull(crash);
        var room = new List<string>();
        if (crash.Record?.Players is { } players)
        {
            room.Add(string.Create(CultureInfo.InvariantCulture, $"{(crash.Record.PlayersExact ? "" : "about ")}{players} players"));
        }
        if (crash.Record?.Host is { } host)
        {
            room.Add(host ? "host" : "not host");
        }
        return $"Crash: {crash.What}{(room.Count > 0 ? $" ({string.Join(", ", room)})" : "")}";
    }

    /// <summary>The crash report's text: the summary, the versions, where it crashed, the top frames, the room, and the zip to attach.</summary>
    public static string CrashBody(CrashIncident crash, string launcherVersion, string? system, string? zipName)
    {
        ArgumentNullException.ThrowIfNull(crash);
        var b = new StringBuilder();
        var r = crash.Record;
        b.Append("**Mario Kart World stopped unexpectedly.**\n\n");
        Row(b, "Crash", crash.What);
        Row(b, "Crash id", crash.Id);
        Row(b, "Time (UTC)", crash.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        if (crash.ExitCode is { } code)
        {
            Row(b, "Exit code", CrashIncident.CodeText(code));
        }
        if (r is not null)
        {
            Row(b, "Exception", $"{r.Code} {r.CodeName}".Trim());
            Row(b, "Access", r.Access);
            Row(b, "Reason", r.Reason);
            Row(b, "Caught at", r.Stage);
            var room = new List<string>();
            if (r.Players is { } players)
            {
                room.Add(string.Create(CultureInfo.InvariantCulture, $"{(r.PlayersExact ? "" : "about ")}{players} players"));
            }
            if (r.Host is { } host)
            {
                room.Add(host ? "this player was the host" : "this player was not the host");
            }
            if (r.Mesh.Length > 0)
            {
                room.Add($"mesh {r.Mesh}");
            }
            Row(b, "Room", string.Join(", ", room));
            Row(b, "Lobby", r.Lobby);
            Row(b, "ussid", r.Ussid);
            Row(b, "MKWMP in the game", Join(" ", r.State.GetValueOrDefault("mkwmp"), r.State.GetValueOrDefault("loader") is { Length: > 0 } loader ? $"({loader})" : null));
            Row(b, "mkw-recomp", r.State.GetValueOrDefault("recomp") ?? "");
            Row(b, "Game", r.State.GetValueOrDefault("game") ?? "");
            Row(b, "OS", r.Os);
            Row(b, "GPU", r.Gpu);
        }
        Row(b, "Launcher", launcherVersion);
        Row(b, "Dump", crash.Dump is null ? "none" : "in the zip");
        if (r is { Frames.Count: > 0 })
        {
            b.Append("\n**Stack** (top ").Append(Math.Min(TopFrames, r.Frames.Count).ToString(CultureInfo.InvariantCulture)).Append(" of ")
                .Append(r.Frames.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n```\n");
            for (var i = 0; i < r.Frames.Count && i < TopFrames; i++)
            {
                b.Append(CultureInfo.InvariantCulture, $"#{i:00} {r.Frames[i]}\n");
            }
            b.Append("```\n");
        }
        if (r is { GuestFrames.Count: > 0 })
        {
            b.Append("\n**Game code** (guest frames)\n```\n");
            b.Append(r.Guest).Append('\n');
            for (var i = 0; i < r.GuestFrames.Count && i < TopFrames; i++)
            {
                b.Append(CultureInfo.InvariantCulture, $"g#{i:00} {r.GuestFrames[i]}\n");
            }
            b.Append("```\n");
        }
        if (r is null)
        {
            b.Append("\nThe game left no crash record (it was ended before it could write one); the logs in the zip are what there is.\n");
        }
        if (system is { Length: > 0 })
        {
            b.Append("\n**System**\n```\n").Append(system.TrimEnd()).Append("\n```\n");
        }
        b.Append("\n**What were you doing when it stopped?**\n\n\n");
        b.Append(Attach(zipName));
        return b.ToString();
    }

    /// <summary>"Found a bug?": the versions and the request for the zip; the player writes the rest.</summary>
    public static string BugBody(string launcherVersion, string? system, string? zipName)
    {
        var b = new StringBuilder("**What happened?**\n\n\n**Steps to reproduce**\n\n1. \n\n**What you expected**\n\n\n");
        Row(b, "Launcher", launcherVersion);
        if (system is { Length: > 0 })
        {
            b.Append("\n**System**\n```\n").Append(system.TrimEnd()).Append("\n```\n");
        }
        b.Append('\n').Append(Attach(zipName));
        return b.ToString();
    }

    private static string Attach(string? zipName) => zipName is null
        ? "**Logs**: the launcher could not save the zip. In the launcher: Settings › Support › Save Logs for a Bug Report, then drag that zip into this issue.\n"
        : $"**Attach the zip**: drag `{zipName}` (the launcher saved it and shows where) into this issue. It has the crash record, the dump, MKWMP's logs and mkw-recomp's own logs (`mkw-recomp/`: mkw.log and its rotation, warm.log, setup.log, setup-state.json, mkw.ini), listed in `index.txt`, with PIN, ticket and tokens removed.\n";

    private static void Row(StringBuilder b, string label, string value)
    {
        if (value.Length > 0)
        {
            b.Append("- **").Append(label).Append("**: `").Append(value.Replace('`', '\'')).Append("`\n");
        }
    }

    private static string Join(string separator, params string?[] parts) => string.Join(separator, parts.Where(p => p is { Length: > 0 }));

    /// <summary>
    /// The new-issue link. Blank issues are off in the repository, so it names the issue form
    /// (<c>crash.yml</c> / <c>bug_report.yml</c>) and fills the form's field <paramref name="field"/>
    /// with the text. The text is cut (at a line, with a note that the rest is in the zip and on
    /// the clipboard) until the whole link is at most <see cref="MaxUrl"/> characters.
    /// </summary>
    public static string Url(string template, string label, string title, string field, string text, IEnumerable<(string Field, string Value)>? more = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var head = new StringBuilder(NewIssue).Append("?template=").Append(Uri.EscapeDataString(template)).Append("&labels=").Append(Uri.EscapeDataString(label))
            .Append("&title=").Append(Uri.EscapeDataString(title.Length > 200 ? title[..200] : title));
        foreach (var (name, value) in more ?? [])
        {
            head.Append('&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        head.Append('&').Append(Uri.EscapeDataString(field)).Append('=');
        const string Cut = "\n\n_(cut to fit the link: the full text is on the clipboard and in the zip)_\n";
        var body = text;
        var escaped = Uri.EscapeDataString(body);
        while (head.Length + escaped.Length > MaxUrl && body.Length > 0)
        {
            // drop about what is over (an escaped character is up to 9 long), then back to the end of a line
            var over = head.Length + escaped.Length - MaxUrl;
            var keep = Math.Max(0, body.Length - Cut.Length - Math.Max(16, over / 3));
            var line = body.LastIndexOf('\n', Math.Max(0, keep - 1));
            body = body[..(line > 0 ? line : keep)];
            if (body.Split("```").Length % 2 == 0)
            {
                body += "\n```"; // a code block the cut left open
            }
            body += Cut;
            escaped = Uri.EscapeDataString(body);
            if (keep == 0)
            {
                break;
            }
        }
        return head.Append(escaped).ToString();
    }

    /// <summary>
    /// Writes the report's zip: <see cref="SupportBundle"/>'s files (the launcher's logs, the game's
    /// <c>mkwmp.log</c>, the redacted <c>mkwmp.toml</c>, <c>system.txt</c>), mkw-recomp's own logs and
    /// state (<see cref="RuntimeFiles"/>: what changed since <paramref name="since"/>, the session's start,
    /// and the newest few), an <c>index.txt</c> of everything, plus, for a crash, <c>mkwmp-crash.log</c>, the crash's own text
    /// (<c>crash.txt</c>), the mod's own log and the minidump, in which every secret of
    /// <paramref name="secrets"/> is overwritten (a dump is a piece of the game's memory). On
    /// <paramref name="desktop"/> when that folder can be written, else beside the launcher's logs.
    /// Throws IOException with the reason when neither works.
    /// </summary>
    public static (string Zip, bool OnDesktop, string? System) SaveZip(string launcherFolder, string version, IGameTarget? target, GameInstall? install,
        IReadOnlyList<CrashIncident> crashes, string text, string? desktop, IReadOnlyList<string> secrets, TimeProvider? clock = null, DateTimeOffset? since = null)
    {
        ArgumentNullException.ThrowIfNull(crashes);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var name = crashes.Count > 0 ? $"mkwmp-crash-{crashes[0].Id}.zip" : $"mkwmp-bug-{SessionLog.FileStamp(now)}.zip";
        string? system = null;
        // what "since the session started" means for a crash nobody timed: the half day before it
        var from = since ?? (crashes.Count > 0 ? crashes[0].Time : now).AddHours(-12);
        void Extra(string folder)
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, crashes.Count > 0 ? "crash.txt" : "report.txt"), SessionLog.Redact(text));
                if (install is not null)
                {
                    // mkw-recomp's own logs and state (RuntimeFiles), and what identifies the build, in system.txt
                    File.AppendAllText(Path.Combine(folder, SupportBundle.SystemFile), RuntimeFiles.Collect(folder, launcherFolder, install, from));
                }
                system = File.ReadAllText(Path.Combine(folder, SupportBundle.SystemFile));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // one file less
            }
            try
            {
                Files(folder);
            }
            finally
            {
                RuntimeFiles.WriteIndex(folder);
            }
        }
        void Files(string folder)
        {
            if (install is null)
            {
                return;
            }
            var game = install.Root;
            SupportBundle.Copy(Path.Combine(game, CrashRecord.FileName), Path.Combine(folder, CrashRecord.FileName));
            // the mod keeps its session log in its own folder (the winmm.dll build: beside mkw.exe, already in the bundle)
            foreach (var root in new[] { install.DataRoot, install.Root }.Where(r => r is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                SupportBundle.Copy(Path.Combine(root!, "mods", "mkwmp", "mkwmp.log"), Path.Combine(folder, "mkwmp-mod.log"));
            }
            // the crashes' own dumps, then any other the game wrote since the session started (matched by time)
            var later = RuntimeFiles.Pick(game, "mkwmp-crash-*.dmp", from).Where(f => f.LastWriteTimeUtc >= from.UtcDateTime.AddMinutes(-1)).Select(f => f.Name);
            foreach (var dump in crashes.Select(c => c.Dump).Where(d => d is not null).Concat(later).Distinct().Take(3))
            {
                CopyDump(Path.Combine(game, dump!), Path.Combine(folder, dump!), secrets);
            }
        }

        IOException? first = null;
        foreach (var (folder, onDesktop) in new[] { (desktop, true), (Path.Combine(launcherFolder, "logs"), false) })
        {
            if (folder is not { Length: > 0 } || (onDesktop && !Directory.Exists(folder)))
            {
                continue;
            }
            try
            {
                var (_, zip) = SupportBundle.Save(launcherFolder, version, target, install, clock, Extra, Path.Combine(folder, name));
                return (zip, onDesktop, system);
            }
            catch (IOException e)
            {
                first ??= e;
            }
        }
        throw first ?? new IOException("no folder to save the report in");
    }

    /// <summary>Largest dump put in a report (the game caps its own at 64 MiB).</summary>
    public const long MaxDumpBytes = 96L * 1024 * 1024;

    /// <summary>Copies a minidump with every secret (as ASCII/UTF-8 and as UTF-16) overwritten by <c>x</c>: same size, same structure.</summary>
    internal static void CopyDump(string from, string to, IReadOnlyList<string> secrets)
    {
        try
        {
            var info = new FileInfo(from);
            if (!info.Exists || info.Length > MaxDumpBytes)
            {
                return;
            }
            byte[] bytes;
            using (var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[source.Length];
                source.ReadExactly(bytes);
            }
            foreach (var secret in secrets.Where(s => s is { Length: >= 6 }).Distinct(StringComparer.Ordinal))
            {
                Scrub(bytes, Encoding.UTF8.GetBytes(secret));
                Scrub(bytes, Encoding.Unicode.GetBytes(secret));
            }
            File.WriteAllBytes(to, bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // one file less
        }
    }

    private static void Scrub(Span<byte> bytes, ReadOnlySpan<byte> secret)
    {
        for (var at = bytes.IndexOf(secret); at >= 0;)
        {
            bytes.Slice(at, secret.Length).Fill((byte)'x');
            var next = bytes[(at + secret.Length)..].IndexOf(secret);
            at = next < 0 ? -1 : at + secret.Length + next;
        }
    }
}

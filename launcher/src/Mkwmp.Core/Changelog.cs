// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core;

/// <summary>One release of the changelog: its version, its date as written ("2026-11-19", "unreleased"), its notes (Markdown).</summary>
/// <param name="IsRunning">This is the version the launcher runs.</param>
public sealed record ChangelogRelease(string Version, string? Date, string Markdown, bool IsRunning);

/// <summary>The changelog as the What's New page shows it: the releases, newest first.</summary>
/// <param name="Loose">Read from the <c>CHANGELOG.md</c> beside the exe (else the one built in).</param>
public sealed record ChangelogDocument(IReadOnlyList<ChangelogRelease> Releases, bool Loose);

/// <summary>
/// MKWMP's changelog (<c>CHANGELOG.md</c> at the root of the repository, one <c>## version — date</c>
/// section per release). It is built into this assembly, so a launcher alone in its folder still
/// has it; a <c>CHANGELOG.md</c> beside the exe (publish.ps1 copies one there) wins when it reads.
/// English only: it is written once per release.
/// </summary>
public static class Changelog
{
    public const string FileName = "CHANGELOG.md";

    /// <summary>A loose file larger than this is not read (the built-in one is shown).</summary>
    public const int MaxBytes = 1 << 20;

    private const string ResourceName = "Mkwmp.Core.CHANGELOG.md";

    /// <summary>The changelog built into the launcher; empty when the build has none.</summary>
    public static string BuiltIn()
    {
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return "";
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The text: <c>&lt;folder&gt;/CHANGELOG.md</c> when it is there, reads and has a release; else the built-in one. Never throws.</summary>
    public static (string Markdown, bool Loose) Read(string? folder)
    {
        if (folder is { Length: > 0 })
        {
            try
            {
                var path = Path.Combine(folder, FileName);
                var file = new FileInfo(path);
                if (file.Exists && file.Length <= MaxBytes && File.ReadAllText(path, Encoding.UTF8) is var text && Parse(text, null).Count > 0)
                {
                    return (text, true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // the built-in one stands in
            }
        }
        return (BuiltIn(), false);
    }

    /// <summary>The changelog of the launcher in <paramref name="folder"/> (default: its own), with <paramref name="running"/> marked.</summary>
    public static ChangelogDocument Load(string? running, string? folder = null)
    {
        var (markdown, loose) = Read(folder ?? LauncherOptions.LauncherFolder);
        return new ChangelogDocument(Parse(markdown, running), loose);
    }

    /// <summary>
    /// The releases of a changelog, newest first (by semver; a heading that is no version keeps its
    /// place after them). Text before the first <c>## </c> heading is the file's own introduction and
    /// is left out. The release that is <paramref name="running"/> is marked: the same version, else
    /// (a development build, <c>1.4.0-dev.abc</c>) the release with its numbers.
    /// </summary>
    public static IReadOnlyList<ChangelogRelease> Parse(string? markdown, string? running)
    {
        var found = new List<(string Version, string? Date, StringBuilder Body)>();
        var inCode = false;
        foreach (var raw in (markdown ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
            }
            if (!inCode && line.StartsWith("## ", StringComparison.Ordinal))
            {
                var (version, date) = Heading(line[3..]);
                if (version.Length > 0)
                {
                    found.Add((version, date, new StringBuilder()));
                    continue;
                }
            }
            if (found.Count > 0)
            {
                found[^1].Body.Append(raw.TrimEnd('\r')).Append('\n');
            }
        }
        var ordered = found
            .Select((r, i) => (r, i, Sem: SemVer.TryParse(r.Version)))
            .OrderBy(x => x.Sem is null ? 1 : 0)
            .ThenByDescending(x => x.Sem)
            .ThenBy(x => x.i)
            .ToList();
        var current = Plain(running);
        var mark = current is null ? -1 : ordered.FindIndex(x => x.Sem == current);
        if (mark < 0 && current is not null)
        {
            mark = ordered.FindIndex(x => x.Sem is not null && SameNumbers(x.Sem, current));
        }
        return [.. ordered.Select((x, i) => new ChangelogRelease(x.r.Version, x.r.Date, x.r.Body.ToString().Trim('\n', ' '), i == mark))];
    }

    /// <summary>
    /// The releases What's New shows on <paramref name="channel"/>. Alpha: all of them, the test versions
    /// (<c>1.5.1-rc.1</c>) with their own notes. Stable: a test version's section is left out once the release it led
    /// to is in the list (its notes are that release's), unless it is the version that runs.
    /// </summary>
    public static IReadOnlyList<ChangelogRelease> For(IReadOnlyList<ChangelogRelease> releases, UpdateChannel channel)
    {
        ArgumentNullException.ThrowIfNull(releases);
        if (channel == UpdateChannel.Alpha)
        {
            return releases;
        }
        var stable = releases.Select(r => SemVer.TryParse(r.Version)).Where(v => v is { IsPrerelease: false }).ToList();
        return [.. releases.Where(r => r.IsRunning || SemVer.TryParse(r.Version) is not { IsReleaseCandidate: true } rc || !stable.Any(v => SameNumbers(v!, rc)))];
    }

    /// <summary>
    /// The part of a release's notes that is about that release: up to the first rule (<c>---</c>),
    /// after which GitHub's notes repeat the earlier releases ("From 1.2.0 …"). Lines that start
    /// with "• " are list items, as "- ".
    /// </summary>
    public static string Highlights(string? notes)
    {
        var lines = (notes ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var cut = Array.FindIndex(lines, l => l.Trim() is "---" or "***" or "___");
        var kept = (cut < 0 ? lines : lines[..cut]).Select(l => l.TrimStart().StartsWith("• ", StringComparison.Ordinal) ? "- " + l.TrimStart()[2..] : l);
        return string.Join('\n', kept).Trim();
    }

    /// <summary>"1.3.0 — 2026-11-19" → (1.3.0, 2026-11-19); "v1.3.0" → 1.3.0; the date may be missing.</summary>
    private static (string Version, string? Date) Heading(string text)
    {
        text = text.Trim().TrimEnd('#').Trim();
        var cut = text.IndexOfAny([' ', '\t']);
        var version = (cut < 0 ? text : text[..cut]).Trim('[', ']');
        if (version.StartsWith('v') || version.StartsWith('V'))
        {
            version = version[1..];
        }
        if (version.Length == 0 || !char.IsAsciiDigit(version[0]))
        {
            return ("", null);
        }
        var rest = cut < 0 ? "" : text[cut..].Trim().TrimStart('—', '–', '-', '(', ' ').TrimEnd(')').Trim();
        return (version, rest.Length > 0 ? rest : null);
    }

    private static SemVer? Plain(string? running)
    {
        if (running is not { Length: > 0 })
        {
            return null;
        }
        var plus = running.IndexOf('+', StringComparison.Ordinal);
        return SemVer.TryParse(plus >= 0 ? running[..plus] : running);
    }

    private static bool SameNumbers(SemVer a, SemVer b) => a.Major == b.Major && a.Minor == b.Minor && a.Patch == b.Patch;
}

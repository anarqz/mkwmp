// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The What's New page's changelog (Changelog.cs: CHANGELOG.md built in, a loose copy beside the exe
/// winning), the running version marked on it, and the launcher offering no privacy policy of its own.
/// </summary>
public sealed class ChangelogTests
{
    /// <summary>The repository's root, from this file's place in the tree (the build may be anywhere: --artifacts-path).</summary>
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var fromSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here) ?? ".", "..", "..", ".."));
        if (File.Exists(Path.Combine(fromSource, Changelog.FileName)))
        {
            return fromSource;
        }
        // a mapped source path (CI): up from the tests' own folder
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, Changelog.FileName)) && Directory.Exists(Path.Combine(dir.FullName, "launcher")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("the repository's root was not found");
    }

    private static string LauncherDir =>Path.Combine(RepoRoot(), "launcher");

    [Fact]
    public void The_built_in_changelog_is_the_repository_s()
    {
        var file = File.ReadAllText(Path.Combine(RepoRoot(), Changelog.FileName), Encoding.UTF8);
        Assert.Equal(file.ReplaceLineEndings("\n"), Changelog.BuiltIn().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Every_release_has_a_section_newest_first_and_1_4_0_is_on_top()
    {
        var releases = Changelog.Parse(Changelog.BuiltIn(), null);
        Assert.Equal(
            ["1.4.0", "1.3.1", "1.3.0", "1.2.0", "1.1.1", "1.1.0", "1.0.2", "1.0.1", "1.0.0", "0.0.1-rc.3", "0.0.1-rc.2", "0.0.1-rc.1"],
            releases.Select(r => r.Version));
        Assert.Equal("unreleased", releases[0].Date);
        Assert.Equal("2026-11-21", releases[1].Date); // 1.3.1, the Rust launcher's last release
        Assert.Equal("2026-11-19", releases[2].Date);
        Assert.Equal("2026-11-14", releases[^1].Date);
        Assert.All(releases, r => Assert.False(string.IsNullOrWhiteSpace(r.Markdown)));
        Assert.All(releases, r => Assert.DoesNotContain("SHA256SUMS", r.Markdown, StringComparison.Ordinal)); // the download lists are left out
        Assert.Contains("0.1.16-mkwmp", releases[0].Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_loose_copy_the_built_in_one_is_shown()
    {
        var nowhere = Path.Combine(Path.GetTempPath(), "mkwmp-no-such-folder-" + Guid.NewGuid().ToString("N"));
        var doc = Changelog.Load("1.4.0", nowhere);
        Assert.False(doc.Loose);
        Assert.Equal("1.4.0", doc.Releases[0].Version);
        Assert.False(Changelog.Read(null).Loose);
        Assert.Equal(Changelog.BuiltIn(), Changelog.Read(null).Markdown);
    }

    [Fact]
    public void A_loose_copy_beside_the_exe_wins_and_a_broken_one_is_ignored()
    {
        var folder = Rig.NewFolder("changelog");
        var path = Path.Combine(folder, Changelog.FileName);
        File.WriteAllText(path, "# Changelog\n\nIntro.\n\n## 9.9.9 — 2030-01-01\n\n- From the loose file.\n");
        var doc = Changelog.Load("1.4.0", folder);
        Assert.True(doc.Loose);
        var only = Assert.Single(doc.Releases);
        Assert.Equal(("9.9.9", "2030-01-01", "- From the loose file.", false), (only.Version, only.Date, only.Markdown, only.IsRunning));

        // no release in it (an empty or wrong file): the built-in one
        File.WriteAllText(path, "nothing here\n");
        Assert.False(Changelog.Load("1.4.0", folder).Loose);
        // too large to be a changelog
        File.WriteAllBytes(path, new byte[Changelog.MaxBytes + 1]);
        Assert.False(Changelog.Load("1.4.0", folder).Loose);
    }

    [Fact]
    public void The_running_version_is_marked()
    {
        const string text = "## 1.3.0 — 2026-11-19\n\nold\n\n## 1.4.0 — unreleased\n\nnew\n\n## 1.4.0-rc.1 — 2026-11-18\n\nrc\n";
        Assert.Equal(["1.4.0"], Changelog.Parse(text, "1.4.0").Where(r => r.IsRunning).Select(r => r.Version));
        Assert.Equal(["1.3.0"], Changelog.Parse(text, "1.3.0+abc123").Where(r => r.IsRunning).Select(r => r.Version));
        Assert.Equal(["1.4.0-rc.1"], Changelog.Parse(text, "1.4.0-rc.1").Where(r => r.IsRunning).Select(r => r.Version));
        // a development build of 1.4.0 is 1.4.0's
        Assert.Equal(["1.4.0"], Changelog.Parse(text, "1.4.0-dev.5d29058").Where(r => r.IsRunning).Select(r => r.Version));
        Assert.DoesNotContain(Changelog.Parse(text, "2.0.0"), r => r.IsRunning);
        Assert.DoesNotContain(Changelog.Parse(text, null), r => r.IsRunning);
        // newest first, whatever the order of the file
        Assert.Equal(["1.4.0", "1.4.0-rc.1", "1.3.0"], Changelog.Parse(text, null).Select(r => r.Version));
        // the real changelog marks this launcher's version
        Assert.Equal(["1.4.0"], Changelog.Load("1.4.0").Releases.Where(r => r.IsRunning).Select(r => r.Version));
    }

    [Fact]
    public void Headings_inside_code_and_other_levels_are_not_releases()
    {
        const string text = "# Changelog\n\n## v2.0.0 (2027-01-01)\n\n### Fixed\n\n```\n## 9.0.0 not a release\n```\n\n## Unreleased notes\n\nkept\n";
        var release = Assert.Single(Changelog.Parse(text, null));
        Assert.Equal(("2.0.0", "2027-01-01"), (release.Version, release.Date));
        Assert.Contains("### Fixed", release.Markdown, StringComparison.Ordinal);
        Assert.Contains("## 9.0.0 not a release", release.Markdown, StringComparison.Ordinal);
        Assert.Contains("## Unreleased notes", release.Markdown, StringComparison.Ordinal); // no version: part of the release above
    }

    [Fact]
    public void An_update_s_banner_shows_only_its_own_notes()
    {
        const string notes = "**mkwmp 1.5.0**: things.\n\n- One\n- Two\n\n---\n\n**From 1.4.0** (old)\n\n- Old\n";
        Assert.Equal("**mkwmp 1.5.0**: things.\n\n- One\n- Two", Changelog.Highlights(notes));
        Assert.Equal("- A\n- B", Changelog.Highlights("• A\n• B"));
        Assert.Equal("", Changelog.Highlights(null));
    }

    [Fact]
    public void The_launcher_offers_no_privacy_policy_of_its_own()
    {
        // The constant is gone, and no page, text or changelog of the launcher points at the project's policy:
        // the only policy is the server's own (GET {server}/privacy).
        Assert.Null(typeof(Strings).GetField("PrivacyUrl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        Assert.Null(typeof(Strings).GetProperty("PrivacyFallbackMarkdown"));
        string[] patterns = ["github.io/mkwmp/#privacy", "github.io/mkwmp#privacy", "PrivacyUrl"];
        var files = Directory.EnumerateFiles(Path.Combine(LauncherDir,"src"), "*.*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".cs" or ".xaml" or ".json")
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Append(Path.Combine(RepoRoot(), Changelog.FileName))
            .ToList();
        Assert.True(files.Count > 50);
        Assert.Empty(files.Where(f => patterns.Any(p => File.ReadAllText(f).Contains(p, StringComparison.Ordinal))).Select(Path.GetFileName));
        Assert.Empty(Strings.English.Where(p => p.Value.Contains("#privacy", StringComparison.Ordinal)).Select(p => p.Key));
    }

    [Fact]
    public void About_no_longer_names_the_launcher_it_was_based_on()
    {
        var views = Directory.EnumerateFiles(Path.Combine(LauncherDir,"src", "Mkwmp.App"), "*.xaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(LauncherDir,"src", "Mkwmp.App", "i18n"), "*.json"));
        Assert.Empty(views.Where(f => File.ReadAllText(f).Contains("InfinityHorizon", StringComparison.Ordinal)).Select(Path.GetFileName));
        Assert.DoesNotContain(Strings.English.Keys, k => k.StartsWith("settings.about.based_on", StringComparison.Ordinal));
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets.MkwRecomp;
using Mkwmp.Core.Updates;
using Mkwmp.Update;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The update channels (launcher/docs/UPDATES.md, "Channels"): stable takes stable releases only, alpha also the
/// release candidates (<c>-rc.N</c>); an update is required per channel; and "Return to Stable", the one install
/// that goes down a version, only when the player asks for it on a release candidate.
/// </summary>
public partial class SelfUpdateTests
{
    private static SemVer V(string text) => SemVer.TryParse(text)!;

    private static Release Rel(string tag, bool prerelease = false, bool draft = false) => new(tag, draft, null, [], prerelease);

    // ---- versions and the choice of a release -------------------------------------------------

    [Fact]
    public void Versions_are_ordered_by_semver_and_only_rc_N_is_a_release_candidate()
    {
        string[] ascending = ["1.5.0", "1.5.1-alpha", "1.5.1-beta.2", "1.5.1-rc.1", "1.5.1-rc.2", "1.5.1-rc.10", "1.5.1", "1.5.2-rc.1", "1.5.2", "1.10.0"];
        for (var i = 1; i < ascending.Length; i++)
        {
            Assert.True(V(ascending[i]) > V(ascending[i - 1]), $"{ascending[i]} > {ascending[i - 1]}");
            Assert.True(V(ascending[i - 1]) < V(ascending[i]));
        }
        Assert.True(V("1.5.1-rc.2") > V("1.5.1-rc.1") && V("1.5.1") > V("1.5.1-rc.2") && V("1.5.1-rc.1") > V("1.5.0"));
        Assert.Equal(V("1.5.1-rc.1"), V("1.5.1-rc.1+build.7")); // build metadata orders nothing

        string[] candidates = ["1.5.1-rc.1", "1.5.1-rc.0", "0.0.1-rc.12"];
        Assert.All(candidates, v => Assert.True(V(v).IsReleaseCandidate && V(v).IsPrerelease, v));
        // Any other suffix is a pre-release, and no channel's: only -rc.N counts as alpha.
        string[] others = ["1.5.1-rc1", "1.5.1-rc", "1.5.1-RC.1", "1.5.1-rc.1.fix", "1.5.1-rc.x", "1.5.1-beta.1", "1.5.1-alpha", "1.5.1-dev.abc", "0.1.15-mkwmp"];
        Assert.All(others, v => Assert.True(V(v).IsPrerelease && !V(v).IsReleaseCandidate, v));
        Assert.False(V("1.5.1").IsPrerelease || V("1.5.1").IsReleaseCandidate);
    }

    [Fact]
    public void Stable_takes_stable_releases_only_and_alpha_the_highest_of_both()
    {
        Release[] list =
        [
            Rel("v1.5.0"),
            Rel("alpha-v1.5.1-rc.1", prerelease: true),
            Rel("alpha-v1.5.1-rc.2", prerelease: true),
            Rel("v1.5.1-rc.3", prerelease: true),   // a release candidate under a plain v tag: the same rules
            Rel("v1.5.1-rc.4"),                      // not flagged on GitHub: the suffix alone keeps it off stable
            Rel("v1.5.2-beta.1", prerelease: true),  // not -rc.N: in no channel
            Rel("v1.6.0", prerelease: true),         // a plain version flagged as a pre-release: in no channel
            Rel("alpha-v1.7.0", prerelease: true),   // the alpha prefix names release candidates only
            Rel("v1.8.0-rc.1", prerelease: true, draft: true),
            Rel("v1.9.0", draft: true),
            Rel("junk"),
        ];
        // Stable: nothing above 1.5.0, whatever is published for testers.
        Assert.Null(Updater.Newest(list, V("1.5.0"), UpdateChannel.Stable));
        Assert.Null(Updater.Newest(list, V("1.5.0")));
        Assert.Equal("v1.5.0", Updater.Newest(list, V("1.4.9"), UpdateChannel.Stable)!.Tag);
        Assert.Equal("v1.5.0", Updater.LatestStable(list)!.Tag);
        // Alpha: the highest of stable and -rc.N.
        Assert.Equal("v1.5.1-rc.4", Updater.Newest(list, V("1.5.0"), UpdateChannel.Alpha)!.Tag);
        Assert.Equal("v1.5.1-rc.4", Updater.Newest(list, V("1.5.1-rc.3"), UpdateChannel.Alpha)!.Tag);
        Assert.Null(Updater.Newest(list, V("1.5.1-rc.4"), UpdateChannel.Alpha));

        // The release itself beats its candidates, on both channels; and alpha goes on to the next candidate after it.
        Release[] released = [.. list, Rel("v1.5.1"), Rel("alpha-v1.5.2-rc.1", prerelease: true)];
        Assert.Equal("v1.5.1", Updater.Newest(released, V("1.5.0"), UpdateChannel.Stable)!.Tag);
        Assert.Equal("v1.5.1", Updater.Newest(released, V("1.5.1-rc.2"), UpdateChannel.Stable)!.Tag); // a candidate back on stable: the next stable at or above its numbers
        Assert.Null(Updater.Newest(released, V("1.5.1"), UpdateChannel.Stable));
        Assert.Equal("alpha-v1.5.2-rc.1", Updater.Newest(released, V("1.5.1-rc.2"), UpdateChannel.Alpha)!.Tag);
        Assert.Equal("alpha-v1.5.2-rc.1", Updater.Newest(released, V("1.5.1"), UpdateChannel.Alpha)!.Tag);

        Assert.True(Rel("v1.5.0").IsStable && !Rel("v1.5.0").IsAlpha && Rel("v1.5.0").In(UpdateChannel.Stable) && Rel("v1.5.0").In(UpdateChannel.Alpha));
        Assert.True(Rel("alpha-v1.5.1-rc.1", prerelease: true) is { IsStable: false, IsAlpha: true } rc && !rc.In(UpdateChannel.Stable) && rc.In(UpdateChannel.Alpha));
        Assert.Null(Rel("alpha-v1.7.0").Version);
        Assert.True(Updater.Takes(UpdateChannel.Stable, V("1.5.1")) && !Updater.Takes(UpdateChannel.Stable, V("1.5.1-rc.1")));
        Assert.True(Updater.Takes(UpdateChannel.Alpha, V("1.5.1")) && Updater.Takes(UpdateChannel.Alpha, V("1.5.1-rc.1")) && !Updater.Takes(UpdateChannel.Alpha, V("1.5.1-beta.1")));
    }

    [Fact]
    public void Return_to_stable_is_offered_only_to_a_release_candidate_on_stable_with_no_stable_release_to_wait_for()
    {
        Release[] list = [Rel("v1.4.9"), Rel("v1.5.0"), Rel("alpha-v1.5.1-rc.1", prerelease: true), Rel("alpha-v1.5.1-rc.2", prerelease: true)];
        Assert.Equal("v1.5.0", Updater.StableReturn(list, V("1.5.1-rc.2"), UpdateChannel.Stable)!.Tag);
        Assert.Null(Updater.StableReturn(list, V("1.5.1-rc.2"), UpdateChannel.Alpha));   // on alpha: nothing to leave
        Assert.Null(Updater.StableReturn(list, V("1.5.0"), UpdateChannel.Stable));        // a stable release runs
        Assert.Null(Updater.StableReturn(list, V("1.5.1-dev.abc"), UpdateChannel.Stable)); // a development build is nobody's release candidate
        Assert.Null(Updater.StableReturn([Rel("alpha-v1.5.1-rc.2", prerelease: true)], V("1.5.1-rc.2"), UpdateChannel.Stable)); // no stable release at all
        // A stable release at or above its numbers exists: that is an ordinary update, not a return.
        Assert.Null(Updater.StableReturn([.. list, Rel("v1.5.1")], V("1.5.1-rc.2"), UpdateChannel.Stable));
        Assert.Null(Updater.StableReturn([.. list, Rel("v1.6.0")], V("1.5.1-rc.2"), UpdateChannel.Stable));
    }

    [Fact]
    public void The_channel_round_trips_through_client_toml_and_is_written_only_off_the_default()
    {
        Assert.Equal(UpdateChannel.Stable, ClientConfig.Default().Settings.UpdateChannel);
        Assert.DoesNotContain("update_channel", ClientConfig.Default().ToToml(), StringComparison.Ordinal);
        var alpha = new ClientConfig { Settings = new LauncherSettings { UpdateChannel = UpdateChannel.Alpha } };
        Assert.Contains("\nupdate_channel = \"alpha\"\n", alpha.ToToml(), StringComparison.Ordinal);
        Assert.Equal(UpdateChannel.Alpha, ClientConfig.Parse(alpha.ToToml()).Settings.UpdateChannel);
        Assert.True(ClientConfig.Parse(alpha.ToToml()).SameAs(alpha));
        // Back to stable: the key goes again.
        var stable = ClientConfig.Parse(alpha.ToToml()) with { Settings = alpha.Settings with { UpdateChannel = UpdateChannel.Stable } };
        Assert.DoesNotContain("update_channel", stable.ToToml(), StringComparison.Ordinal);
        Assert.Equal(UpdateChannel.Alpha, ClientConfig.Parse("update_channel = \"Alpha\"\n").Settings.UpdateChannel);
        Assert.Equal(UpdateChannel.Stable, ClientConfig.Parse("update_channel = \"stable\"\n").Settings.UpdateChannel);
        // Anything else (a channel this launcher does not know, a wrong type) is stable.
        Assert.Equal(UpdateChannel.Stable, ClientConfig.Parse("update_channel = \"beta\"\n").Settings.UpdateChannel);
        Assert.Equal(UpdateChannel.Stable, ClientConfig.Parse("update_channel = true\n").Settings.UpdateChannel);
        Assert.Equal("1.5.1-rc.2 (alpha channel)", UpdateChannels.Describe("1.5.1-rc.2", UpdateChannel.Alpha));
        Assert.Equal("1.5.0 (stable channel)", UpdateChannels.Describe("1.5.0", UpdateChannel.Stable));
    }

    [Fact]
    public void Whats_new_shows_the_test_versions_notes_on_alpha_and_folds_them_into_the_release_on_stable()
    {
        const string text = "## 1.5.2-rc.1 — 2026-12-02\n\nnext\n\n## 1.5.1 — 2026-12-01\n\nreleased\n\n## 1.5.1-rc.2 — 2026-11-29\n\nsecond\n\n## 1.5.1-rc.1 — 2026-11-28\n\nfirst\n\n## 1.5.0 — 2026-11-27\n\nold\n\n## 0.0.1-rc.1 — 2026-11-14\n\nthe first\n";
        var all = Changelog.Parse(text, "1.5.1");
        Assert.Equal(["1.5.2-rc.1", "1.5.1", "1.5.1-rc.2", "1.5.1-rc.1", "1.5.0", "0.0.1-rc.1"], Changelog.For(all, UpdateChannel.Alpha).Select(r => r.Version));
        // Stable: 1.5.1's candidates are in 1.5.1; a candidate with no release yet (and the project's first ones) stays.
        Assert.Equal(["1.5.2-rc.1", "1.5.1", "1.5.0", "0.0.1-rc.1"], Changelog.For(all, UpdateChannel.Stable).Select(r => r.Version));
        // The version that runs is always there.
        Assert.Equal(["1.5.2-rc.1", "1.5.1", "1.5.1-rc.2", "1.5.0", "0.0.1-rc.1"], Changelog.For(Changelog.Parse(text, "1.5.1-rc.2"), UpdateChannel.Stable).Select(r => r.Version));
    }

    // ---- the installer: never down a version, except "Return to Stable" ---------------------------

    [Fact]
    public void The_installer_refuses_an_older_version_unless_it_is_the_return_from_a_test_version_to_a_stable_release()
    {
        static UpdatePlan Plan(string from, string to, bool back) =>
            new() { Root = "C:\\mkwmp", Version = to, FromVersion = from, Kind = UpdatePlan.KindZip, Asset = Updater.ZipAsset(to), ReturnToStable = back };

        // The ordinary update: up, or the same version again (a repair).
        UpdateInstaller.CheckDirection(Plan("1.5.0", "1.5.1", false), "1.5.0");
        UpdateInstaller.CheckDirection(Plan("1.5.0", "1.5.1-rc.1", false), "1.5.0");
        UpdateInstaller.CheckDirection(Plan("1.5.1-rc.2", "1.5.1", false), "1.5.1-rc.2");
        UpdateInstaller.CheckDirection(Plan("1.5.0", "1.5.0", false), "1.5.0");
        UpdateInstaller.CheckDirection(Plan("", "1.5.0", false), null); // a launcher that does not say what it is (a test build): as before
        // Down, without the player's "Return to Stable": never.
        Assert.Contains("older than the installed 1.5.1-rc.2", Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("1.5.1-rc.2", "1.5.0", false), "1.5.1-rc.2")).Message, StringComparison.Ordinal);
        Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("1.5.1", "1.5.0", false), "1.5.1"));
        Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("1.5.1", "1.5.1-rc.2", false), "1.5.1"));
        Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("1.5.1-rc.2", "1.5.1-rc.1", false), "1.5.1-rc.2"));
        // What is really installed counts, not what the plan says of it.
        Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("1.4.0", "1.5.0", false), "1.5.1"));

        // "Return to Stable": from a test version down to a stable release, and nothing else.
        UpdateInstaller.CheckDirection(Plan("1.5.1-rc.2", "1.5.0", true), "1.5.1-rc.2");
        Assert.Contains("only from a test version", Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("1.5.1", "1.5.0", true), "1.5.1")).Message, StringComparison.Ordinal); // from a stable release
        Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("1.5.1-rc.2", "1.5.1-rc.1", true), "1.5.1-rc.2")); // to a test version
        Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("1.5.1-rc.2", "1.5.1", true), "1.5.1-rc.2"));      // not a way down: an ordinary update
        Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("", "1.5.0", true), null));                      // from nobody knows what
        Assert.Throws<UpdateException>(() => UpdateInstaller.CheckDirection(Plan("1.5.1-rc.2", "1.5.0", true), "1.5.1"));         // the plan lies about what is installed
    }

    [Fact]
    public void A_staged_older_release_is_installed_only_with_return_to_stable_and_leaves_the_players_files()
    {
        var plan = Staged(Release150(), version: "1.5.0") with { FromVersion = "1.5.1-rc.2" };
        File.WriteAllText(plan.LauncherPath, "launcher 1.5.1-rc.2");
        Directory.CreateDirectory(Path.Combine(plan.Root, "mods", "neon"));
        File.WriteAllText(Path.Combine(plan.Root, "mods", "neon", "mod.json"), "the player's mod");
        var log = new List<string>();

        // Without the flag: refused before anything is touched.
        var refused = Assert.Throws<UpdateException>(() => Installer(log).Apply(plan));
        Assert.Contains("1.5.0 is older than the installed 1.5.1-rc.2", refused.Message, StringComparison.Ordinal);
        Assert.Equal("launcher 1.5.1-rc.2", Read(plan, "mkwmp.exe"));
        Assert.False(Directory.Exists(Path.Combine(plan.Transaction, "incoming")));
        // The helper says so and starts the launcher that is there again.
        var started = new List<string?>();
        Assert.Equal(UpdateOutcome.Kept, new UpdateRunner(Installer(), _ => { }) { StartLauncher = (_, why) => { started.Add(why); return true; } }.Run(plan, 0));
        Assert.Contains("older than the installed", Assert.Single(started)!, StringComparison.Ordinal);

        // The flag survives the plan file, and only it lets the install through.
        var back = plan with { ReturnToStable = true };
        back.Write();
        Assert.Contains("\"return_to_stable\": true", File.ReadAllText(back.PlanPath), StringComparison.Ordinal);
        Assert.True(UpdatePlan.Read(back.PlanPath).ReturnToStable);
        plan.Write();
        Assert.DoesNotContain("return_to_stable", File.ReadAllText(plan.PlanPath), StringComparison.Ordinal);
        Assert.False(UpdatePlan.Read(plan.PlanPath).ReturnToStable);

        var installed = Installer(log).Apply(back);
        installed.Commit();
        Assert.Equal(("launcher 1.5.0", "helper 1.5.0", "{ \"new\": \"1.5.0\" }"), (Read(back, "mkwmp.exe"), Read(back, "updater.exe"), Read(back, "i18n/en.json")));
        // The player's own: settings, logs, mods.
        Assert.Equal(("the player's settings", "a session", "the player's mod"), (Read(back, "client.toml"), Read(back, "logs/launcher-1.log"), Read(back, "mods/neon/mod.json")));
        // The signature and the hash are checked exactly as for any update.
        var tampered = Staged(Release150(), version: "1.5.0") with { FromVersion = "1.5.1-rc.2", ReturnToStable = true };
        File.WriteAllBytes(tampered.PackagePath, Zip(("mkwmp/mkwmp.exe", "not the release")));
        Assert.Contains("checksum mismatch", Assert.Throws<UpdateException>(() => Installer().Apply(tampered)).Message, StringComparison.Ordinal);
        File.WriteAllText(tampered.SignaturePath, FakeGitHub.Sign("another list"));
        Assert.Throws<UpdateException>(() => Installer().Apply(tampered));
    }

    // ---- the launcher ---------------------------------------------------------------------------

    /// <summary>A launcher folder as <see cref="Launch"/> makes it, running <paramref name="version"/> on <paramref name="channel"/>, with its own helper installed.</summary>
    private static string HomeOf(string version, UpdateChannel channel)
    {
        _ = Rig.TestExe;
        var root = Rig.NewFolder("channel");
        var home = Path.Combine(root, "launcher");
        var game = Path.Combine(root, "mkw");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(Path.Combine(home, "i18n"));
        Directory.CreateDirectory(Path.Combine(home, "logs"));
        File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
        File.WriteAllText(Path.Combine(home, "mkwmp.exe"), $"launcher {version}");
        File.WriteAllText(Path.Combine(home, "updater.exe"), $"helper {version}");
        File.WriteAllText(Path.Combine(home, "i18n", "en.json"), "{ \"old\": \"" + version + "\" }");
        new ClientConfig { Settings = new LauncherSettings { Game = game, UpdateChannel = channel, Ui = new UiSettings { Theme = "dark", Locale = "pt-BR" } } }.Save(Path.Combine(home, "client.toml"));
        return home;
    }

    private static byte[] ReleaseZip(string version) => Zip(
        ("mkwmp/i18n/en.json", "{ \"new\": \"" + version + "\" }"),
        ("mkwmp/mkwmp.exe", $"launcher {version}"),
        ("mkwmp/updater.exe", $"helper {version}"));

    /// <summary>GitHub with the stable 1.5.0 and the release candidates given, cut as release.sh cuts them (pre-releases, <c>alpha-v…</c>).</summary>
    private static FakeGitHub GitHubWithCandidates(params string[] candidates)
    {
        var github = new FakeGitHub().Release("1.5.0", new() { [Updater.ZipAsset("1.5.0")] = ReleaseZip("1.5.0") }, notes: "stable notes");
        foreach (var rc in candidates)
        {
            github.Release(rc, new() { [Updater.ZipAsset(rc)] = ReleaseZip(rc) }, notes: $"test notes of {rc}", prerelease: true, tag: Release.AlphaTagPrefix + rc);
        }
        return github;
    }

    [Fact]
    public async Task A_stable_launcher_is_never_given_a_release_candidate_and_alpha_is_required_to_take_the_newest_at_once()
    {
        var github = GitHubWithCandidates("1.5.1-rc.1", "1.5.1-rc.2");
        var home = HomeOf("1.5.0", UpdateChannel.Stable);
        var (launcher, _, exe, _, helpers) = Launch(github, home, version: "1.5.0", require: true);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            // Stable: up to date, nothing required, nothing downloaded, however many test versions are out.
            Assert.Equal((UpdateState.UpToDate, false, null, null), (launcher.Update.State, launcher.Update.Required, launcher.Update.AvailableVersion, launcher.Update.StableReturn));
            Assert.DoesNotContain(github.Requests, r => r.Name.EndsWith(".zip", StringComparison.Ordinal));
            await launcher.CheckForUpdatesAsync();
            Assert.Equal((UpdateState.UpToDate, false), (launcher.Update.State, launcher.Update.Required));
            Assert.False(Directory.Exists(Path.Combine(home, ".mkwmp-selfupdate", "1.5.1-rc.2")));

            // Settings › Updates › Alpha: saved, looked for at once, and the newest test version is required like any update.
            await launcher.SetUpdateChannelAsync(UpdateChannel.Alpha);
            Assert.Equal(UpdateChannel.Alpha, launcher.Settings.UpdateChannel);
            Assert.Contains("update_channel = \"alpha\"", File.ReadAllText(Path.Combine(home, "client.toml")), StringComparison.Ordinal);
            Assert.Equal((UpdateState.ReadyToInstall, "1.5.1-rc.2", true, "test notes of 1.5.1-rc.2"), (launcher.Update.State, launcher.Update.AvailableVersion, launcher.Update.Required, launcher.Update.Notes));
            await launcher.PlayAsync();
            Assert.False(launcher.Session.Busy); // required: nothing is played before it
            Assert.Equal("launcher 1.5.0", File.ReadAllText(exe));

            await launcher.ApplyUpdateAsync(default);
            var started = Assert.Single(helpers);
            var plan = UpdatePlan.Read(UpdaterArgs.Parse(started.Arguments, out _)!.Plan!);
            Assert.Equal(("1.5.1-rc.2", "1.5.0", false), (plan.Version, plan.FromVersion, plan.ReturnToStable));
            // An ordinary update: the new release's own helper, and the same signature and hash check as a stable one.
            Assert.Equal("helper 1.5.1-rc.2", File.ReadAllText(started.Helper));
            Assert.Equal(UpdateOutcome.Installed, new UpdateRunner(Installer(), _ => { }) { StartLauncher = (_, _) => true }.Run(plan, 0));
            Assert.Equal("launcher 1.5.1-rc.2", File.ReadAllText(exe));
        }
    }

    [Fact]
    public async Task A_release_candidate_download_that_does_not_verify_is_refused_like_any_other()
    {
        var name = Updater.ZipAsset("1.5.1-rc.1");
        // The list is signed, but names another file's hash: the download is thrown away.
        var github = new FakeGitHub().Release("1.5.1-rc.1", new() { [name] = ReleaseZip("1.5.1-rc.1") }, listed: new() { [name] = "something else"u8.ToArray() },
            prerelease: true, tag: Release.AlphaTagPrefix + "1.5.1-rc.1");
        var home = HomeOf("1.5.0", UpdateChannel.Alpha);
        var (launcher, _, exe, _, helpers) = Launch(github, home, version: "1.5.0", require: true);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            Assert.Equal((UpdateState.Failed, true), (launcher.Update.State, launcher.Update.Required));
            Assert.Contains("checksum mismatch", launcher.Update.Status, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(home, ".mkwmp-selfupdate", "1.5.1-rc.1", "package.zip")));
            Assert.Empty(helpers);
            Assert.Equal("launcher 1.5.0", File.ReadAllText(exe));
        }
    }

    [Fact]
    public async Task Leaving_alpha_on_a_release_candidate_offers_the_return_to_stable_and_installs_it_only_when_asked()
    {
        var github = GitHubWithCandidates("1.5.1-rc.1", "1.5.1-rc.2", "1.5.1-rc.3");
        var home = HomeOf("1.5.1-rc.2", UpdateChannel.Alpha);
        Directory.CreateDirectory(Path.Combine(home, "mods"));
        File.WriteAllText(Path.Combine(home, "mods", "mine.json"), "the player's mod");
        File.WriteAllText(Path.Combine(home, "logs", "launcher-1.log"), "a session");
        var stage = Path.Combine(home, ".mkwmp-selfupdate");
        var (launcher, _, exe, game, helpers) = Launch(github, home, version: "1.5.1-rc.2", require: false);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            // On alpha: the next test version is on offer, and it is downloaded.
            Assert.Equal((UpdateState.Available, "1.5.1-rc.3", null), (launcher.Update.State, launcher.Update.AvailableVersion, launcher.Update.StableReturn));
            await launcher.DownloadUpdateAsync(default);
            Assert.Equal(UpdateState.ReadyToInstall, launcher.Update.State);
            Assert.True(Directory.Exists(Path.Combine(stage, "1.5.1-rc.3")));
            // Not on offer while on alpha: asking does nothing.
            await launcher.ReturnToStableAsync(default);
            Assert.Empty(helpers);

            // Settings › Updates › Stable: the downloaded test version is no longer offered; no stable release has
            // reached 1.5.1 yet, so the newest stable one, 1.5.0, is named as the way back. Nothing is required,
            // nothing is downloaded, nothing is installed.
            var before = github.Requests.Count;
            await launcher.SetUpdateChannelAsync(UpdateChannel.Stable);
            Assert.DoesNotContain("update_channel", File.ReadAllText(Path.Combine(home, "client.toml")), StringComparison.Ordinal);
            Assert.Equal((UpdateState.UpToDate, null, false, "1.5.0"), (launcher.Update.State, launcher.Update.AvailableVersion, launcher.Update.Required, launcher.Update.StableReturn));
            Assert.DoesNotContain(github.Requests.Skip(before), r => r.Name.EndsWith(".zip", StringComparison.Ordinal));
            // The player declines: this version stays, and installing "the update" installs nothing (there is none).
            await launcher.ApplyUpdateAsync(default);
            await launcher.CheckForUpdatesAsync();
            Assert.Empty(helpers);
            Assert.Equal((UpdateState.UpToDate, "1.5.0"), (launcher.Update.State, launcher.Update.StableReturn));
            Assert.Equal("launcher 1.5.1-rc.2", File.ReadAllText(exe));

            // Never under a game.
            game.Running = true;
            await launcher.ReturnToStableAsync(default);
            Assert.Empty(helpers);
            Assert.Equal((UpdateState.Failed, Strings.UpdateNeedsNoGame, false), (launcher.Update.State, launcher.Update.Status, launcher.Update.Required));
            game.Running = false;

            // "Return to Stable", confirmed: 1.5.0 is downloaded, verified, and handed to the helper that is installed
            // (this version's: it knows the rule), with the plan saying what it is.
            var settings = File.ReadAllText(Path.Combine(home, "client.toml"));
            await launcher.ReturnToStableAsync(default);
            Assert.Equal((UpdateState.Restarting, "1.5.0"), (launcher.Update.State, launcher.Update.AvailableVersion));
            var started = Assert.Single(helpers);
            Assert.Equal(Path.Combine(stage, "1.5.0", "worker.exe"), started.Helper);
            Assert.Equal("helper 1.5.1-rc.2", File.ReadAllText(started.Helper));
            var plan = UpdatePlan.Read(UpdaterArgs.Parse(started.Arguments, out _)!.Plan!);
            Assert.Equal(("1.5.0", "1.5.1-rc.2", true, home), (plan.Version, plan.FromVersion, plan.ReturnToStable, plan.Root));
            // The test version that was waiting is gone: the stable launcher finds nothing to "update" to.
            Assert.False(Directory.Exists(Path.Combine(stage, "1.5.1-rc.3")));
            Assert.Equal("launcher 1.5.1-rc.2", File.ReadAllText(exe)); // the launcher itself swaps nothing

            // The same staged files without the flag are not installed by the helper.
            (plan with { ReturnToStable = false }).Write();
            Assert.Equal(UpdateOutcome.Kept, new UpdateRunner(Installer(), _ => { }) { StartLauncher = (_, _) => true }.Run(UpdatePlan.Read(plan.PlanPath), 0));
            Assert.Equal("launcher 1.5.1-rc.2", File.ReadAllText(exe));
            // With it: installed, and the player's own files are as they were.
            plan.Write();
            Assert.Equal(UpdateOutcome.Installed, new UpdateRunner(Installer(), _ => { }) { StartLauncher = (_, _) => true }.Run(UpdatePlan.Read(plan.PlanPath), 0));
            Assert.Equal(("launcher 1.5.0", "helper 1.5.0"), (File.ReadAllText(exe), File.ReadAllText(Path.Combine(home, "updater.exe"))));
            Assert.Equal((settings, "the player's mod", "a session"),
                (File.ReadAllText(Path.Combine(home, "client.toml")), File.ReadAllText(Path.Combine(home, "mods", "mine.json")), File.ReadAllText(Path.Combine(home, "logs", "launcher-1.log"))));
        }

        // The stable launcher that starts next: on stable, up to date, no way back on offer, and no test version in sight.
        var (stable, _, _, _, again) = Launch(github, home, version: "1.5.0", require: true);
        await using (stable)
        {
            await stable.StartAsync(default);
            await stable.StartupUpdates;
            Assert.Equal((UpdateChannel.Stable, UpdateState.UpToDate, false, null), (stable.Settings.UpdateChannel, stable.Update.State, stable.Update.Required, stable.Update.StableReturn));
            Assert.False(Directory.Exists(Path.Combine(stage, "1.5.0")));
            Assert.Empty(again);
        }
    }

    [Fact]
    public async Task A_release_candidate_on_stable_is_required_to_take_the_next_stable_release_and_is_offered_no_return_then()
    {
        var github = GitHubWithCandidates("1.5.1-rc.2", "1.5.2-rc.1").Release("1.5.1", new() { [Updater.ZipAsset("1.5.1")] = ReleaseZip("1.5.1") });
        var home = HomeOf("1.5.1-rc.2", UpdateChannel.Stable);
        var (launcher, _, _, _, helpers) = Launch(github, home, version: "1.5.1-rc.2", require: true);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            // 1.5.1 is at its numbers: the ordinary, required update. Not 1.5.2-rc.1, and no return to 1.5.0.
            Assert.Equal((UpdateState.ReadyToInstall, "1.5.1", true, null), (launcher.Update.State, launcher.Update.AvailableVersion, launcher.Update.Required, launcher.Update.StableReturn));
            await launcher.ReturnToStableAsync(default);
            Assert.Empty(helpers);
            await launcher.ApplyUpdateAsync(default);
            var plan = UpdatePlan.Read(UpdaterArgs.Parse(Assert.Single(helpers).Arguments, out _)!.Plan!);
            Assert.Equal(("1.5.1", false), (plan.Version, plan.ReturnToStable));
        }
    }

    [Fact]
    public async Task A_release_candidate_on_stable_is_never_required_to_go_back_and_a_stable_release_is_offered_no_return()
    {
        var github = GitHubWithCandidates("1.5.1-rc.2");
        var home = HomeOf("1.5.1-rc.2", UpdateChannel.Stable);
        var (launcher, _, exe, _, helpers) = Launch(github, home, version: "1.5.1-rc.2", require: true);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            // A real launcher (updates are required): the way back is named, and that is all. It plays as it is.
            Assert.Equal((UpdateState.UpToDate, false, "1.5.0"), (launcher.Update.State, launcher.Update.Required, launcher.Update.StableReturn));
            Assert.DoesNotContain(github.Requests, r => r.Name.EndsWith(".zip", StringComparison.Ordinal));
            Assert.Empty(helpers);
            Assert.Equal("launcher 1.5.1-rc.2", File.ReadAllText(exe));
        }

        // A stable release on stable: the launcher refuses to "return" anywhere, whatever is asked of it.
        var plain = HomeOf("1.5.0", UpdateChannel.Stable);
        var (stable, _, _, _, none) = Launch(new FakeGitHub().Release("1.4.9", new() { [Updater.ZipAsset("1.4.9")] = ReleaseZip("1.4.9") }), plain, version: "1.5.0", require: true);
        await using (stable)
        {
            await stable.StartAsync(default);
            await stable.StartupUpdates;
            Assert.Equal((UpdateState.UpToDate, null), (stable.Update.State, stable.Update.StableReturn));
            await stable.ReturnToStableAsync(default);
            Assert.Empty(none);
            Assert.False(Directory.Exists(Path.Combine(plain, ".mkwmp-selfupdate", "1.4.9")));
        }
    }

    [Fact]
    public async Task Offline_a_release_candidate_downloaded_on_alpha_is_not_offered_on_stable()
    {
        var github = GitHubWithCandidates("1.5.1-rc.1");
        var home = HomeOf("1.5.0", UpdateChannel.Alpha);
        var (alpha, _, _, _, _) = Launch(github, home, version: "1.5.0");
        await using (alpha)
        {
            await alpha.StartAsync(default);
            await alpha.StartupUpdates;
            await alpha.DownloadUpdateAsync(default);
            Assert.Equal((UpdateState.ReadyToInstall, "1.5.1-rc.1"), (alpha.Update.State, alpha.Update.AvailableVersion));
        }
        // The same folder, offline: alpha still has its download...
        github.Down = true;
        var (offline, _, _, _, _) = Launch(github, home, version: "1.5.0", require: true);
        await using (offline)
        {
            await offline.StartAsync(default);
            await offline.StartupUpdates;
            Assert.Equal((UpdateState.ReadyToInstall, "1.5.1-rc.1"), (offline.Update.State, offline.Update.AvailableVersion));
            // ...and back on stable it is not an update any more.
            await offline.SetUpdateChannelAsync(UpdateChannel.Stable);
            Assert.NotEqual(UpdateState.ReadyToInstall, offline.Update.State);
            Assert.Null(offline.Update.AvailableVersion);
        }
        // The next start on stable clears it away.
        var (stable, _, _, _, helpers) = Launch(github, home, version: "1.5.0", require: true);
        await using (stable)
        {
            await stable.StartAsync(default);
            await stable.StartupUpdates;
            Assert.Equal((UpdateState.UpToDate, false), (stable.Update.State, stable.Update.Required));
            Assert.False(Directory.Exists(Path.Combine(home, ".mkwmp-selfupdate", "1.5.1-rc.1")));
            Assert.Empty(helpers);
        }
    }

    [Fact]
    public void A_release_candidates_own_game_package_is_taken_by_that_launcher_and_no_other()
    {
        var d = Rig.NewFolder("bundle-rc");
        var assets = new MkwRecompTarget("1.5.1-rc.1").PackageAssets;
        File.WriteAllText(Path.Combine(d, "winmm.dll"), "p");
        File.WriteAllText(Path.Combine(d, "mkwmp-voice.dll"), "v");
        var list = $"# mkwmp 1.5.1-rc.1\n{Updater.Sha256Hex("p"u8)}  winmm.dll\n{Updater.Sha256Hex("v"u8)}  mkwmp-voice.dll\n";
        File.WriteAllText(Path.Combine(d, "SHA256SUMS"), list);
        File.WriteAllText(Path.Combine(d, "SHA256SUMS.sig"), FakeGitHub.Sign(list));
        Assert.Equal("1.5.1-rc.1", Launcher.Bundled(d, "1.5.1-rc.1", TestKeys, assets)!.Version);
        // The version gate holds between a candidate and its release, both ways, and between candidates.
        Assert.Null(Launcher.Bundled(d, "1.5.1", TestKeys, assets));
        Assert.Null(Launcher.Bundled(d, "1.5.1-rc.2", TestKeys, assets));
        Assert.Null(Launcher.Bundled(d, "1.5.0", TestKeys, assets));
    }
}

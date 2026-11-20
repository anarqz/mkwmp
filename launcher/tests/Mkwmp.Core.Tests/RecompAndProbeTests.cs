// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core.Tests;

public class RecompTests
{
    private const string Id13 = "c0dd6cf12c07c23a";
    private const string Id15 = "c9bbcb75a9ac9a49";

    [Fact]
    public void Only_supported_releases_pass()
    {
        var unknown = Recomp.IdentifyBytes("not mkw.exe"u8);
        Assert.Equal(RecompKind.Unknown, unknown.Kind);
        Assert.False(unknown.Ok);
        Assert.Contains("0.1.13", unknown.Problem, StringComparison.Ordinal);
        Assert.Contains("0.1.11: MKWMP needs 0.1.10 or 0.1.12 or 0.1.13 or 0.1.15 or 0.1.15-mkwmp", new RecompId(RecompKind.Other, "0.1.11").Problem, StringComparison.Ordinal);
        Assert.Equal(RecompKind.Unknown, Recomp.IdentifyBytes([]).Kind);
        var ok = new RecompId(RecompKind.Supported, "0.1.10");
        Assert.True(ok.Ok);
        Assert.Null(ok.Problem);
        Assert.Equal("mkw-recomp 0.1.10: supported by this MKWMP.", ok.Explain);
        Assert.All(Recomp.Supported, v => Assert.Contains(Recomp.Known, k => k.Version == v)); // every supported release has a known hash
        Assert.All(Recomp.Known, k => Assert.Equal(64, k.Sha256.Length));
        Assert.Contains(Recomp.Recommended, Recomp.Supported);
        Assert.EndsWith(Recomp.Recommended, Recomp.Download, StringComparison.Ordinal);
    }

    [Fact]
    public void Identify_reads_the_folder_and_notices_a_new_exe()
    {
        var d = Rig.NewFolder("recomp");
        Assert.Equal(RecompKind.Missing, Recomp.Identify(d).Kind);
        File.WriteAllText(Path.Combine(d, "mkw.exe"), "some build");
        Assert.Equal(RecompKind.Unknown, Recomp.Identify(d).Kind);
        File.WriteAllBytes(Path.Combine(d, "mkw.exe"), Rig.TestExe);
        Assert.Equal(new RecompId(RecompKind.Supported, "0.1.10"), Recomp.Identify(d));
        var install = new MkwRecompTarget("1.4.0").Detect(d)!;
        Assert.Equal(("mkw-recomp", "0.1.10", (string?)null, d), (install.TargetId, install.Version, install.GameVersion, install.DataRoot));
        Assert.Null(new MkwRecompTarget("1.4.0").Detect(Path.Combine(d, "nothing")));
        Assert.Null(new MkwRecompTarget("1.4.0").Detect("  "));
    }

    [Fact]
    public void Data_dir_follows_game_dir_in_mkw_ini()
    {
        var d = Rig.NewFolder("recomp-dir");
        var ini = Path.Combine(d, "mkw.ini");
        Assert.Equal(d, Recomp.DataDir(d));
        File.WriteAllText(ini, "[launcher]\nupdate_check = 1\n");
        Assert.Equal(d, Recomp.DataDir(d));
        File.WriteAllText(ini, "[launcher]\ngame_dir = \"/games/mkw data\"\n");
        Assert.Equal("/games/mkw data", Recomp.DataDir(d));
        File.WriteAllText(ini, "GAME_DIR = \"D:\\\\MKW\"\r\n");
        Assert.Equal("D:\\\\MKW", Recomp.DataDir(d));
        File.WriteAllText(ini, "game_dir = \"install\"\n");
        Assert.Equal(Path.Combine(d, "install"), Recomp.DataDir(d));
    }

    [Fact]
    public void The_mod_is_found_only_when_enabled()
    {
        Assert.True(Recomp.ModsListHas("mkwmp", "mkwmp"));
        Assert.True(Recomp.ModsListHas("other,mkwmp", "mkwmp") && Recomp.ModsListHas("other, mkwmp", "mkwmp"));
        Assert.True(Recomp.ModsListHas("\"other,mkwmp\"", "mkwmp"));
        Assert.True(Recomp.ModsListHas("MKWMP", "mkwmp")); // any case, like the probe
        Assert.False(Recomp.ModsListHas("mkwmp2,other", "mkwmp") || Recomp.ModsListHas("", "mkwmp"));
        Assert.Equal("other,mkwmp", Recomp.ModsIn("[launcher]\r\nmods = other,mkwmp\r\n"));
        Assert.Null(Recomp.ModsIn("[video]\nmods = mkwmp\n")); // only under [launcher]
        if (Environment.GetEnvironmentVariable("MKW_MODS") is not null)
        {
            return; // the environment would win over the files below
        }
        var d = Rig.NewFolder("recomp-mod");
        var ini = Path.Combine(d, "mkw.ini");
        Directory.CreateDirectory(Path.Combine(d, "mods", "mkwmp", "plugin"));
        Assert.Null(Recomp.ModDir(d));
        File.WriteAllText(ini, "[launcher]\nmods = mkwmp\n");
        Assert.Null(Recomp.ModDir(d)); // enabled, but no plugin/mkwmp.dll
        File.WriteAllText(Path.Combine(d, "mods", "mkwmp", "plugin", "mkwmp.dll"), "mod");
        Assert.Equal(Path.Combine(d, "mods", "mkwmp"), Recomp.ModDir(d));
        Assert.Null(Recomp.ActiveMod(d)); // no runtime there to load it
        File.WriteAllText(ini, "[launcher]\nmods = other\n");
        Assert.Null(Recomp.ModDir(d)); // installed but not enabled
        // 0.1.12+ "choose game folder": mods/ and the mods key live in the data dir.
        Directory.CreateDirectory(Path.Combine(d, "data", "mods", "mkwmp", "plugin"));
        File.WriteAllText(Path.Combine(d, "data", "mods", "mkwmp", "plugin", "mkwmp.dll"), "mod");
        File.WriteAllText(ini, "[launcher]\ngame_dir = \"data\"\nmods = mkwmp\n");
        Assert.Null(Recomp.ModDir(d)); // the data dir's mkw.ini decides
        File.WriteAllText(Path.Combine(d, "data", "mkw.ini"), "[launcher]\nmods = mkwmp\n");
        Assert.Equal(Path.Combine(d, "data", "mods", "mkwmp"), Recomp.ModDir(d));
    }

    [Fact]
    public void A_game_set_up_by_another_runtime_needs_a_rebuild()
    {
        static RecompId S(string v) => new(RecompKind.Supported, v);
        Assert.Contains("open mkw-recomp once", Recomp.RebuildProblemFor(S("0.1.15"), Id13), StringComparison.Ordinal);
        Assert.Null(Recomp.RebuildProblemFor(S("0.1.15"), Id15));
        Assert.Null(Recomp.RebuildProblemFor(S("0.1.13"), Id13));
        Assert.NotNull(Recomp.RebuildProblemFor(S("0.1.13"), Id15));
        Assert.Null(Recomp.RebuildProblemFor(S("0.1.15"), null)); // unknown is allowed
        Assert.Null(Recomp.RebuildProblemFor(S("0.1.15-mkwmp"), Id13)); // the fork keeps 0.1.13's id
        Assert.NotNull(Recomp.RebuildProblemFor(S("0.1.15-mkwmp"), Id15)); // coming from official 0.1.15
        Assert.Null(Recomp.RebuildProblemFor(S("0.1.10"), "anything")); // no id known for 0.1.10
        Assert.Null(Recomp.RebuildProblemFor(new RecompId(RecompKind.Unknown, null), Id13));
        Assert.Equal(Id15, Recomp.IdWord("C9BBCB75A9AC9A49\n"));
        Assert.Null(Recomp.IdWord("not an id at all"));
        Assert.Equal(Id15, Recomp.IdWord($"{Id15} 1.8.0 windows"));
        Assert.Equal(Id15, Recomp.GameIdIn("{\"done\":{\"generate\":\"" + Id15 + " 1.8.0 ab\"}}"));

        var d = Rig.NewFolder("recomp-id");
        Assert.Null(Recomp.GameId(d));
        File.WriteAllText(Path.Combine(d, "setup-state.json"), "{\"done\":{\"generate\":\"" + Id13 + " 1.8.0 ab\"}}");
        Assert.Equal(Id13, Recomp.GameId(d));
        File.WriteAllText(Path.Combine(d, "mkwgame.id"), Id15 + "\n");
        Assert.Equal(Id15, Recomp.GameId(d)); // mkwgame.id first

        // Through the target: the supported stand-in is 0.1.10 (no id known); the mod runtime 0.1.15 wants its own.
        File.WriteAllBytes(Path.Combine(d, "mkw.exe"), Rig.TestModExe);
        var target = new MkwRecompTarget("1.4.0");
        Assert.Equal(TargetStatus.Ready, target.Check(target.Detect(d)!).Status);
        File.WriteAllText(Path.Combine(d, "mkwgame.id"), Id13 + "\n\n");
        var check = target.Check(target.Detect(d)!);
        Assert.Equal((TargetStatus.NeedsRebuild, Recomp.RebuildProblem), (check.Status, check.Problem));
    }

    [Fact]
    public void Game_version_gate()
    {
        // Real files (paths shortened): 0.1.12 on game 1.0.0, 0.1.13 on game 1.8.0 (key order differs).
        const string V1 = """{"done":{"exefs":"C:\\x\\ROM\\base\\exefs","romfs":"copy 1.0.0","compile":"c0dd6cf12c07c23a 1.0.0 windows c5ac38fc744c91bd","shaders":"1.0.0 6a07c53dc829807b","warm":"c0dd6cf12c07c23a 1.0.0","cleanup":"","generate":"c0dd6cf12c07c23a 1.0.0 bdcbce92237e51cf"},"source":"C:\\x\\ROM","romfs_mode":"copy","version":1,"port":"0.1.12"}""";
        const string V8 = """{"done":{"exefs":"C:\\x\\updates\\917504\\exefs","romfs":"link C:\\x\\updates\\917504\\romfs","generate":"c0dd6cf12c07c23a 1.8.0 b05897af8b40fec6","compile":"c0dd6cf12c07c23a 1.8.0 windows 41829b71d97ad65a","shaders":"1.8.0 6b8e0b1447f058ce","warm":"c0dd6cf12c07c23a 1.8.0","cleanup":""},"source":"C:\\x\\updates\\917504","romfs_mode":"link","version":1,"port":"0.1.13"}""";
        Assert.Equal("1.0.0", Recomp.GameVersionIn(V1));
        Assert.Equal("1.8.0", Recomp.GameVersionIn(V8));
        // A much older recomp put a path there: unknown, not refused (its mkw.exe fails the hash check anyway).
        Assert.Null(Recomp.GameVersionIn("""{"done":{"generate":"6866c415aadb9a39 C:\\Users\\a\\MKW [ROM]\\base\\exefs"}}"""));
        Assert.Equal("1.0.0", Recomp.GameVersionIn("""{"generate": "c0dd6cf12c07c23a 1.0.0 x"}"""));
        Assert.Null(Recomp.GameVersionIn("""{"done": {}}"""));
        Assert.Null(Recomp.GameVersionIn("not json"));
        Assert.Null(Recomp.GameVersionIn("""{"done": {"generate": "c0dd6cf12c07c23a b05897af8b40fec6"}}""")); // no version word
        Assert.Equal("1.8.0", Recomp.GameVersionIn("""{"done": {"generate": "c0dd6cf12c07c23a 1.8.0"}}"""));
        Assert.Null(Recomp.GameProblemFor("1.0.0"));
        Assert.Null(Recomp.GameProblemFor("1.8.0"));
        Assert.Null(Recomp.GameProblemFor(null)); // unknown is allowed
        Assert.Equal("Game 1.9.0: MKWMP supports game 1.0.0 or 1.8.0 (a newer MKWMP may support 1.9.0)", Recomp.GameProblemFor("1.9.0"));

        var d = Rig.NewFolder("recomp-game");
        Directory.CreateDirectory(Path.Combine(d, "data"));
        Assert.Null(Recomp.GameProblem(d));
        Assert.Contains("unknown", Recomp.GameLine(d), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(d, "mkw.ini"), "game_dir = \"data\"\n");
        File.WriteAllText(Path.Combine(d, "data", "setup-state.json"), V8);
        Assert.Equal("1.8.0", Recomp.GameVersion(d)); // read from the data dir
        Assert.Null(Recomp.GameProblem(d));
        Assert.Equal("game version: 1.8.0", Recomp.GameLine(d));
        File.WriteAllText(Path.Combine(d, "data", "setup-state.json"), """{"done": {"generate": "c0dd6cf12c07c23a 1.9.0 ab"}}""");
        Assert.NotNull(Recomp.GameProblem(d));
        Assert.Contains("not supported", Recomp.GameLine(d), StringComparison.Ordinal);
        File.WriteAllBytes(Path.Combine(d, "mkw.exe"), Rig.TestExe);
        var target = new MkwRecompTarget("1.4.0");
        var install = target.Detect(d)!;
        Assert.Equal(("1.9.0", Path.Combine(d, "data")), (install.GameVersion, install.DataRoot));
        Assert.Equal(TargetStatus.Unsupported, target.Check(install).Status);
    }
}

public class ProbeTests
{
    private const string V = "1.4.0";

    private static string Dir(string name) => Rig.NewFolder("probe-" + name);

    private static string Hash(string text) => Updater.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text));

    private static IntegrationState State(string d, Expected? want, bool dev = false) => Probe.Check(d, want, V, dev).State;

    [Fact]
    public void Installs_backs_up_a_foreign_dll_once_then_updates_quietly()
    {
        var d = Dir("install");
        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "someone else's dll");
        Assert.Equal(ProbeAction.Installed, Probe.InstallProbe(d, "probe v1"u8, false, out var backup));
        Assert.Equal(Path.Combine(d, "winmm.dll.bak"), backup);
        Assert.Equal("someone else's dll", File.ReadAllText(backup!));
        Assert.Equal(ProbeAction.UpToDate, Probe.InstallProbe(d, "probe v1"u8, false, out _));
        // Our own older probe is replaced without another backup.
        Assert.Equal(ProbeAction.Installed, Probe.InstallProbe(d, "probe v2"u8, false, out backup));
        Assert.Null(backup);
        Assert.Equal("probe v2", File.ReadAllText(Path.Combine(d, Probe.ProbeDll)));
        Assert.Equal("someone else's dll", File.ReadAllText(Path.Combine(d, "winmm.dll.bak")));
    }

    [Fact]
    public void Never_touches_a_running_game()
    {
        var d = Dir("running");
        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "old");
        Assert.Equal(ProbeAction.GameRunning, Probe.InstallProbe(d, "new"u8, true, out _));
        Assert.Equal("old", File.ReadAllText(Path.Combine(d, Probe.ProbeDll)));
        Assert.Equal(ProbeAction.Installed, Probe.InstallFile(d, Probe.Voice, "v1"u8, false));
        Assert.Equal(ProbeAction.UpToDate, Probe.InstallFile(d, Probe.Voice, "v1"u8, false));
        Assert.Equal(ProbeAction.GameRunning, Probe.InstallFile(d, Probe.Voice, "v2"u8, true));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(d, Probe.Voice)));
    }

    [Fact]
    public void Uninstall_restores_the_folder()
    {
        var d = Dir("uninstall");
        File.WriteAllText(Path.Combine(d, "mkw.exe"), "");
        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "someone else's dll");
        Assert.Equal(IntegrationState.Damaged, State(d, null)); // a foreign winmm.dll is not our patch
        Probe.InstallProbe(d, "probe"u8, false, out _);
        Probe.InstallFile(d, Probe.Voice, "voice"u8, false);
        foreach (var f in new[] { "mkwmp.toml", "mkwmp-lobby-ca.pem", "mkwmp.log", Probe.Host })
        {
            File.WriteAllText(Path.Combine(d, f), "x");
        }
        Assert.Equal("Close the game first", Assert.Throws<LauncherException>(() => Probe.Uninstall(d, true)).Message);
        var done = Probe.Uninstall(d, false);
        Assert.Contains("put your previous winmm.dll back", done);
        Assert.Equal(["mkw.exe", "winmm.dll"], Directory.GetFiles(d).Select(Path.GetFileName).Order());
        Assert.Equal("someone else's dll", File.ReadAllText(Path.Combine(d, Probe.ProbeDll)));

        // A winmm.dll that is not ours is left alone.
        File.WriteAllText(Path.Combine(d, Probe.Voice), "v");
        Assert.Contains("left winmm.dll alone (not mkwmp's)", Probe.Uninstall(d, false));
        Assert.False(File.Exists(Path.Combine(d, Probe.Voice)));
        Assert.Equal(IntegrationState.Damaged, State(d, null));
        Assert.Throws<LauncherException>(() => Probe.Uninstall(Dir("empty"), false));
    }

    [Fact]
    public void The_mod_takes_the_launchers_patch_out()
    {
        var d = Dir("mod");
        File.WriteAllText(Path.Combine(d, "mkw.exe"), "exe");
        Assert.Empty(Probe.RemoveForMod(d, true)); // nothing of ours: nothing to do
        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "someone else's dll");
        Probe.InstallProbe(d, "probe v1"u8, false, out _);
        Probe.InstallFile(d, Probe.Voice, "voice"u8, false);
        File.WriteAllBytes(Path.Combine(d, Probe.Host), Rig.FakeWinmm());
        File.WriteAllText(Path.Combine(d, "mkwmp.toml"), "server");
        Assert.Throws<LauncherException>(() => Probe.RemoveForMod(d, true)); // never while the game runs
        Assert.Contains(Probe.RemoveForMod(d, false), l => l.Contains("previous winmm.dll back", StringComparison.Ordinal));
        Assert.Equal("someone else's dll", File.ReadAllText(Path.Combine(d, Probe.ProbeDll)));
        Assert.False(File.Exists(Path.Combine(d, Probe.Voice)) || File.Exists(Path.Combine(d, Probe.Host)) || File.Exists(Path.Combine(d, ".mkwmp-probe")));
        Assert.True(File.Exists(Path.Combine(d, "mkwmp.toml"))); // the mod reads the launcher's mkwmp.toml
        Assert.Empty(Probe.RemoveForMod(d, false));
    }

    [Fact]
    public void Check_tells_each_state()
    {
        var d = Dir("check");
        File.WriteAllText(Path.Combine(d, "mkw.exe"), "");
        var want = new Expected("1.0.0", Hash("p1"), Hash("v1"));
        var missing = Probe.Check(d, want, V, false);
        Assert.Equal((IntegrationState.Missing, "Not installed: press INSTALL", "no mkwmp file in the game folder"), (missing.State, missing.Summary, missing.Lines.Single()));
        Probe.InstallProbe(d, "p1"u8, false, out _);
        Probe.InstallFile(d, Probe.Voice, "v1"u8, false);
        Assert.Equal(IntegrationState.HostMissing, State(d, want));
        File.WriteAllText(Path.Combine(d, Probe.Host), "p1"); // our probe copied as the host
        Assert.Equal(IntegrationState.Damaged, State(d, want));
        File.WriteAllBytes(Path.Combine(d, Probe.Host), Rig.FakeWinmm());
        var r = Probe.Check(d, want, V, false);
        Assert.Equal(IntegrationState.Installed, r.State);
        Assert.True(r.Ok);
        Assert.Equal("Patches installed (v1.4.0)", r.Summary);
        Assert.Equal(["winmm.dll: v1.0.0, verified", "mkwmp-voice.dll: v1.0.0, verified", "winmm_host.dll: the system's winmm.dll"], r.Lines);
        var unverified = Probe.Check(d, null, V, false);
        Assert.Equal((IntegrationState.Unverified, "Patches installed (not verified: offline)"), (unverified.State, unverified.Summary));
        Assert.Equal(["winmm.dll: mkwmp's (not verified)", "mkwmp-voice.dll: present (not verified)", "winmm_host.dll: the system's winmm.dll"], unverified.Lines);
        // Copied by hand (no .mkwmp-probe), nothing to compare with: ours by content.
        File.Delete(Path.Combine(d, ".mkwmp-probe"));
        Assert.Equal(IntegrationState.Damaged, State(d, null)); // not a probe by content
        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "MZ...mkwmp probe 1.0.0...");
        Assert.Equal(IntegrationState.Unverified, State(d, null));
        Probe.InstallProbe(d, "p1"u8, false, out _);
        Probe.InstallProbe(d, "p0"u8, false, out _); // an older mkwmp probe
        var outdated = Probe.Check(d, want, V, false);
        Assert.Equal((IntegrationState.Outdated, "Outdated (other version): press INSTALL to update"), (outdated.State, outdated.Summary));
        Assert.Contains("winmm.dll: another mkwmp version", outdated.Lines);

        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "tampered");
        Assert.Equal(IntegrationState.Damaged, State(d, want));
        Assert.False(Probe.Check(d, want, V, false).Ok);
    }

    [Fact]
    public void Dev_probe_keeps_a_local_build()
    {
        var d = Dir("dev");
        File.WriteAllText(Path.Combine(d, "mkw.exe"), "");
        var want = new Expected("1.0.0", Hash("p1"), null);
        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "MZ...mkwmp probe 1.0.0-dev...");
        File.WriteAllBytes(Path.Combine(d, Probe.Host), Rig.FakeWinmm());
        Assert.Equal(IntegrationState.Outdated, State(d, want)); // not this release's probe
        var r = Probe.Check(d, want, V, true);
        Assert.Equal((IntegrationState.Dev, true, "Dev probe kept (MKWMP_DEV_PROBE=1)"), (r.State, r.Ok, r.Summary));
        // Still only a probe, and still the real winmm.dll behind it.
        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "tampered");
        Assert.Equal(IntegrationState.Damaged, State(d, want, dev: true));
        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "MZ...mkwmp probe 1.0.0-dev...");
        File.Delete(Path.Combine(d, Probe.Host));
        Assert.False(Probe.Check(d, want, V, true).Ok); // no real winmm.dll behind it
    }

    [Fact]
    public void Pe_exports_of_a_real_winmm()
    {
        var b = Rig.FakeWinmm();
        Assert.Equal(["timeBeginPeriod", "timeEndPeriod"], Probe.PeExports(b));
        Assert.True(Probe.LooksLikeWinmm(b));
        Assert.False(Probe.LooksLikeWinmm("MZ timeBeginPeriod timeEndPeriod"u8)); // names in the bytes are not exports
        Assert.Null(Probe.PeExports(b.AsSpan(0, 0x100)));
        var system = Path.Combine(Environment.SystemDirectory, "winmm.dll");
        if (File.Exists(system))
        {
            Assert.True(Probe.LooksLikeWinmm(File.ReadAllBytes(system)));
        }
    }

    [Fact]
    public void The_session_file_carries_everything_the_probe_reads()
    {
        var session = new SessionFile("mkwlb.example:8905", "Ana", "127.0.0.1:47820", "abc", 8907, "mkw1.x.y", 2, "-----BEGIN CERTIFICATE-----\n");
        Assert.Equal(
            "# Generated by the mkwmp launcher. Do not edit; edit client.toml.\nserver = \"mkwlb.example:8905\"\nname = \"Ana\"\nlauncher = \"127.0.0.1:47820\"\n"
            + "launcher_token = \"abc\"\nvoice_port = 8907\nticket = \"mkw1.x.y\"\nslot = 2\n",
            Probe.SessionText(session));
        var plain = Probe.SessionText(session with { Ticket = null, Slot = 1 });
        Assert.DoesNotContain("ticket", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("slot", plain, StringComparison.Ordinal);

        var d = Dir("session");
        File.WriteAllBytes(Path.Combine(d, "mkw.exe"), Rig.TestExe);
        var target = new MkwRecompTarget(V);
        var install = target.Detect(d)!;
        target.WriteSession(install, session);
        Assert.True(File.Exists(Path.Combine(d, Probe.LobbyCa)));
        Assert.False(File.Exists(Path.Combine(d, "mkwmp.toml.tmp")));
        // The lobby CA follows the server: one that has none removes the file.
        target.WriteSession(install, session with { LobbyCaPem = null });
        Assert.False(File.Exists(Path.Combine(d, Probe.LobbyCa)));
        target.ForgetTicket(install);
        Assert.Equal(Probe.SessionText(session with { Ticket = null }), File.ReadAllText(Path.Combine(d, "mkwmp.toml")));
        target.ForgetTicket(install);
        File.Delete(Path.Combine(d, "mkwmp.toml"));
        target.ForgetTicket(install); // nothing there: nothing to do
    }

    [Fact]
    public async Task The_target_installs_from_a_package_and_uninstalls()
    {
        var d = Dir("target");
        File.WriteAllBytes(Path.Combine(d, "mkw.exe"), Rig.TestExe);
        var files = Dir("package");
        File.WriteAllText(Path.Combine(files, "winmm.dll"), "MZ mkwmp probe 1.4.0");
        File.WriteAllText(Path.Combine(files, "mkwmp-voice.dll"), "voice");
        var package = new IntegrationPackage(
            V,
            new Dictionary<string, string> { ["winmm.dll"] = Path.Combine(files, "winmm.dll"), ["mkwmp-voice.dll"] = Path.Combine(files, "mkwmp-voice.dll") },
            new Dictionary<string, string> { ["winmm.dll"] = Hash("MZ mkwmp probe 1.4.0"), ["mkwmp-voice.dll"] = Hash("voice") });
        var notes = new List<string>();
        var running = false;
        var target = new MkwRecompTarget(V, notes.Add) { RunningOverride = () => running };
        var install = target.Detect(d)!;
        Assert.True(target.NeedsPackage(install));
        Assert.Equal(IntegrationState.Missing, target.Inspect(install, package).State);
        running = true;
        Assert.Equal("close the game first: a game patch update is ready", (await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(install, package, default))).Message);
        running = false;
        var report = await target.InstallAsync(install, package, default);
        // winmm_host.dll is the system's own winmm.dll, copied.
        Assert.Equal((IntegrationState.Installed, "Patches installed (v1.4.0)"), (report.State, report.Summary));
        Assert.Contains("installed the game patch v1.4.0", notes);
        Assert.Contains("installed voice chat v1.4.0", notes);
        // A file changed since it was verified is not installed.
        File.WriteAllText(Path.Combine(files, "winmm.dll"), "swapped");
        File.Delete(Path.Combine(d, "winmm.dll"));
        Assert.Equal("winmm.dll: checksum mismatch", (await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(install, package, default))).Message);
        Assert.Equal("the release lists no game patch", (await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(install, IntegrationPackage.Empty(V), default))).Message);
        await target.UninstallAsync(install, default);
        Assert.Equal(["mkw.exe"], Directory.GetFiles(d).Select(Path.GetFileName));
    }
}

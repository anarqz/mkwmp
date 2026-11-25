// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>The play checklist: each row from the state it reads, the PLAY gate, and its agreement with <see cref="Launcher.PlayProblem"/>.</summary>
public class PlayChecklistTests
{
    private const string Folder = @"C:\Games\mkw-recomp";
    private static readonly TargetInfo Recomp = new("mkw-recomp", "mkw-recomp", "the one with mkw.exe", TargetCapabilities.Launch) { GameVersions = ["1.8.0"] };
    private static readonly GameInstall Install = new("mkw-recomp", Folder, Folder + @"\mkw.exe", "0.1.18", "1.8.0", null);
    private static readonly IntegrationReport ModIn = new(IntegrationState.Installed, Strings.PatchState("mod_installed", "1.4.1"), []) { AsMod = true };
    private static readonly RuntimeView Pinned = new(RuntimeState.Installed, "0.1.18", "0.1.18", Folder, false, 0, 0, 0, 0, null, 1, 1, null, null);
    private static readonly GameSetupView SetUp = GameSetupView.None with { Available = true, DumpPath = @"D:\MKW.nup", GameVersion = "1.8.0" };

    /// <summary>Everything in place, offline, the launcher up to date.</summary>
    private static ChecklistFacts Ready() => new(
        new LauncherSettings { Game = Folder },
        new ServerView(ServerState.NotAsked, null, 0, 0, 0, null),
        new GameView([Recomp], Install, new TargetCheck(TargetStatus.Ready, null, null, null, null), ModIn, false, false, null, false),
        Pinned,
        SetUp,
        new UpdateView(UpdateState.UpToDate, null, null, 0, null, ""),
        "1.4.1",
        null);

    private static CheckItem Row(ChecklistFacts facts, CheckId id) => PlayChecklist.Build(facts).Find(id)!;

    [Fact]
    public void Everything_in_place_is_ready_with_every_row_ok_or_not_for_now()
    {
        var list = PlayChecklist.Build(Ready());
        Assert.True(list.Ready);
        Assert.Equal(0, list.NeededCount);
        Assert.Equal([CheckId.Runtime, CheckId.GameCopy, CheckId.GameVersion, CheckId.GameSetup, CheckId.MkwmpFiles, CheckId.LauncherUpdate, CheckId.Online], list.Items.Select(i => i.Id));
        Assert.All(list.Items.Take(6), i => Assert.Equal(CheckState.Ok, i.State));
        Assert.Equal(CheckState.NotApplicable, list.Find(CheckId.Online)!.State); // offline
        Assert.Equal("MKW 1.8.0", list.Find(CheckId.GameVersion)!.Status);
        // A set-up game can always be set up again: a second button, asked for first.
        Assert.Equal(CheckActionKind.Rebuild, list.Find(CheckId.GameSetup)!.More!.Kind);
    }

    [Fact]
    public void The_wrong_runtime_is_needed_and_offers_mkwmps_build_beside_the_players_own()
    {
        var facts = Ready() with
        {
            Game = Ready().Game with
            {
                Install = Install with { Version = "0.1.13" },
                Check = new TargetCheck(TargetStatus.Unsupported, "mkw-recomp 0.1.13: not supported", null, null, null) { Gate = TargetGate.Runtime },
            },
            Runtime = Pinned with { InstalledVersion = "0.1.13", Foreign = true },
        };
        var row = Row(facts, CheckId.Runtime);
        Assert.Equal((CheckState.Needed, "mkw-recomp 0.1.13: not supported", CheckActionKind.InstallRuntime, "Install MKWMP's Build"), (row.State, row.Status, row.Action!.Kind, row.Action.Label));
        // What depends on the runtime waits for it.
        Assert.Equal(CheckState.NotApplicable, Row(facts, CheckId.GameSetup).State);
        Assert.Equal(CheckState.NotApplicable, Row(facts, CheckId.MkwmpFiles).State);

        // MKWMP's own build, older: updated in place; the build for another processor: the same, said so.
        var older = Ready() with { Runtime = Pinned with { State = RuntimeState.UpdateAvailable, InstalledVersion = "0.1.16-mkwmp" } };
        row = Row(older, CheckId.Runtime);
        Assert.Equal((CheckState.Needed, Strings.RuntimeUpdateNeeded("mkw-recomp", "0.1.18"), "Update mkw-recomp"), (row.State, row.Status, row.Action!.Label));
        var cpu = Ready() with { Runtime = Pinned with { State = RuntimeState.UpdateAvailable, PinnedVariant = "no AVX2" } };
        Assert.Contains("does not run on this processor", Row(cpu, CheckId.Runtime).Status, StringComparison.Ordinal);

        // Nothing installed: the pinned download; under way: working, with its progress, and PLAY waits.
        var none = Ready() with { Game = Ready().Game with { Install = null, Check = null, Integration = null }, Runtime = Pinned with { State = RuntimeState.NotInstalled, InstalledVersion = null } };
        Assert.Equal((CheckState.Needed, "Install mkw-recomp"), (Row(none, CheckId.Runtime).State, Row(none, CheckId.Runtime).Action!.Label));
        var busy = none with { Runtime = Pinned with { State = RuntimeState.Downloading, Progress = 0.4 } };
        row = Row(busy, CheckId.Runtime);
        Assert.Equal((CheckState.Working, 0.4, true), (row.State, row.Progress, row.Blocks));
    }

    [Fact]
    public void A_copy_that_is_not_1_8_0_is_needed_with_the_version_it_is_and_never_a_download()
    {
        var problem = Strings.RecompGameProblem("1.0.0", ["1.8.0"]);
        Assert.Equal("MKWMP needs MKW 1.8.0: this copy of the game is 1.0.0", problem);
        var facts = Ready() with
        {
            Game = Ready().Game with
            {
                Install = Install with { GameVersion = "1.0.0" },
                Check = new TargetCheck(TargetStatus.Unsupported, problem, null, null, null) { Gate = TargetGate.GameVersion },
            },
        };
        var row = Row(facts, CheckId.GameVersion);
        Assert.Equal((CheckState.Needed, problem, CheckActionKind.ChangeCopy), (row.State, row.Status, row.Action!.Kind));
        Assert.Equal(CheckState.Ok, Row(facts, CheckId.Runtime).State); // the runtime is fine: only the copy is wrong
        Assert.DoesNotContain(PlayChecklist.Build(facts).Items, i => i.Action?.Kind == CheckActionKind.InstallRuntime);

        // A setup that read another version is said at once, before any game is built from it.
        var reading = Ready() with { Setup = SetUp with { State = GameSetupState.Running, GameVersion = "1.9.0" } };
        Assert.Equal((CheckState.Needed, Strings.RecompGameProblem("1.9.0", ["1.8.0"])), (Row(reading, CheckId.GameVersion).State, Row(reading, CheckId.GameVersion).Status));
        // Not known before the setup: nothing to say yet.
        var fresh = Ready() with
        {
            Game = Ready().Game with { Install = Install with { GameVersion = null } },
            Setup = SetUp with { State = GameSetupState.NeedsSetup, Reason = GameSetupReason.NotSetUp, NeedsDump = true, DumpPath = null, GameVersion = null, Message = Strings.SetupNotSetUp },
        };
        Assert.Equal(CheckState.NotApplicable, Row(fresh, CheckId.GameVersion).State);
        Assert.Equal((CheckState.Needed, CheckActionKind.ChooseCopy), (Row(fresh, CheckId.GameCopy).State, Row(fresh, CheckId.GameCopy).Action!.Kind));
    }

    [Theory]
    [InlineData(IntegrationState.Damaged, "damaged", "Repair")]
    [InlineData(IntegrationState.Outdated, "outdated", "Repair")]
    [InlineData(IntegrationState.Missing, "missing", "Install")]
    [InlineData(IntegrationState.Disabled, "mod_disabled", "Repair")]
    public void Mod_files_that_do_not_match_or_a_mod_not_in_mkw_ini_are_needed_with_repair(IntegrationState state, string summary, string label)
    {
        var facts = Ready() with { Game = Ready().Game with { Integration = new IntegrationReport(state, Strings.PatchState(summary), []) { AsMod = true } } };
        var row = Row(facts, CheckId.MkwmpFiles);
        Assert.Equal((CheckState.Needed, Strings.PatchState(summary), CheckActionKind.RepairFiles, label), (row.State, row.Status, row.Action!.Kind, row.Action.Label));
    }

    [Fact]
    public void A_leftover_winmm_beside_the_mod_and_a_missing_wine_override_are_needed()
    {
        var leftover = Ready() with { Game = Ready().Game with { Integration = ModIn with { LeftoverPatch = true } } };
        Assert.Equal((CheckState.Needed, Strings.PatchState("leftover")), (Row(leftover, CheckId.MkwmpFiles).State, Row(leftover, CheckId.MkwmpFiles).Status));
        var wine = Ready() with { Game = Ready().Game with { Integration = new IntegrationReport(IntegrationState.Installed, "Patches installed", []) { OverrideMissing = true } } };
        Assert.Equal((CheckState.Needed, Strings.PatchState("no_override")), (Row(wine, CheckId.MkwmpFiles).State, Row(wine, CheckId.MkwmpFiles).Status));
        // The switch off in the settings: INSTALL turns it on.
        var off = Ready() with { Settings = Ready().Settings with { Patch = false } };
        Assert.Equal((Strings.InstallPatchFirst, "Install"), (Row(off, CheckId.MkwmpFiles).Status, Row(off, CheckId.MkwmpFiles).Action!.Label));
    }

    [Theory]
    [InlineData(GameSetupReason.RuntimeChanged, false, CheckActionKind.RunSetup, "Set Up Again")]
    [InlineData(GameSetupReason.GameMismatch, false, CheckActionKind.RunSetup, "Set Up Again")]
    [InlineData(GameSetupReason.Unfinished, false, CheckActionKind.RunSetup, "Continue Setup")]
    [InlineData(GameSetupReason.CopyChanged, false, CheckActionKind.RunSetup, "Set Up Again")]
    [InlineData(GameSetupReason.DumpMissing, true, CheckActionKind.ChooseCopy, "Choose Your Game")]
    public void A_game_set_up_for_another_runtime_or_from_a_changed_copy_is_needed(GameSetupReason reason, bool dump, CheckActionKind kind, string label)
    {
        var facts = Ready() with { Setup = SetUp with { State = GameSetupState.NeedsSetup, Reason = reason, NeedsDump = dump, Message = "why" } };
        var row = Row(facts, CheckId.GameSetup);
        Assert.Equal((CheckState.Needed, "why", kind, label), (row.State, row.Status, row.Action!.Kind, row.Action.Label));
        // MKWMP's files wait for the setup (it writes mkw.ini) instead of being offered before it.
        var files = Row(facts with { Game = facts.Game with { Integration = ModIn with { State = IntegrationState.Missing } } }, CheckId.MkwmpFiles);
        Assert.Equal(CheckState.NotApplicable, files.State);
    }

    [Fact]
    public void A_running_setup_works_with_its_step_and_progress_and_blocks_play()
    {
        var facts = Ready() with { Setup = SetUp with { State = GameSetupState.Running, StepLabel = "Building the game", StepIndex = 4, StepCount = 8, Progress = 0.42 } };
        var row = Row(facts, CheckId.GameSetup);
        Assert.Equal((CheckState.Working, "Building the game · step 5 of 8", 0.42, true, CheckActionKind.ShowProgress), (row.State, row.Status, row.Progress, row.Blocks, row.Action!.Kind));
        Assert.False(PlayChecklist.Build(facts).Ready);
    }

    [Fact]
    public void The_update_is_a_warning_and_online_rows_follow_the_flow()
    {
        var update = Ready() with { Update = new UpdateView(UpdateState.Available, "1.4.2", null, 0, null, "") };
        var row = Row(update, CheckId.LauncherUpdate);
        Assert.Equal((CheckState.Warning, CheckActionKind.InstallUpdate), (row.State, row.Action!.Kind));
        Assert.True(PlayChecklist.Build(update).Ready); // a warning never blocks

        var online = Ready() with { Settings = Ready().Settings with { Server = "http://seat.test", Name = "Ana", Pin = "", AdultConsent = false } };
        Assert.Equal((Strings.NeedPin, CheckActionKind.SignIn), (Row(online, CheckId.Online).Status, Row(online, CheckId.Online).Action!.Kind));
        online = online with { Settings = online.Settings with { Pin = "123456" } };
        Assert.Equal((Strings.NoConsent, CheckActionKind.ReviewPrivacy), (Row(online, CheckId.Online).Status, Row(online, CheckId.Online).Action!.Kind));
        online = online with { Settings = online.Settings with { AdultConsent = true }, Server = new ServerView(ServerState.Offline, null, 0, 0, 0, null) };
        Assert.Equal((CheckState.Needed, CheckActionKind.ChooseServer), (Row(online, CheckId.Online).State, Row(online, CheckId.Online).Action!.Kind));
        online = online with { Server = new ServerView(ServerState.Online, "MKWMP", 32, 1, 0, null) };
        Assert.Equal((CheckState.Ok, "MKWMP · signed in as Ana"), (Row(online, CheckId.Online).State, Row(online, CheckId.Online).Status));
    }

    [Fact]
    public void A_problem_no_row_is_about_gets_a_row_of_its_own()
    {
        var list = PlayChecklist.Build(Ready() with { PlayProblem = Strings.AnotherLauncher(47820) });
        Assert.Equal((CheckId.Other, CheckState.Needed, Strings.AnotherLauncher(47820)), (list.Items[0].Id, list.Items[0].State, list.Items[0].Status));
        Assert.False(list.Ready);
        // A problem a row already blocks for: no second row.
        var setup = Ready() with { Setup = SetUp with { State = GameSetupState.NeedsSetup, Reason = GameSetupReason.Unfinished, Message = Strings.SetupUnfinished }, PlayProblem = Strings.SetupUnfinished };
        Assert.DoesNotContain(PlayChecklist.Build(setup).Items, i => i.Id == CheckId.Other);
    }

    // ---- the launcher: its checklist and PLAY never disagree --------------------------------

    private static void Agrees(Launcher l)
    {
        Assert.Equal(l.PlayProblem is null, l.Checklist.Ready);
        if (l.PlayProblem is not null)
        {
            Assert.NotEmpty(l.Checklist.Blocking);
        }
    }

    [Fact]
    public async Task The_checklist_agrees_with_play_problem_in_the_order_the_player_fixes_things()
    {
        await using var rig = new Rig(new FakeSeatServer(), s => s with { AdultConsent = false, Name = "a b", Pin = "", Patch = false }, installPatch: false);
        var l = rig.Launcher;
        Agrees(l);
        Assert.Equal((CheckState.Needed, Strings.PickGamertag), (l.Checklist.Find(CheckId.Online)!.State, l.Checklist.Find(CheckId.Online)!.Status));
        await l.SaveSettingsAsync(l.Settings with { Name = "Ana" });
        await l.UsePinAsync("Ana", "123456");
        await l.SetConsentAsync(true);
        Agrees(l);
        Assert.Equal(Strings.InstallPatchFirst, l.PlayProblem);
        Assert.Equal((CheckState.Needed, Strings.InstallPatchFirst), (l.Checklist.Find(CheckId.MkwmpFiles)!.State, l.Checklist.Find(CheckId.MkwmpFiles)!.Status));
        await l.SaveSettingsAsync(l.Settings with { Patch = true });
        await l.FindGameAsync();
        Agrees(l);
        Assert.Equal(CheckState.Needed, l.Checklist.Find(CheckId.MkwmpFiles)!.State);
        // An unsupported runtime: its row, and the files wait for it.
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), "some other build"u8);
        await l.SaveSettingsAsync(l.Settings with { Slot = 2 });
        await l.FindGameAsync();
        Agrees(l);
        Assert.Equal((CheckState.Needed, l.PlayProblem), (l.Checklist.Find(CheckId.Runtime)!.State, l.Checklist.Find(CheckId.Runtime)!.Status));
        Assert.Equal(CheckState.NotApplicable, l.Checklist.Find(CheckId.MkwmpFiles)!.State);
    }

    [Fact]
    public async Task A_mod_turned_off_in_mkw_ini_blocks_play_and_its_row_says_repair()
    {
        if (Environment.GetEnvironmentVariable("MKW_MODS") is not null || Probe.DevProbe)
        {
            return;
        }
        await using var rig = new Rig(new FakeSeatServer(), installPatch: false);
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        rig.EnableMod();
        File.WriteAllText(Path.Combine(rig.Game, "mkw.ini"), "[launcher]\nmods = \n"); // the mod's folder is there, mkw.ini does not load it
        await rig.Launcher.FindGameAsync();
        var row = rig.Launcher.Checklist.Find(CheckId.MkwmpFiles)!;
        Assert.Equal((IntegrationState.Disabled, CheckState.Needed, "Repair"), (rig.Launcher.Game.Integration!.State, row.State, row.Action!.Label));
        Assert.Equal(row.Status, rig.Launcher.PlayProblem);
        Agrees(rig.Launcher);
    }

    [Fact]
    public async Task The_launchers_old_patch_beside_the_mod_blocks_play_until_repaired()
    {
        if (Environment.GetEnvironmentVariable("MKW_MODS") is not null || Probe.DevProbe)
        {
            return;
        }
        await using var rig = new Rig(new FakeSeatServer()); // the game patch is installed
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        rig.EnableMod();
        await rig.Launcher.FindGameAsync();
        Assert.True(rig.Launcher.Game.Integration!.LeftoverPatch);
        Assert.Equal(Strings.PatchState("leftover"), rig.Launcher.PlayProblem);
        Assert.Equal(CheckState.Needed, rig.Launcher.Checklist.Find(CheckId.MkwmpFiles)!.State);
        // REPAIR (what PLAY does too) takes the old patch out: nothing blocks.
        await rig.Launcher.SyncIntegrationAsync(force: false);
        Assert.False(File.Exists(Path.Combine(rig.Game, Probe.ProbeDll)));
        Assert.Null(rig.Launcher.PlayProblem);
        Assert.True(rig.Launcher.Checklist.Ready);
    }

    [Fact]
    public void Under_wine_a_game_patch_without_its_override_says_so()
    {
        using var files = new SetupRig();
        var target = new MkwRecompTarget("1.4.0") { OverrideOverride = () => false };
        File.WriteAllBytes(Path.Combine(files.Game, "mkw.exe"), Rig.TestExe);
        Probe.InstallProbe(files.Game, "probe"u8, false, out _);
        Probe.InstallFile(files.Game, Probe.Voice, "voice"u8, false);
        File.WriteAllBytes(Path.Combine(files.Game, Probe.Host), Rig.FakeWinmm());
        var install = target.Detect(files.Game)!;
        var report = target.Inspect(install, null);
        Assert.True(report is { Ok: true, OverrideMissing: true });
        Assert.Equal(Strings.PatchState("no_override"), report.Problem);
        target.OverrideOverride = () => true;
        Assert.Null(target.Inspect(install, null).Problem);
        target.OverrideOverride = () => null; // Windows: nothing to say
        Assert.Null(target.Inspect(install, null).Problem);
    }

    [Fact]
    public async Task Play_is_refused_while_a_row_blocks_it()
    {
        // A game set up from 1.0.0: the version row, and PLAY says the same and starts nothing.
        using var files = new SetupRig();
        await using var rig = new Rig(new FakeSeatServer());
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), RuntimeRig.NewExe);
        SetupRig.Wire(rig.Target, files);
        File.WriteAllText(Path.Combine(rig.Game, "setup-state.json"), SetupRig.StateJson(SetupRig.Pinned, files.Dump).Replace("1.8.0", "1.0.0", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(rig.Game, "mkwgame.dll"), "built");
        File.WriteAllText(Path.Combine(rig.Game, "mkwgame.id"), SetupRig.PinnedId + "\n");
        await rig.Launcher.ChooseGameFolderAsync(rig.Game);
        var problem = Strings.RecompGameProblem("1.0.0", ["1.8.0"]);
        Assert.Equal(problem, rig.Launcher.PlayProblem);
        Assert.Equal((CheckState.Needed, problem), (rig.Launcher.Checklist.Find(CheckId.GameVersion)!.State, rig.Launcher.Checklist.Find(CheckId.GameVersion)!.Status));
        Agrees(rig.Launcher);
        await rig.Launcher.PlayAsync();
        var session = await rig.SettleSession();
        Assert.Equal(SessionState.Failed, session.State);
        Assert.StartsWith(problem, session.Status, StringComparison.Ordinal);
        Assert.False(rig.Started);
        Assert.Equal(0, rig.Server.Joins);
    }
}

/// <summary>"Set up the game again" and a copy changed since it was set up (mkw-recomp: setup-state.json, prepare.py's keys).</summary>
public class GameRebuildTests
{
    [Fact]
    public async Task A_rebuild_takes_every_done_key_out_so_prepare_runs_every_step_and_keeps_the_rest()
    {
        using var rig = new SetupRig();
        rig.SetUp();
        File.WriteAllText(Path.Combine(rig.Game, "mkw.ini"), "[launcher]\nmods = mkwmp\n");
        Directory.CreateDirectory(Path.Combine(rig.Game, "profile"));
        File.WriteAllText(Path.Combine(rig.Game, "profile", "save.bin"), "my save");
        var seen = Path.Combine(rig.Scripts, "seen.json");
        rig.Script([$"copy /y \"{Path.Combine(rig.Game, "setup-state.json")}\" \"{seen}\" >nul", "echo step check", "echo version 1.8.0", .. rig.Builds(), "echo done", "exit /b 0"]);

        var result = await rig.Target.RunSetupAsync(rig.Install, new GameSetupRequest(null, GameDataMode.Link, rig.Logs, Rebuild: true), null, default);

        Assert.True(result.Ok, result.Message);
        // What prepare.py found when it started: no step done (so none is skipped), the same source and mode.
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(seen))!;
        Assert.Empty(state["done"]!.AsObject());
        Assert.Equal((rig.Dump, "link", 1, SetupRig.Pinned), (state["source"]!.GetValue<string>(), state["romfs_mode"]!.GetValue<string>(), state["version"]!.GetValue<int>(), state["port"]!.GetValue<string>()));
        Assert.DoesNotContain("--source", rig.Arguments, StringComparison.Ordinal); // the same copy, as the last setup
        // The player's things and their copy stay.
        Assert.Equal("my save", File.ReadAllText(Path.Combine(rig.Game, "profile", "save.bin")));
        Assert.Contains("mods = mkwmp", File.ReadAllText(Path.Combine(rig.Game, "mkw.ini")), StringComparison.Ordinal);
        Assert.True(File.Exists(rig.Dump));
    }

    [Fact]
    public void A_copied_romfs_is_kept_by_a_rebuild_and_a_missing_state_is_left_alone()
    {
        using var rig = new SetupRig();
        rig.SetUp(mode: "copy");
        Assert.True(RecompSetup.ForgetBuild(rig.Game, keepRomfs: true));
        var done = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(rig.Game, "setup-state.json")))!["done"]!.AsObject();
        Assert.Equal(["romfs"], done.Select(p => p.Key));
        File.Delete(Path.Combine(rig.Game, "setup-state.json"));
        Assert.False(RecompSetup.ForgetBuild(rig.Game, keepRomfs: false));
    }

    [Fact]
    public void A_copy_changed_since_its_setup_needs_the_setup_again_and_a_touched_one_does_not()
    {
        using var rig = new SetupRig();
        rig.SetUp();
        Assert.Null(RecompSetup.Status(rig.Install)!.Needed); // set up; what the copy is now is recorded
        Assert.True(File.Exists(Path.Combine(rig.Game, RecompSetup.CopyRecordFile)));
        Assert.False(RecompSetup.CopyChanged(rig.Game, rig.Dump));

        // Only touched (copied back, a backup tool): the same bytes, so the same game.
        File.SetLastWriteTimeUtc(rig.Dump, DateTime.UtcNow.AddMinutes(5));
        Assert.False(RecompSetup.CopyChanged(rig.Game, rig.Dump));
        Assert.Null(RecompSetup.Status(rig.Install)!.Needed);

        // Updated somewhere else (another size, other bytes): set up again, from the same copy, no file asked for.
        File.WriteAllBytes(rig.Dump, [.. "NUP\u0001"u8, 9, 9, 9, 9, 9, 9, 9, 9, 9]);
        var plan = RecompSetup.Status(rig.Install)!.Needed!;
        Assert.Equal((GameSetupReason.CopyChanged, Strings.SetupCopyChanged, false), (plan.Reason, plan.Message, plan.NeedsDump));
        // The same size, other bytes: the sample hash tells.
        File.WriteAllBytes(rig.Dump, [.. "NUP\u0001"u8, 1, 2, 3, 4, 5, 6, 7, 8]);
        RecompSetup.RecordCopy(rig.Game, rig.Dump);
        File.WriteAllBytes(rig.Dump, [.. "NUP\u0001"u8, 8, 7, 6, 5, 4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(rig.Dump, DateTime.UtcNow.AddMinutes(9));
        Assert.True(RecompSetup.CopyChanged(rig.Game, rig.Dump));
    }

    [Fact]
    public void A_folder_copy_is_compared_by_its_exefs_main()
    {
        using var rig = new SetupRig();
        var dump = Path.Combine(rig.Root, "dump");
        Directory.CreateDirectory(Path.Combine(dump, "base", "exefs"));
        Directory.CreateDirectory(Path.Combine(dump, "base", "romfs"));
        File.WriteAllText(Path.Combine(dump, "base", "exefs", "main"), "game 1.8.0");
        Assert.Equal(Path.Combine(dump, "base", "exefs", "main"), RecompSetup.PrintFile(dump));
        RecompSetup.RecordCopy(rig.Game, dump);
        Assert.False(RecompSetup.CopyChanged(rig.Game, dump));
        File.WriteAllText(Path.Combine(dump, "base", "exefs", "main"), "game 1.9.0, an update");
        Assert.True(RecompSetup.CopyChanged(rig.Game, dump));
        Assert.Null(RecompSetup.CopyChanged(rig.Game, rig.Dump)); // the record is of another copy: not known
    }

    [Fact]
    public async Task A_copy_updated_from_1_8_0_to_a_newer_game_sets_up_again_and_the_newer_version_is_refused()
    {
        using var files = new SetupRig();
        await using var rig = new Rig(new FakeSeatServer());
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), RuntimeRig.NewExe);
        SetupRig.Wire(rig.Target, files);
        File.WriteAllText(Path.Combine(rig.Game, "setup-state.json"), SetupRig.StateJson(SetupRig.Pinned, files.Dump));
        File.WriteAllText(Path.Combine(rig.Game, "mkwgame.dll"), "built");
        File.WriteAllText(Path.Combine(rig.Game, "mkwgame.id"), SetupRig.PinnedId + "\n");
        await rig.Launcher.ChooseGameFolderAsync(rig.Game);
        Assert.Equal("1.8.0", rig.Launcher.Game.Install!.GameVersion);
        Assert.Equal(CheckState.Ok, rig.Launcher.Checklist.Find(CheckId.GameSetup)!.State);

        // The player updates their .nup elsewhere: the setup row is needed, PLAY says the same.
        File.WriteAllBytes(files.Dump, [.. "NUP\u0001"u8, 1, 9, 0, 0, 0, 0, 0, 0, 0, 0]);
        await rig.Launcher.FindGameAsync();
        var row = rig.Launcher.Checklist.Find(CheckId.GameSetup)!;
        Assert.Equal((CheckState.Needed, Strings.SetupCopyChanged, "Set Up Again"), (row.State, row.Status, row.Action!.Label));
        Assert.Equal(Strings.SetupCopyChanged, rig.Launcher.PlayProblem);

        // "Set up again" redoes every step (prepare.py's keys would not see a same-path update), and the setup reads 1.9.0:
        // stopped there, said, and PLAY stays refused.
        var seen = Path.Combine(files.Scripts, "seen.json");
        files.Script($"copy /y \"{Path.Combine(rig.Game, "setup-state.json")}\" \"{seen}\" >nul", "echo step check", "echo version 1.9.0", files.Hangs, "exit /b 1");
        await rig.Launcher.RunGameSetupAsync(null, default);
        Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(seen))!["done"]!.AsObject());
        var view = rig.Launcher.GameSetup;
        var problem = Strings.RecompGameProblem("1.9.0", ["1.8.0"]);
        Assert.Equal((GameSetupState.Failed, "version", problem), (view.State, view.ErrorCode, view.Message));
        var version = rig.Launcher.Checklist.Find(CheckId.GameVersion)!;
        Assert.Equal((CheckState.Needed, problem), (version.State, version.Status));
        Assert.Equal(CheckActionKind.ChooseCopy, rig.Launcher.Checklist.Find(CheckId.GameSetup)!.Action!.Kind);
        Assert.NotNull(rig.Launcher.PlayProblem);

        // A setup that did build 1.9.0 (outside the launcher): the version gate refuses it with what MKWMP needs.
        File.WriteAllText(Path.Combine(rig.Game, "setup-state.json"), SetupRig.StateJson(SetupRig.Pinned, files.Dump).Replace("1.8.0", "1.9.0", StringComparison.Ordinal));
        var check = rig.Target.Check(rig.Target.Detect(rig.Game)!);
        Assert.Equal((TargetStatus.Unsupported, TargetGate.GameVersion, problem), (check.Status, check.Gate, check.Problem));
    }

    [Fact]
    public async Task Set_up_again_is_refused_while_the_game_runs_and_before_a_first_setup()
    {
        await using var rig = new Rig(new FakeSeatServer());
        using var files = new SetupRig();
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), RuntimeRig.NewExe);
        SetupRig.Wire(rig.Target, files);
        await rig.Launcher.ChooseGameFolderAsync(rig.Game);
        Assert.Equal(Strings.SetupNotYet, await rig.Launcher.RebuildGameAsync(default)); // never set up: the first setup asks for a copy

        File.WriteAllText(Path.Combine(rig.Game, "setup-state.json"), SetupRig.StateJson(SetupRig.Pinned, files.Dump));
        File.WriteAllText(Path.Combine(rig.Game, "mkwgame.dll"), "built");
        File.WriteAllText(Path.Combine(rig.Game, "mkwgame.id"), SetupRig.PinnedId + "\n");
        await rig.Launcher.FindGameAsync();
        files.Running = true;
        Assert.Equal(Strings.SetupCloseGame, await rig.Launcher.RebuildGameAsync(default));
        Assert.Null(files.Arguments); // nothing started
        files.Running = false;
        files.Script("echo step check", "echo version 1.8.0", "echo done", "exit /b 0");
        Assert.Null(await rig.Launcher.RebuildGameAsync(default));
        Assert.NotNull(files.Arguments);
    }
}

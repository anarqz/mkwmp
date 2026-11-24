// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>
/// A game folder with the pinned mkw-recomp (a stand-in mkw.exe) and a stand-in for its setup
/// program: a .cmd that prints prepare.py's event lines and ends with its exit codes. No test
/// runs the real setup, and none has a real game.
/// </summary>
internal sealed class SetupRig : IDisposable
{
    public const string Pinned = "0.1.18";
    public const string PinnedId = "c9bbcb75a9ac9a49";

    /// <summary>The game id of the previous pin, 0.1.15-mkwmp (0.1.13's).</summary>
    public const string PreviousId = "c0dd6cf12c07c23a";

    private bool _running;

    public SetupRig()
    {
        Root = Rig.NewFolder("setup");
        Game = Path.Combine(Root, "game");
        Logs = Path.Combine(Root, "launcher", "logs");
        Scripts = Path.Combine(Root, "scripts");
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Scripts);
        File.WriteAllBytes(Path.Combine(Game, "mkw.exe"), RuntimeRig.NewExe);
        Dump = Path.Combine(Root, "my game.nup");
        File.WriteAllBytes(Dump, [.. "NUP\u0001"u8, 1, 2, 3, 4, 5, 6, 7, 8]);
        Target = NewTarget(this);
    }

    public string Root { get; }
    public string Game { get; }
    public string Logs { get; }
    public string Scripts { get; }

    /// <summary>The player's copy of the game: a file that starts as a .nup does.</summary>
    public string Dump { get; }

    public MkwRecompTarget Target { get; }

    public bool Running { get => _running; set => _running = value; }

    public long? Free { get; set; }

    public GameInstall Install => Target.Detect(Game)!;

    public string ScriptPath => Path.Combine(Scripts, "prepare.cmd");

    /// <summary>What the stand-in was started with (one line), or null when it never ran.</summary>
    public string? Arguments => File.Exists(Path.Combine(Scripts, "args.txt")) ? File.ReadAllText(Path.Combine(Scripts, "args.txt")).Trim() : null;

    public string ChildPid => Path.Combine(Scripts, "child.pid");

    public static MkwRecompTarget NewTarget(SetupRig rig) => Wire(new MkwRecompTarget("1.4.0"), rig);

    /// <summary>Makes a target run this rig's stand-in instead of the install's Python.</summary>
    public static MkwRecompTarget Wire(MkwRecompTarget target, SetupRig rig)
    {
        target.RunningOverride = () => rig.Running;
        target.SetupFreeSpace = _ => rig.Free;
        target.SetupCommandOverride = args => new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } cmd ? cmd : "cmd.exe",
            $"/d /s /c \"\"{rig.ScriptPath}\" {string.Join(' ', args.Select(a => $"\"{a}\""))}\"");
        return target;
    }

    /// <summary>The state a finished setup leaves, as prepare.py writes it.</summary>
    public static string StateJson(string? port, string? source, string mode = "link", string id = PinnedId) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["done"] = new Dictionary<string, string> { ["exefs"] = source ?? "", ["romfs"] = $"{mode} {source}", ["generate"] = $"{id} 1.8.0 0123456789abcdef" },
            ["source"] = source,
            ["romfs_mode"] = mode,
            ["version"] = 1,
            ["port"] = port,
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));

    /// <summary>The folder as a finished setup leaves it.</summary>
    public void SetUp(string? port = Pinned, string? source = null, string mode = "link", string id = PinnedId, bool built = true)
    {
        File.WriteAllText(Path.Combine(Game, "setup-state.json"), StateJson(port, source ?? Dump, mode, id));
        if (built)
        {
            File.WriteAllText(Path.Combine(Game, "mkwgame.dll"), "built from the player's game");
            File.WriteAllText(Path.Combine(Game, "mkwgame.id"), id + "\n");
        }
    }

    /// <summary>Lines for the stand-in that leave the folder set up (what the real setup does on the way to "done").</summary>
    public string[] Builds(string? port = Pinned)
    {
        File.WriteAllText(Path.Combine(Scripts, "state.json"), StateJson(port, Dump));
        return
        [
            $"copy /y \"{Path.Combine(Scripts, "state.json")}\" \"{Path.Combine(Game, "setup-state.json")}\" >nul",
            $">\"{Path.Combine(Game, "mkwgame.dll")}\" echo built",
            $">\"{Path.Combine(Game, "mkwgame.id")}\" echo {PinnedId}",
        ];
    }

    /// <summary>A child process of the stand-in that leaves its process id behind and then waits: what a compiler is to prepare.py.</summary>
    public string Hangs =>
        $"powershell -NoProfile -Command \"Set-Content -LiteralPath '{ChildPid}' -Value $PID; Start-Sleep -Seconds 300\"";

    /// <summary>Writes the stand-in: it records its arguments, then runs these lines.</summary>
    public void Script(params string[] lines) =>
        File.WriteAllLines(ScriptPath, ["@echo off", $">\"{Path.Combine(Scripts, "args.txt")}\" echo %*", .. lines]);

    /// <summary>A first setup from start to end, with the events of the real one.</summary>
    public void ScriptFirstSetup() => Script(
    [
        "echo step check", "echo version 1.8.0",
        "echo step exefs", "echo progress 3 6", "echo progress 6 6",
        "echo step romfs",
        "echo step generate",
        "echo step compile", "echo progress 100 397", "echo progress 397 397",
        "echo step shaders", "echo progress 11600 11604",
        .. Builds(),
        "echo step warm", "echo progress 0 1", "echo progress 1 1",
        "echo step cleanup",
        "echo done",
        "exit /b 0",
    ]);

    public Task<GameSetupResult> Run(string? dump, Collect? progress = null, GameDataMode mode = GameDataMode.Link, CancellationToken cancel = default) =>
        Target.RunSetupAsync(Install, new GameSetupRequest(dump, mode, Logs), progress, cancel);

    public async Task<int> WaitForChild()
    {
        await Rig.Until("the stand-in's child process", () => File.Exists(ChildPid) && File.ReadAllText(ChildPid).Trim().Length > 0, seconds: 30);
        return int.Parse(File.ReadAllText(ChildPid).Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal sealed class Collect : IProgress<GameSetupProgress>
    {
        private readonly List<GameSetupProgress> _seen = [];

        public IReadOnlyList<GameSetupProgress> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen];
                }
            }
        }

        public void Report(GameSetupProgress value)
        {
            lock (_seen)
            {
                _seen.Add(value);
            }
        }
    }
}

/// <summary>prepare.py's event lines into a progress: the grammar and the arithmetic, with no process and no clock.</summary>
public class SetupProgressTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static RecompSetupProgress Feed(RecompSetupProgress p, params (double At, string Line)[] lines)
    {
        foreach (var (at, line) in lines)
        {
            p.Line(line, S(at));
        }
        return p;
    }

    [Fact]
    public void Every_event_of_the_setup_is_read_and_anything_else_is_ignored()
    {
        var p = new RecompSetupProgress(copy: false);
        Assert.Equal("", p.Step);
        Assert.True(p.Line("step check", S(0)));
        Assert.Equal("check", p.Step);
        Assert.True(p.Line("version 1.8.0", S(1)));
        Assert.Equal("1.8.0", p.GameVersion);
        Assert.True(p.Line("skip exefs", S(1)));
        Assert.True(p.Line("step compile\r", S(2)));
        Assert.True(p.Line("progress 10 397", S(3)));
        Assert.Equal(10.0 / 397, p.At(S(3)).StepFraction, 6);
        // Not events: the script may say more, and a tool's line may slip through.
        Assert.False(p.Line("", S(3)));
        Assert.False(p.Line("progress", S(3)));
        Assert.False(p.Line("progress ten of them", S(3)));
        Assert.False(p.Line("progress -1 5", S(3)));
        Assert.False(p.Line("step", S(3)));
        Assert.False(p.Line("Traceback (most recent call last):", S(3)));
        Assert.False(p.Line("done and dusted", S(3)));
        Assert.Equal(10.0 / 397, p.At(S(3)).StepFraction, 6);
        Assert.False(p.Finished);
        Assert.Null(p.Error);

        // The detail is the rest of the line, spaces included.
        Assert.True(p.Line(@"error space 25.0 GB, 3.1 GB free in C:\Games\Mario Kart", S(4)));
        Assert.Equal(("space", @"25.0 GB, 3.1 GB free in C:\Games\Mario Kart"), p.Error);
        Assert.True(p.Line("error cancelled", S(4)));
        Assert.Equal(("cancelled", ""), p.Error);

        Assert.True(p.Line("done", S(5)));
        Assert.True(p.Finished);
        var end = p.At(S(5));
        Assert.Equal((1.0, 1.0, TimeSpan.Zero), (end.Fraction, end.StepFraction, end.Remaining));
        Assert.Equal(["check", "compile"], p.Ran);
        Assert.Contains("exefs", p.Skipped);
    }

    [Fact]
    public void The_steps_have_names_a_player_can_read()
    {
        var labels = RecompSetupProgress.Steps.Select(s => Strings.SetupStep(s, copy: false)).ToList();
        Assert.Equal(
            ["Reading your game", "Copying the game's program", "Finding the game's data", "Translating the game", "Building the game", "Preparing shaders", "Warming up the graphics", "Finishing up"],
            labels);
        Assert.Equal("Copying the game's data", Strings.SetupStep("romfs", copy: true));
        var p = Feed(new RecompSetupProgress(false), (0, "step compile"));
        Assert.Equal(("compile", "Building the game", 4, 8), (p.At(S(1)).Step, p.At(S(1)).Label, p.At(S(1)).StepIndex, p.At(S(1)).StepCount));
    }

    [Fact]
    public void The_whole_is_weighed_by_the_measured_time_of_each_step()
    {
        // The reference machine itself: check 4 s, exefs 6, romfs (linked) 1, generate 160, compile 186, shaders 80 after
        // compile, warm 76, cleanup 24: 537 s.
        var p = Feed(new RecompSetupProgress(false), (0, "step check"), (4, "step exefs"), (10, "step romfs"), (11, "step generate"));
        var at = p.At(S(91));
        // generate says nothing: its bar is the clock against the 160 s it usually takes.
        Assert.True(at.Indeterminate);
        Assert.Equal(0.5, at.StepFraction, 3);
        Assert.Equal(91.0 / 537, at.Fraction, 3);
        Assert.Equal(446, at.Remaining!.Value.TotalSeconds, 0);

        Feed(p, (171, "step compile"), (264, "progress 198 397"));
        at = p.At(S(264));
        Assert.False(at.Indeterminate);
        Assert.Equal(198.0 / 397, at.StepFraction, 6);
        Assert.Equal((171 + 186 * 198.0 / 397) / 537, at.Fraction, 3);
        Assert.InRange(at.Remaining!.Value.TotalSeconds, 268, 278); // 537 - 264, at this machine's own speed (about 1)

        Feed(p, (357, "step shaders"), (437, "step warm"), (513, "step cleanup"));
        Assert.Equal(513.0 / 537, p.At(S(513)).Fraction, 3);
        Assert.Equal(1, p.Speed(S(513)), 2);
    }

    [Fact]
    public void A_slower_machine_gets_a_longer_estimate_once_it_has_shown_its_speed()
    {
        // Twice as slow as the reference: every step takes double.
        var p = Feed(new RecompSetupProgress(false), (0, "step check"), (8, "step exefs"), (20, "step romfs"), (22, "step generate"), (342, "step compile"));
        Assert.Equal(2, p.Speed(S(342)), 2);
        var at = p.At(S(342));
        Assert.Equal(171.0 / 537, at.Fraction, 3);
        Assert.Equal((537 - 171) * 2, at.Remaining!.Value.TotalSeconds, 0);
        // Before anything long has finished there is nothing to tell the speed from: the reference's.
        var early = Feed(new RecompSetupProgress(false), (0, "step check"), (8, "step exefs"));
        Assert.Equal(1, early.Speed(S(9)));
    }

    [Fact]
    public void Steps_that_need_not_run_are_not_counted_and_the_bar_never_goes_back()
    {
        // After a runtime update that changed nothing the game is built from: check, then only the cleanup.
        var p = Feed(new RecompSetupProgress(false), (0, "step check"), (4, "skip exefs"), (4, "skip romfs"), (4, "skip generate"), (4, "skip compile"),
            (4, "skip shaders"), (4, "skip warm"), (4, "step cleanup"));
        var at = p.At(S(4));
        Assert.Equal(4.0 / 28, at.Fraction, 3);
        Assert.Equal(24, at.Remaining!.Value.TotalSeconds, 0);
        Assert.Equal(["exefs", "romfs", "generate", "compile", "shaders", "warm"], p.Skipped);

        // The shaders step alone (only the translator changed) is the long one: 266 s, not the 80 s it is beside a build.
        var alone = Feed(new RecompSetupProgress(false), (0, "step check"), (4, "skip exefs"), (4, "skip romfs"), (4, "skip generate"));
        var before = alone.At(S(4)).Fraction;
        Feed(alone, (4, "skip compile"), (4, "step shaders"));
        var shaders = alone.At(S(4));
        Assert.Equal(4.0 / (4 + RecompSetupProgress.ShadersAlone + 76 + 24), shaders.Fraction, 3);
        Assert.True(shaders.Fraction >= before);
        var last = 0.0;
        foreach (var second in Enumerable.Range(4, 300))
        {
            var now = alone.At(S(second)).Fraction;
            Assert.True(now >= last);
            last = now;
        }
        // The data copied instead of linked is by far the longest step.
        var copy = Feed(new RecompSetupProgress(copy: true), (0, "step check"), (4, "step exefs"), (10, "step romfs"), (10, "progress 50 100"));
        Assert.Equal("Copying the game's data", copy.At(S(10)).Label);
        Assert.Equal((10 + 450.0) / (537 + 899), copy.At(S(10)).Fraction, 3);
    }

    [Fact]
    public void The_grammar_of_0_1_16_is_the_same_and_its_tone_mapper_is_no_step()
    {
        // prepare.py STEPS at v0.1.16, unchanged from 0.1.15. The tone mapper's programs (renodx.py) are made inside the
        // warm step's turn, before the prewarm, with no event of their own; a failure only writes to stderr.
        Assert.Equal(["check", "exefs", "romfs", "generate", "compile", "shaders", "warm", "cleanup"], RecompSetupProgress.Steps);
        var p = new RecompSetupProgress(false);
        Assert.False(p.Line("tonemap: renodx.py: failed (the HDR tone mapper stays the game's)", S(0)));
        // An update from 0.1.15-mkwmp: the build and the warm-up run again, the shaders are kept. Events come as UTF-8
        // from 0.1.16 on (a version line with a path is read the same).
        Feed(p, (0, "step check"), (4, "version 1.8.0"), (4, "skip exefs"), (4, "skip romfs"), (4, "step generate"), (164, "step compile"),
            (350, "skip shaders"), (350, "step warm"), (350, "progress 0 1"), (426, "progress 1 1"), (426, "step cleanup"), (450, "done"));
        Assert.True(p.Finished);
        Assert.Equal("1.8.0", p.GameVersion);
        Assert.Equal(["check", "generate", "compile", "warm", "cleanup"], p.Ran);
        Assert.Equal(["exefs", "romfs", "shaders"], p.Skipped);
        Assert.Equal("Caminho é válido", RecompSetup.Decode(System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes("Caminho é válido"))));
    }

    [Fact]
    public void The_warm_step_takes_its_count_from_the_runtimes_own_log()
    {
        var p = Feed(new RecompSetupProgress(false), (0, "step warm"), (0, "progress 0 1"));
        Assert.True(p.At(S(10)).Indeterminate); // 0 of 1 is no count
        p.WarmCount(6842, 13684);
        var at = p.At(S(10));
        Assert.Equal((0.5, false), (at.StepFraction, at.Indeterminate));
        p.WarmCount(100, 13684); // an older line read again: never back
        Assert.Equal(0.5, p.At(S(11)).StepFraction);
        Feed(p, (20, "step cleanup"));
        p.WarmCount(13684, 13684); // not the warm step any more
        Assert.True(p.At(S(21)).Indeterminate);

        // The file itself: the newest line of this run counts; an old file does not.
        var data = Rig.NewFolder("warm");
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        File.WriteAllText(Path.Combine(data, "logs", "warm.log"),
            "[host] vk: using gpu 1\n[host] vk_pipeline: pairs prewarm: 1200 of 13684 shader libraries, 2001 ms\n[host] vk_pipeline: pairs prewarm: 2400 of 13684 shader libraries, 4002 ms\n");
        Assert.Equal((2400, 13684), RecompSetup.WarmCount(data, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Null(RecompSetup.WarmCount(data, DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.Null(RecompSetup.WarmCount(Path.Combine(data, "none"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_step_named_again_starts_over_and_an_unknown_step_is_shown_by_its_name()
    {
        // compile found a damaged generated file: the setup generates once more, then compiles.
        var p = Feed(new RecompSetupProgress(false), (0, "step generate"), (100, "step compile"), (110, "progress 50 397"), (111, "step generate"));
        Assert.Equal("generate", p.Step);
        Feed(p, (200, "step compile"));
        var at = p.At(S(200));
        Assert.Equal(("compile", 0.0), (at.Step, at.StepFraction));

        // A later mkw-recomp adds a step: it is shown, not dropped.
        var next = Feed(new RecompSetupProgress(false), (0, "step shaders"), (80, "step tonemap"));
        var unknown = next.At(S(81));
        Assert.Equal(("tonemap", "Working (tonemap)", 9), (unknown.Step, unknown.Label, unknown.StepCount));
        Assert.True(unknown.Indeterminate);
    }

    [Fact]
    public void Silence_at_the_very_start_is_another_setup_holding_the_folder()
    {
        var p = new RecompSetupProgress(false);
        Assert.Equal(Strings.SetupStarting, p.At(S(2)).Label);
        Assert.Equal(Strings.SetupWaitingForLock, p.At(S(11)).Label);
        Assert.Null(p.At(S(11)).Remaining);
        p.Line("step check", S(30));
        Assert.Equal("Reading your game", p.At(S(31)).Label);
    }

    [Theory]
    [InlineData("source", "no earlier setup in this folder", "Pick your copy of the game")]
    [InlineData("source", @"D:\dumps\game.nx", @"D:\dumps\game.nx could not be unpacked")]
    [InlineData("moved", @"D:\dumps\game.nup", @"no longer at D:\dumps\game.nup")]
    [InlineData("nogame", @"D:\photos", @"D:\photos is not a copy of the game")]
    [InlineData("encrypted", @"D:\dump\exefs\main", "still encrypted")]
    [InlineData("version", "0123456789abcdef", "not supported (build 0123456789abcdef)")]
    [InlineData("romfs", "1200", "incomplete: its data has only 1200")]
    [InlineData("mismatch", "ResourceSizeTable.Product.100", "mixes two versions")]
    [InlineData("space", @"25.0 GB, 3.1 GB free in C:\Games", @"needs 25.0 GB, 3.1 GB free in C:\Games")]
    [InlineData("failed", "compiling the game code failed: out of memory", "\"Building the game\" failed: compiling the game code failed: out of memory")]
    [InlineData("io", "[WinError 5] Access is denied", "could not be read or written: [WinError 5] Access is denied")]
    [InlineData("no_setup", "", "setup tools are missing")]
    [InlineData("cancelled", "", "Setup stopped")]
    [InlineData("stopped", "", "ended unexpectedly during \"Building the game\" (exit code 3)")]
    [InlineData("a_code_of_a_later_release", "what it said", "what it said")]
    public void Every_error_code_of_the_setup_has_words_for_the_player(string code, string detail, string expected)
    {
        var message = Strings.SetupError(code, detail, "Building the game", 3);
        Assert.Contains(expected, message, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_is_decoded_as_utf8_or_as_the_systems_code_page()
    {
        Assert.Equal("step check", RecompSetup.Decode("step check"));
        var utf8 = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes(@"error moved D:\Adrià\jogo.nup"));
        Assert.Equal(@"error moved D:\Adrià\jogo.nup", RecompSetup.Decode(utf8));
        // Not UTF-8 (a lone 0xE0): the system's code page, and never an exception.
        Assert.StartsWith(@"error moved D:\Adri", RecompSetup.Decode("error moved D:\\Adri\u00E0\\jogo.nup"), StringComparison.Ordinal);
    }
}

/// <summary>Whether a setup is needed (the gate <c>mkw.exe --play</c> skips), the player's copy, and the settings the launcher keeps.</summary>
public class SetupGateTests
{
    [Fact]
    public void The_state_file_is_read_as_the_setup_writes_it()
    {
        var state = RecompSetup.StateIn(SetupRig.StateJson("0.1.15-mkwmp", @"D:\dumps\game.nup"))!;
        Assert.Equal(("0.1.15-mkwmp", @"D:\dumps\game.nup", false), (state.Port, state.Source, state.Copy));
        Assert.True(RecompSetup.StateIn(SetupRig.StateJson(null, @"D:\dump", "copy"))!.Copy);
        Assert.Null(RecompSetup.StateIn(SetupRig.StateJson(null, @"D:\dump"))!.Port);
        // Another PREP_VERSION counts as no setup at all for prepare.py, so it does here; so does a broken file.
        Assert.Null(RecompSetup.StateIn("""{"done":{},"source":"D:\\dump","version":2,"port":"0.1.15-mkwmp"}"""));
        Assert.Null(RecompSetup.StateIn("""{"done":{"generate":"c0dd6cf12c07c23a 1.8.0 x"}}"""));
        Assert.Null(RecompSetup.StateIn("{ not json"));
        // Before 0.1.5 the mode was only in the romfs step's key.
        Assert.True(RecompSetup.StateIn("""{"done":{"romfs":"copy 1.8.0"},"source":"D:\\dump","version":1}""")!.Copy);
        // 0.1.16 keeps one more key under "done" (the HDR tone mapper's programs), which is not a step.
        var with016 = RecompSetup.StateIn("""{"done":{"exefs":"1.8.0","romfs":"link 1.8.0","generate":"c9bbcb75a9ac9a49 1.8.0 b05897af8b40fec6","compile":"c9bbcb75a9ac9a49 1.8.0 windows 41829b71d97ad65a","shaders":"1.8.0 6b8e0b1447f058ce","tonemap":"1.8.0 6b8e0b1447f058ce renodx 0123456789abcdef","warm":"c9bbcb75a9ac9a49 1.8.0 6b8e0b1447f058ce pairs3 pack2","cleanup":""},"source":"D:\\dumps\\game.nup","romfs_mode":"link","version":1,"port":"0.1.16"}""")!;
        Assert.Equal(("0.1.16", @"D:\dumps\game.nup", false), (with016.Port, with016.Source, with016.Copy));
    }

    [Fact]
    public void A_game_set_up_for_the_installed_runtime_needs_nothing()
    {
        using var rig = new SetupRig();
        rig.SetUp();
        var status = rig.Target.SetupStatus(rig.Install)!;
        Assert.Null(status.Needed);
        Assert.Equal(rig.Dump, status.DumpPath);
        Assert.Null(((IGameTarget)rig.Target).SetupNeeded(rig.Install));
        Assert.Equal(TargetStatus.Ready, rig.Target.Check(rig.Install).Status);
    }

    [Fact]
    public void A_folder_never_set_up_asks_for_the_players_game()
    {
        using var rig = new SetupRig();
        var plan = rig.Target.SetupStatus(rig.Install)!.Needed!;
        Assert.Equal((GameSetupReason.NotSetUp, Strings.SetupNotSetUp, true, (string?)null), (plan.Reason, plan.Message, plan.NeedsDump, plan.DumpPath));
        // A state file of another setup version is no setup.
        File.WriteAllText(Path.Combine(rig.Game, "setup-state.json"), """{"done":{},"source":"D:\\dump","version":7}""");
        Assert.Equal(GameSetupReason.NotSetUp, rig.Target.SetupStatus(rig.Install)!.Needed!.Reason);
    }

    [Fact]
    public void Another_port_in_the_state_means_set_up_again_and_is_never_a_crash()
    {
        using var rig = new SetupRig();
        // The folder was set up by the runtime before the update: mkw.exe --play would start it with the old code and shaders.
        rig.SetUp(port: "0.1.14-mkwmp");
        var plan = rig.Target.SetupStatus(rig.Install)!.Needed!;
        Assert.Equal((GameSetupReason.RuntimeChanged, false, rig.Dump), (plan.Reason, plan.NeedsDump, plan.DumpPath));
        Assert.Contains("mkw-recomp 0.1.18", plan.Message, StringComparison.Ordinal);
        // The target's own gates do not send the player to mkw-recomp's screen for it.
        var check = rig.Target.Check(rig.Install);
        Assert.Equal((TargetStatus.Ready, (string?)null), (check.Status, check.Problem));

        // A CI build's port (a commit) is another port too.
        rig.SetUp(port: "3c51af6");
        Assert.Equal(GameSetupReason.RuntimeChanged, rig.Target.SetupStatus(rig.Install)!.Needed!.Reason);
    }

    [Fact]
    public void A_game_built_for_another_runtime_must_be_built_again()
    {
        using var rig = new SetupRig();
        rig.SetUp(id: SetupRig.PreviousId); // 0.1.15-mkwmp's game id, with our port by some accident
        var plan = rig.Target.SetupStatus(rig.Install)!.Needed!;
        Assert.Equal((GameSetupReason.GameMismatch, Strings.SetupGameMismatch, false), (plan.Reason, plan.Message, plan.NeedsDump));
        Assert.DoesNotContain("open mkw-recomp", rig.Target.Check(rig.Install).Problem ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Game_code_built_for_the_other_processor_level_is_built_again()
    {
        using var rig = new SetupRig();
        rig.SetUp();
        var state = Path.Combine(rig.Game, "setup-state.json");
        var level = Path.Combine(rig.Game, "setup", "cpu-level");
        // Compiled by the main package: v3, no level at the end of the compile step's key (prepare.py).
        File.WriteAllText(state, File.ReadAllText(state).Replace("\"generate\"", "\"compile\":\"c9bbcb75a9ac9a49 1.8.0 windows 41829b71d97ad65a\",\"generate\"", StringComparison.Ordinal));
        Assert.Equal("v3", RecompSetup.ReadState(rig.Game)!.CompileLevel);
        Assert.Null(rig.Target.SetupStatus(rig.Install)!.Needed);

        // The install is now the no-AVX2 package (a folder brought to a processor without AVX2): mkwgame.id cannot tell.
        Directory.CreateDirectory(Path.GetDirectoryName(level)!);
        File.WriteAllText(level, "v2\n");
        Assert.Equal("v2", RecompSetup.CpuLevel(rig.Install));
        var plan = rig.Target.SetupStatus(rig.Install)!.Needed!;
        Assert.Equal((GameSetupReason.RuntimeChanged, false, rig.Dump), (plan.Reason, plan.NeedsDump, plan.DumpPath));
        Assert.Equal(Strings.SetupRuntimeChanged("mkw-recomp", "0.1.18 (no AVX2)"), plan.Message);

        // Compiled for v2 by it: nothing to do.
        File.WriteAllText(state, File.ReadAllText(state).Replace("41829b71d97ad65a\"", "41829b71d97ad65a v2\"", StringComparison.Ordinal));
        Assert.Equal("v2", RecompSetup.ReadState(rig.Game)!.CompileLevel);
        Assert.Null(rig.Target.SetupStatus(rig.Install)!.Needed);

        // Back to the main package: compiled again for v3 (prepare.py's own key says so too).
        File.Delete(level);
        Assert.Equal(GameSetupReason.RuntimeChanged, rig.Target.SetupStatus(rig.Install)!.Needed!.Reason);
    }

    [Fact]
    public void The_processor_check_follows_the_package_s_level()
    {
        Assert.Null(RecompSetup.CpuProblem("v3", v3: true, v2: true));
        Assert.Equal(Strings.SetupCpu, RecompSetup.CpuProblem("v3", v3: false, v2: true));
        // The no-AVX2 package asks only SSE4.2 (x86-64-v2).
        Assert.Null(RecompSetup.CpuProblem("v2", v3: false, v2: true));
        Assert.Equal(Strings.SetupCpuV2, RecompSetup.CpuProblem("v2", v3: false, v2: false));
        Assert.DoesNotContain("AVX2", Strings.SetupCpuV2, StringComparison.Ordinal);
    }

    [Fact]
    public void A_setup_that_stopped_half_way_is_continued()
    {
        using var rig = new SetupRig();
        rig.SetUp(port: null, built: false); // stopped while compiling: a state, no game code, no port yet
        var plan = rig.Target.SetupStatus(rig.Install)!.Needed!;
        Assert.Equal((GameSetupReason.Unfinished, Strings.SetupUnfinished, false, rig.Dump), (plan.Reason, plan.Message, plan.NeedsDump, plan.DumpPath));
        rig.SetUp(port: null); // stopped in the shaders: the game code is there, the port is not written yet
        Assert.Equal(GameSetupReason.Unfinished, rig.Target.SetupStatus(rig.Install)!.Needed!.Reason);
        rig.SetUp();
        File.Delete(Path.Combine(rig.Game, "mkwgame.dll")); // deleted by hand
        Assert.Equal(GameSetupReason.Unfinished, rig.Target.SetupStatus(rig.Install)!.Needed!.Reason);
    }

    [Fact]
    public void A_linked_game_that_moved_is_asked_for_again_and_a_copied_one_is_not()
    {
        using var rig = new SetupRig();
        rig.SetUp();
        File.Delete(rig.Dump);
        var plan = rig.Target.SetupStatus(rig.Install)!.Needed!;
        Assert.Equal((GameSetupReason.DumpMissing, true, rig.Dump), (plan.Reason, plan.NeedsDump, plan.DumpPath));
        Assert.Contains(rig.Dump, plan.Message, StringComparison.Ordinal);

        // Copied: the install holds the whole game, so the original may go (prepare.py takes the install for the source).
        rig.SetUp(mode: "copy");
        Assert.Equal(GameSetupReason.DumpMissing, rig.Target.SetupStatus(rig.Install)!.Needed!.Reason); // not while the copy is not there
        Directory.CreateDirectory(Path.Combine(rig.Game, "exefs"));
        File.WriteAllText(Path.Combine(rig.Game, "exefs", "main"), "NSO0");
        Directory.CreateDirectory(Path.Combine(rig.Game, "romfs"));
        Assert.Null(rig.Target.SetupStatus(rig.Install)!.Needed);
    }

    [Fact]
    public void An_install_the_launcher_does_not_set_up_keeps_the_old_gate()
    {
        using var rig = new SetupRig();
        // The player's own upstream 0.1.15: its setup is mkw-recomp's business, as before.
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        Assert.Null(rig.Target.SetupStatus(rig.Install));
        Assert.Null(((IGameTarget)rig.Target).SetupNeeded(rig.Install));
        File.WriteAllText(Path.Combine(rig.Game, "mkwgame.id"), SetupRig.PreviousId + "\n");
        Assert.Equal((TargetStatus.NeedsRebuild, Recomp.RebuildProblem), (rig.Target.Check(rig.Install).Status, rig.Target.Check(rig.Install).Problem));
        // A target without a setup (the interface's defaults): nothing needed, and running one is refused, not thrown.
        Assert.Equal([SetupRig.Pinned], RecompSetup.Runtimes);
    }

    [Fact]
    public void The_players_copy_is_recognised_by_what_the_setup_itself_takes()
    {
        using var rig = new SetupRig();
        Assert.Null(RecompSetup.DumpProblem(rig.Dump)); // a .nup by its first bytes
        var nx = Path.Combine(rig.Root, "game.nx");
        File.WriteAllBytes(nx, [.. "NXAR"u8, 0, 0, 0, 0]);
        Assert.Null(RecompSetup.DumpProblem(nx));
        // The name is not what counts.
        var fake = Path.Combine(rig.Root, "holiday.nup");
        File.WriteAllText(fake, "a photo, renamed");
        Assert.Equal(Strings.SetupDumpNotAGame(fake), RecompSetup.DumpProblem(fake));
        var tiny = Path.Combine(rig.Root, "tiny.nup");
        File.WriteAllBytes(tiny, [0x4E, 0x55]);
        Assert.NotNull(RecompSetup.DumpProblem(tiny));
        Assert.Equal(Strings.SetupDumpNotFound(Path.Combine(rig.Root, "gone.nup")), RecompSetup.DumpProblem(Path.Combine(rig.Root, "gone.nup")));
        Assert.Equal(Strings.SetupPickDump, RecompSetup.DumpProblem(null));
        Assert.Equal(Strings.SetupPickDump, RecompSetup.DumpProblem("  "));

        // Loose folders: exefs/main and romfs/ in the folder or up to two levels below it.
        var loose = Path.Combine(rig.Root, "loose");
        void Game(string at)
        {
            Directory.CreateDirectory(Path.Combine(at, "exefs"));
            File.WriteAllText(Path.Combine(at, "exefs", "main"), "NSO0");
            Directory.CreateDirectory(Path.Combine(at, "romfs"));
        }
        Directory.CreateDirectory(loose);
        Assert.Equal(Strings.SetupDumpNotAGame(loose), RecompSetup.DumpProblem(loose));
        Game(Path.Combine(loose, "a", "b", "c"));
        Assert.NotNull(RecompSetup.DumpProblem(loose)); // three levels: too deep, as for the setup
        Assert.Null(RecompSetup.DumpProblem(Path.Combine(loose, "a")));
        Game(loose);
        Assert.Null(RecompSetup.DumpProblem(loose));
        // exefs alone is not a game.
        var half = Path.Combine(rig.Root, "half");
        Directory.CreateDirectory(Path.Combine(half, "exefs"));
        File.WriteAllText(Path.Combine(half, "exefs", "main"), "NSO0");
        Assert.NotNull(RecompSetup.DumpProblem(half));
    }

    [Fact]
    public void The_command_is_the_one_mkw_recomps_own_screen_builds()
    {
        using var rig = new SetupRig();
        var install = rig.Install;
        Assert.Equal(Path.Combine(rig.Game, "setup", "python", "python.exe"), RecompSetup.Python(install));
        Assert.Equal(Path.Combine(rig.Game, "setup", "src", "tools", "prepare", "prepare.py"), RecompSetup.Script(install));
        Assert.Equal(
            ["--game", rig.Game, "--runtime", rig.Game, "--port", "0.1.18", "--source", rig.Dump, "--romfs", "link"],
            RecompSetup.Arguments(install, new GameSetupRequest(rig.Dump)));
        Assert.Equal("copy", RecompSetup.Arguments(install, new GameSetupRequest(rig.Dump, GameDataMode.Copy))[^1]);
        // Repeating the last setup: neither the source nor the mode, so prepare.py takes both from its state.
        Assert.Equal(["--game", rig.Game, "--runtime", rig.Game, "--port", "0.1.18"], RecompSetup.Arguments(install, new GameSetupRequest(null, GameDataMode.Copy)));
        // "Choose game folder": the data is elsewhere, the runtime stays the folder of mkw.exe.
        var moved = install with { DataRoot = @"D:\mkw-data" };
        Assert.Equal(["--game", @"D:\mkw-data", "--runtime", rig.Game], RecompSetup.Arguments(moved, new GameSetupRequest(null)).Take(4));
    }

    [Fact]
    public void The_launcher_keeps_mkw_recomps_updater_off_and_its_start_screen_skipped()
    {
        using var rig = new SetupRig();
        IGameTarget t = rig.Target;
        var schema = t.GameSettings(rig.Install)!;
        // Shown, and locked with the reason.
        Assert.Contains("MKWMP", schema.Find("update_check")!.Locked, StringComparison.Ordinal);
        Assert.Contains("MKWMP", schema.Find("start_screen")!.Locked, StringComparison.Ordinal);
        Assert.Null(schema.Find("volume")!.Locked);
        Assert.Contains("update check", Assert.Throws<LauncherException>(() => t.WriteGameSettings(rig.Install, new Dictionary<string, string?> { ["update_check"] = "1" })).Message,
            StringComparison.Ordinal);

        var ini = Path.Combine(rig.Game, "mkw.ini");
        File.WriteAllText(ini, "# my settings\n[launcher]\nupdate_check = 1\nmods = mkwmp\n\n[audio]\nvolume = 80\n");
        Assert.Equal(["update_check = 0", "start_screen = skip"], t.ApplyOwnedSettings(rig.Install));
        Assert.Equal("# my settings\n[launcher]\nupdate_check = 0\nmods = mkwmp\nstart_screen = skip\n\n[audio]\nvolume = 80\n", File.ReadAllText(ini));
        var values = t.ReadGameSettings(rig.Install);
        Assert.Equal(("0", "skip", "80"), (values["update_check"], values["start_screen"], values["volume"]));
        Assert.Empty(t.ApplyOwnedSettings(rig.Install)); // already so: the file is not written again

        // The player (or mkw-recomp's own menu) turned one back: put right at the next install or setup, never while the game runs.
        File.WriteAllText(ini, "[launcher]\nupdate_check = 1\nstart_screen = skip\n");
        rig.Running = true;
        Assert.Empty(t.ApplyOwnedSettings(rig.Install));
        Assert.Contains("update_check = 1", File.ReadAllText(ini), StringComparison.Ordinal);
        rig.Running = false;
        Assert.Equal(["update_check = 0"], t.ApplyOwnedSettings(rig.Install));
        // No file yet: one is made.
        File.Delete(ini);
        Assert.Equal(2, t.ApplyOwnedSettings(rig.Install).Count);
        Assert.Equal("[launcher]\nupdate_check = 0\nstart_screen = skip\n", File.ReadAllText(ini));
        // Resetting everything leaves them.
        t.WriteGameSettings(rig.Install, schema.Settings.Where(s => s.Locked is null).ToDictionary(s => s.Id, _ => (string?)null));
        Assert.Contains("update_check = 0", File.ReadAllText(ini), StringComparison.Ordinal);

        // The player's own upstream install is theirs: nothing locked, nothing written.
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        File.Delete(ini);
        Assert.Null(t.GameSettings(rig.Install)!.Find("update_check")!.Locked);
        Assert.Empty(t.ApplyOwnedSettings(rig.Install));
        Assert.False(File.Exists(ini));
    }
}

/// <summary>The driver against a stand-in for prepare.py: a real process, real pipes, a real kill.</summary>
public class SetupDriverTests
{
    [Fact]
    public async Task A_first_setup_runs_to_its_end_and_reports_every_step()
    {
        using var rig = new SetupRig();
        rig.ScriptFirstSetup();
        var dumpBefore = (File.ReadAllBytes(rig.Dump), File.GetLastWriteTimeUtc(rig.Dump));
        var progress = new SetupRig.Collect();

        var result = await rig.Run(rig.Dump, progress);

        Assert.True(result.Ok, result.Message);
        Assert.Equal((null, null, "1.8.0", 0), (result.ErrorCode, result.Message, result.GameVersion, result.ExitCode));
        // Started as mkw-recomp's own screen starts it, with the copy linked, not copied.
        Assert.Equal($"\"--game\" \"{rig.Game}\" \"--runtime\" \"{rig.Game}\" \"--port\" \"0.1.18\" \"--source\" \"{rig.Dump}\" \"--romfs\" \"link\"", rig.Arguments);
        var seen = progress.Seen;
        Assert.Equal(
            ["Reading your game", "Copying the game's program", "Finding the game's data", "Translating the game", "Building the game", "Preparing shaders", "Warming up the graphics", "Finishing up"],
            seen.Where(p => p.Step.Length > 0).Select(p => p.Label).Distinct());
        Assert.Contains(seen, p => p.Step == "compile" && Math.Abs(p.StepFraction - (100.0 / 397)) < 1e-9 && !p.Indeterminate);
        Assert.Contains(seen, p => p.GameVersion == "1.8.0");
        Assert.Equal(1, seen[^1].Fraction);
        Assert.True(seen.Zip(seen.Skip(1)).All(pair => pair.Second.Fraction >= pair.First.Fraction));
        // The gate is satisfied by what the setup left.
        Assert.Null(rig.Target.SetupStatus(rig.Install)!.Needed);
        // The whole log is kept in the launcher's logs folder, paths and all.
        Assert.Equal(Path.Combine(rig.Logs, Path.GetFileName(result.Log!)), result.Log);
        Assert.StartsWith("setup-", Path.GetFileName(result.Log), StringComparison.Ordinal);
        var log = File.ReadAllText(result.Log!);
        Assert.Contains("step compile", log, StringComparison.Ordinal);
        Assert.Contains("progress 397 397", log, StringComparison.Ordinal);
        Assert.Contains(rig.Dump, log, StringComparison.Ordinal);
        Assert.Contains("exit code 0", log, StringComparison.Ordinal);
        Assert.Contains("result: done, game 1.8.0", log, StringComparison.Ordinal);
        // The settings the launcher keeps were written with it.
        Assert.Contains("update_check = 0", File.ReadAllText(Path.Combine(rig.Game, "mkw.ini")), StringComparison.Ordinal);
        Assert.Contains("start_screen = skip", File.ReadAllText(Path.Combine(rig.Game, "mkw.ini")), StringComparison.Ordinal);
        // The player's copy was only named: same bytes, same time, and no second copy of it anywhere.
        Assert.Equal(dumpBefore.Item1, File.ReadAllBytes(rig.Dump));
        Assert.Equal(dumpBefore.Item2, File.GetLastWriteTimeUtc(rig.Dump));
        Assert.Single(Directory.EnumerateFiles(rig.Root, "*.nup", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("error version 0123456789abcdef", 1, "version", "check", "not supported (build 0123456789abcdef)")]
    [InlineData("error source no earlier setup in this folder", 2, "source", "check", "Pick your copy of the game")]
    [InlineData("error failed compiling the game code failed: clang: error", 1, "failed", "check", "\"Reading your game\" failed: compiling the game code failed: clang: error")]
    public async Task An_error_of_the_setup_becomes_a_code_and_words_for_the_player(string line, int exit, string code, string step, string words)
    {
        using var rig = new SetupRig();
        rig.Script("echo step check", $"echo {line}", $"exit /b {exit}");

        var result = await rig.Run(rig.Dump);

        Assert.False(result.Ok);
        Assert.Equal((code, step, exit), (result.ErrorCode, result.FailedStep, result.ExitCode));
        Assert.Contains(words, result.Message, StringComparison.Ordinal);
        Assert.Equal(line[(line.IndexOf(code, StringComparison.Ordinal) + code.Length + 1)..], result.Detail);
        Assert.Contains($"result: {code}", File.ReadAllText(result.Log!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_in_the_shaders_step_names_its_download()
    {
        using var rig = new SetupRig();
        rig.Script("echo step shaders", "echo error failed spec.py failed: urlopen error timed out", "exit /b 1");
        var result = await rig.Run(rig.Dump);
        Assert.Equal(("failed", "shaders"), (result.ErrorCode, result.FailedStep));
        Assert.Contains("\"Preparing shaders\" failed: spec.py failed", result.Message, StringComparison.Ordinal);
        Assert.Contains(Platform.WineHost.Detected ? Strings.SetupShadersProton : Strings.SetupShadersOnline, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_setup_that_ends_without_a_word_is_stopped_not_done()
    {
        using var rig = new SetupRig();
        rig.Script("echo step check", "echo step compile", "echo progress 3 397", ">&2 echo Traceback: something broke", "exit /b 3");
        var result = await rig.Run(rig.Dump);
        Assert.Equal((false, "stopped", "compile", 3), (result.Ok, result.ErrorCode, result.FailedStep, result.ExitCode));
        Assert.Contains("ended unexpectedly during \"Building the game\" (exit code 3)", result.Message, StringComparison.Ordinal);
        Assert.Contains("! Traceback: something broke", File.ReadAllText(result.Log!), StringComparison.Ordinal); // what it wrote to stderr is kept

        // Exit 0 without "done" is not a finished setup either.
        rig.Script("echo step check", "exit /b 0");
        Assert.Equal("stopped", (await rig.Run(rig.Dump)).ErrorCode);
    }

    [Fact]
    public async Task Cancel_ends_the_setup_and_the_tools_it_started()
    {
        using var rig = new SetupRig();
        rig.Script("echo step check", "echo step compile", "echo progress 10 397", rig.Hangs, "echo done", "exit /b 0");
        using var cancel = new CancellationTokenSource();
        var progress = new SetupRig.Collect();
        var run = rig.Run(rig.Dump, progress, cancel: cancel.Token);
        var child = await rig.WaitForChild();
        Assert.True(SetupRig.Alive(child));
        Assert.Contains(progress.Seen, p => p.Step == "compile");

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Rig.Until("the setup's child process to end", () => !SetupRig.Alive(child));
        var log = File.ReadAllText(RecompSetup.Logs(rig.Logs)[0]);
        Assert.Contains("result: cancelled", log, StringComparison.Ordinal);
        // The folder is free again: the next setup starts.
        rig.ScriptFirstSetup();
        Assert.True((await rig.Run(rig.Dump)).Ok);
    }

    [Fact]
    public async Task A_stopped_setup_continues_from_its_state_without_asking_for_the_game_again()
    {
        using var rig = new SetupRig();
        // The first run gets as far as compiling and is stopped there; prepare.py has saved its state after each step.
        File.WriteAllText(Path.Combine(rig.Scripts, "partial.json"), SetupRig.StateJson(null, rig.Dump));
        var marker = Path.Combine(rig.Scripts, "second-run");
        rig.Script(
        [
            $"if exist \"{marker}\" goto again",
            $">\"{marker}\" echo 1",
            "echo step check", "echo version 1.8.0", "echo step exefs", "echo step romfs", "echo step generate",
            $"copy /y \"{Path.Combine(rig.Scripts, "partial.json")}\" \"{Path.Combine(rig.Game, "setup-state.json")}\" >nul",
            "echo step compile", "echo progress 200 397",
            rig.Hangs,
            "exit /b 1",
            ":again",
            "echo step check", "echo version 1.8.0", "echo skip exefs", "echo skip romfs", "echo skip generate",
            "echo step compile", "echo progress 397 397", "echo step shaders",
            .. rig.Builds(),
            "echo step warm", "echo step cleanup", "echo done", "exit /b 0",
        ]);
        using var cancel = new CancellationTokenSource();
        var first = rig.Run(rig.Dump, cancel: cancel.Token);
        await rig.WaitForChild();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var plan = rig.Target.SetupStatus(rig.Install)!.Needed!;
        Assert.Equal((GameSetupReason.Unfinished, false), (plan.Reason, plan.NeedsDump));

        var progress = new SetupRig.Collect();
        var result = await rig.Run(null, progress);

        Assert.True(result.Ok, result.Message);
        // No --source and no --romfs: prepare.py repeats the last setup, and only the steps left.
        Assert.Equal($"\"--game\" \"{rig.Game}\" \"--runtime\" \"{rig.Game}\" \"--port\" \"0.1.18\"", rig.Arguments);
        var log = File.ReadAllText(result.Log!);
        Assert.Contains("skip generate", log, StringComparison.Ordinal);
        Assert.Contains("skipped exefs romfs generate", log, StringComparison.Ordinal);
        Assert.DoesNotContain(progress.Seen, p => p.Step == "generate");
        Assert.Null(rig.Target.SetupStatus(rig.Install)!.Needed);
    }

    [Fact]
    public async Task Nothing_is_started_while_the_game_runs()
    {
        using var rig = new SetupRig();
        rig.ScriptFirstSetup();
        rig.Running = true;
        var result = await rig.Run(rig.Dump);
        Assert.Equal((false, "running", Strings.SetupCloseGame), (result.Ok, result.ErrorCode, result.Message));
        Assert.Null(rig.Arguments);
        Assert.False(File.Exists(Path.Combine(rig.Game, "mkw.ini"))); // not even its settings
        Assert.False(Directory.Exists(rig.Logs));
    }

    [Fact]
    public async Task One_setup_of_a_folder_at_a_time()
    {
        using var rig = new SetupRig();
        rig.Script("echo step check", rig.Hangs, "exit /b 1");
        using var cancel = new CancellationTokenSource();
        var first = rig.Run(rig.Dump, cancel: cancel.Token);
        await rig.WaitForChild();

        var second = await rig.Run(rig.Dump);
        Assert.Equal(("busy", Strings.SetupAlreadyRunning), (second.ErrorCode, second.Message));

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task What_the_player_picked_is_checked_before_anything_starts()
    {
        using var rig = new SetupRig();
        rig.ScriptFirstSetup();
        var photo = Path.Combine(rig.Root, "holiday.jpg");
        File.WriteAllText(photo, "not a game");

        var wrong = await rig.Run(photo);
        Assert.Equal(("nogame", Strings.SetupDumpNotAGame(photo), photo), (wrong.ErrorCode, wrong.Message, wrong.Detail));
        var missing = await rig.Run(Path.Combine(rig.Root, "gone.nup"));
        Assert.Equal("nogame", missing.ErrorCode);
        Assert.Contains("Nothing at", missing.Message, StringComparison.Ordinal);
        // Nothing to repeat: there was no setup here yet.
        var nothing = await rig.Run(null);
        Assert.Equal(("source", Strings.SetupPickDump), (nothing.ErrorCode, nothing.Message));
        Assert.Null(rig.Arguments);
    }

    [Fact]
    public async Task The_machine_is_checked_before_anything_starts()
    {
        using var rig = new SetupRig();
        rig.ScriptFirstSetup();
        // A first build needs room (the setup's own check asks for less than its real peak).
        rig.Free = RecompSetup.BuildSpace - 1;
        var full = await rig.Run(rig.Dump);
        Assert.Equal("space", full.ErrorCode);
        Assert.StartsWith("Not enough free space on ", full.Message, StringComparison.Ordinal);
        // Copying the game's data needs its 22 GiB on top.
        rig.Free = RecompSetup.BuildSpace + 1;
        Assert.Equal("space", (await rig.Run(rig.Dump, mode: GameDataMode.Copy)).ErrorCode);
        Assert.Null(rig.Arguments);
        // Once built, an update needs none of it.
        rig.SetUp(port: "0.1.14-mkwmp");
        rig.Free = 1;
        Assert.True((await rig.Run(null)).Ok);

        // The runtime refuses a data folder with a longer path than this.
        var far = rig.Install with { DataRoot = Path.Combine(rig.Root, new string('d', RecompSetup.GameDirMax)) };
        var tooLong = await rig.Target.RunSetupAsync(far, new GameSetupRequest(rig.Dump), null, default);
        Assert.Equal("path", tooLong.ErrorCode);
    }

    [Fact]
    public async Task Without_the_runtimes_tools_there_is_no_setup()
    {
        using var rig = new SetupRig();
        var real = new MkwRecompTarget("1.4.0") { RunningOverride = () => false }; // the install's own Python: there is none
        var result = await real.RunSetupAsync(rig.Install, new GameSetupRequest(rig.Dump, LogFolder: rig.Logs), null, default);
        Assert.Equal(("no_setup", Strings.SetupToolsMissing), (result.ErrorCode, result.Message));

        // An install the launcher does not set up, and a target without any setup: refused, not thrown.
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        Assert.Equal("none", (await rig.Run(rig.Dump)).ErrorCode);
    }

    /// <summary>
    /// Only with <c>MKWMP_SETUP_LIVE=&lt;folder of an unpacked pinned runtime&gt;</c>: the runtime's real
    /// Python and prepare.py, started by the driver, given a folder that is not a game. It checks
    /// the command line, the pipes and the event lines against the real program without any game.
    /// </summary>
    [Fact]
    public async Task Live_the_real_setup_program_answers_the_driver()
    {
        if (Environment.GetEnvironmentVariable("MKWMP_SETUP_LIVE") is not { Length: > 0 } runtime)
        {
            return;
        }
        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => false, SetupFreeSpace = _ => null };
        var install = target.Detect(runtime);
        Assert.Equal(SetupRig.Pinned, install?.Version);
        var notGame = Rig.NewFolder("live-not-a-game");
        Directory.CreateDirectory(Path.Combine(notGame, "exefs"));
        Directory.CreateDirectory(Path.Combine(notGame, "romfs"));
        File.WriteAllBytes(Path.Combine(notGame, "exefs", "main"), new byte[0x100]);
        var progress = new SetupRig.Collect();

        var result = await target.RunSetupAsync(install!, new GameSetupRequest(notGame, LogFolder: Path.Combine(notGame, "logs")), progress, default);

        Assert.Equal((false, "encrypted", "check", 1), (result.Ok, result.ErrorCode, result.FailedStep, result.ExitCode));
        Assert.Contains(Path.Combine(notGame, "exefs", "main"), result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(progress.Seen, p => p.Step == "check" && p.Label == "Reading your game");
        Assert.Contains("step check", File.ReadAllText(result.Log!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Old_setup_logs_are_pruned()
    {
        using var rig = new SetupRig();
        Directory.CreateDirectory(rig.Logs);
        for (var i = 0; i < RecompSetup.KeepLogs + 5; i++)
        {
            File.WriteAllText(Path.Combine(rig.Logs, $"setup-20260101-0000{i:00}.log"), "old");
        }
        File.WriteAllText(Path.Combine(rig.Logs, "launcher-20260101-000000.log"), "not a setup log");
        rig.ScriptFirstSetup();
        var result = await rig.Run(rig.Dump);
        var logs = RecompSetup.Logs(rig.Logs);
        Assert.Equal(RecompSetup.KeepLogs, logs.Count);
        Assert.Equal(result.Log, logs[0]);
        Assert.True(File.Exists(Path.Combine(rig.Logs, "launcher-20260101-000000.log")));
    }
}

/// <summary>The same through the launcher: what blocks PLAY and in which order, and the view the window shows.</summary>
public class LauncherGameSetupTests
{
    /// <summary>The launcher of <see cref="Rig"/> with the pinned mkw-recomp in its game folder, and a stand-in setup.</summary>
    private sealed class Home : IAsyncDisposable
    {
        private Home(Rig rig, SetupRig files)
        {
            (Rig, Files) = (rig, files);
            Launcher.Changed += (_, _) =>
            {
                lock (States)
                {
                    States.Add(Launcher.GameSetup);
                }
            };
        }

        public Rig Rig { get; }

        /// <summary>The stand-in's scripts and the player's copy; the game folder is the launcher rig's.</summary>
        public SetupRig Files { get; }
        public Launcher Launcher => Rig.Launcher;
        public List<GameSetupView> States { get; } = [];

        public static async Task<Home> New(Func<LauncherSettings, LauncherSettings>? edit = null, bool installPatch = true, Action<string, SetupRig>? arrange = null, FakeSeatServer? server = null)
        {
            var rig = new Rig(server ?? new FakeSeatServer(), edit, installPatch);
            var files = new SetupRig();
            // The game folder becomes the pinned mkw-recomp, and its setup the stand-in.
            File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), RuntimeRig.NewExe);
            SetupRig.Wire(rig.Target, files);
            arrange?.Invoke(rig.Game, files);
            await rig.Launcher.ChooseGameFolderAsync(rig.Game);
            return new Home(rig, files);
        }

        /// <summary>The stand-in's lines that leave this launcher's game folder set up.</summary>
        public string[] Builds()
        {
            File.WriteAllText(Path.Combine(Files.Scripts, "state.json"), SetupRig.StateJson(SetupRig.Pinned, Files.Dump));
            return
            [
                $"copy /y \"{Path.Combine(Files.Scripts, "state.json")}\" \"{Path.Combine(Rig.Game, "setup-state.json")}\" >nul",
                $">\"{Path.Combine(Rig.Game, "mkwgame.dll")}\" echo built",
                $">\"{Path.Combine(Rig.Game, "mkwgame.id")}\" echo {SetupRig.PinnedId}",
            ];
        }

        public static void SetUp(string game, SetupRig files, string? port = SetupRig.Pinned)
        {
            File.WriteAllText(Path.Combine(game, "setup-state.json"), SetupRig.StateJson(port, files.Dump));
            File.WriteAllText(Path.Combine(game, "mkwgame.dll"), "built");
            File.WriteAllText(Path.Combine(game, "mkwgame.id"), SetupRig.PinnedId + "\n");
        }

        public async ValueTask DisposeAsync()
        {
            await Rig.DisposeAsync();
            Files.Dispose();
        }
    }

    [Fact]
    public async Task A_game_that_is_not_set_up_blocks_play_with_a_fix_and_not_a_crash()
    {
        await using var home = await Home.New();
        var view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.NeedsSetup, true, GameSetupReason.NotSetUp, Strings.SetupNotSetUp, true), (view.State, view.Available, view.Reason, view.Message, view.NeedsDump));
        Assert.Equal(Strings.SetupNotSetUp, home.Launcher.PlayProblem);
        Assert.Equal((TargetStatus.NeedsRebuild, Strings.SetupNotSetUp), (home.Launcher.Game.Check!.Status, home.Launcher.Game.Check.Problem));

        // PLAY says the same and starts nothing.
        await home.Launcher.PlayAsync();
        var session = await home.Rig.SettleSession();
        Assert.Equal((SessionState.Failed, Strings.SetupNotSetUp), (session.State, session.Status));
        Assert.False(home.Rig.Started);
        Assert.Equal(0, home.Rig.Server.Joins);
    }

    [Fact]
    public async Task The_runtime_was_updated_so_the_port_no_longer_matches_and_play_waits_for_the_setup()
    {
        await using var home = await Home.New(arrange: (game, files) => Home.SetUp(game, files, port: "0.1.14-mkwmp"));
        var view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.NeedsSetup, GameSetupReason.RuntimeChanged, false, home.Files.Dump), (view.State, view.Reason, view.NeedsDump, view.DumpPath));
        Assert.Equal(Strings.SetupRuntimeChanged("mkw-recomp", "0.1.18"), home.Launcher.PlayProblem);

        // "Set up again" needs no game picked: it repeats the last setup, and PLAY is free afterwards.
        home.Files.Script(["echo step check", "echo version 1.8.0", "echo skip exefs", "echo skip romfs", "echo skip generate", "echo skip compile",
            "echo step shaders", .. home.Builds(), "echo skip warm", "echo step cleanup", "echo done", "exit /b 0"]);
        await home.Launcher.RunGameSetupAsync(null, default);

        view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.Done, (GameSetupReason?)null, Strings.SetupDone, "1.8.0"), (view.State, view.Reason, view.Message, view.GameVersion));
        Assert.Null(home.Launcher.PlayProblem);
        Assert.Equal(TargetStatus.Ready, home.Launcher.Game.Check!.Status);
        Assert.DoesNotContain("--source", home.Files.Arguments, StringComparison.Ordinal);
        await home.Launcher.PlayAsync();
        await Rig.Until("the game to start", () => home.Rig.Started);
        await home.Launcher.StopAsync();
    }

    [Fact]
    public async Task What_blocks_play_comes_in_order_game_folder_then_setup_then_integration()
    {
        // No game folder: that first.
        await using (var none = await Home.New(s => s with { Game = "" }))
        {
            await none.Launcher.SaveSettingsAsync(none.Launcher.Settings with { Game = Path.Combine(none.Rig.Root, "nowhere") });
            Assert.Equal(Strings.SetGameFolder("the one with mkw.exe"), none.Launcher.PlayProblem);
            Assert.Equal((GameSetupState.Idle, false), (none.Launcher.GameSetup.State, none.Launcher.GameSetup.Available));
        }
        // The setup before the integration's files...
        await using (var noPatch = await Home.New(installPatch: false))
        {
            Assert.False(noPatch.Launcher.Game.Integration!.Ok);
            Assert.Equal(Strings.SetupNotSetUp, noPatch.Launcher.PlayProblem);
            // ...and once the game is set up, the integration is what is left.
            Home.SetUp(noPatch.Rig.Game, noPatch.Files);
            await noPatch.Launcher.ChooseGameFolderAsync(noPatch.Rig.Game);
            Assert.Equal(noPatch.Launcher.Game.Integration!.Summary, noPatch.Launcher.PlayProblem);
            Assert.Equal(TargetStatus.NoIntegration, noPatch.Launcher.Game.Check!.Status);
        }
        // ...and before the integration's switch in the settings.
        await using (var off = await Home.New(s => s with { Patch = false }))
        {
            Assert.Equal(Strings.SetupNotSetUp, off.Launcher.PlayProblem);
            Home.SetUp(off.Rig.Game, off.Files);
            await off.Launcher.ChooseGameFolderAsync(off.Rig.Game);
            Assert.Equal(Strings.InstallPatchFirst, off.Launcher.PlayProblem);
        }
        // What the player must fix in the settings first still comes first.
        await using (var noConsent = await Home.New(s => s with { AdultConsent = false }))
        {
            Assert.Equal(Strings.NoConsent, noConsent.Launcher.PlayProblem);
            Assert.Equal(GameSetupState.NeedsSetup, noConsent.Launcher.GameSetup.State);
        }
    }

    [Fact]
    public async Task The_window_sees_the_setup_run_step_by_step_and_end()
    {
        await using var home = await Home.New();
        home.Files.Script(
        [
            "echo step check", "echo version 1.8.0", "echo step exefs", "echo step romfs", "echo step generate",
            "echo step compile", "echo progress 200 397", "echo step shaders",
            .. home.Builds(),
            "echo step warm", "echo step cleanup", "echo done", "exit /b 0",
        ]);

        await home.Launcher.RunGameSetupAsync(home.Files.Dump, default);

        var view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.Done, Strings.SetupDone, false, home.Files.Dump, "1.8.0"), (view.State, view.Message, view.NeedsDump, view.DumpPath, view.GameVersion));
        Assert.False(view.Busy);
        Assert.Null(home.Launcher.PlayProblem);
        Assert.StartsWith(Path.Combine(home.Launcher.LauncherFolder, "logs", "setup-"), view.LastLog, StringComparison.Ordinal);
        Assert.Contains("step compile", File.ReadAllText(view.LastLog!), StringComparison.Ordinal);
        List<GameSetupView> states;
        lock (home.States)
        {
            states = [.. home.States];
        }
        var running = states.Where(s => s.State == GameSetupState.Running).ToList();
        Assert.Equal(
            ["Reading your game", "Copying the game's program", "Finding the game's data", "Translating the game", "Building the game", "Preparing shaders", "Warming up the graphics", "Finishing up"],
            running.Where(s => s.Step is { Length: > 0 }).Select(s => s.StepLabel).Distinct());
        Assert.Contains(running, s => s.Step == "compile" && s.Remaining is not null && s.Progress > 0);
        Assert.True(running.Zip(running.Skip(1)).All(pair => pair.Second.Progress >= pair.First.Progress));
        // From Running straight to Done: never "needs setup" again in between.
        var after = states.SkipWhile(s => s.State != GameSetupState.Running).SkipWhile(s => s.State == GameSetupState.Running).ToList();
        Assert.All(after, s => Assert.Equal(GameSetupState.Done, s.State));
        // mkw-recomp's updater is off and its start screen skipped, in the game's own file and in the view.
        Assert.Equal(("0", "skip"), (home.Launcher.GameSettings.Value("update_check"), home.Launcher.GameSettings.Value("start_screen")));
        Assert.NotNull(home.Launcher.GameSettings.Schema!.Find("update_check")!.Locked);
    }

    [Fact]
    public async Task A_failed_setup_says_why_keeps_its_log_and_can_be_tried_again()
    {
        await using var home = await Home.New();
        home.Files.Script("echo step check", "echo error encrypted D:\\dump\\exefs\\main", "exit /b 1");

        await home.Launcher.RunGameSetupAsync(home.Files.Dump, default);

        var view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.Failed, "encrypted", GameSetupReason.NotSetUp), (view.State, view.ErrorCode, view.Reason));
        Assert.Contains("still encrypted", view.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(view.LastLog));
        Assert.Equal(Strings.SetupNotSetUp, home.Launcher.PlayProblem); // PLAY is blocked by the cause, not by the failure

        // Dismissed: back to "needs setup", the log still named.
        await home.Launcher.CancelGameSetup();
        view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.NeedsSetup, Strings.SetupNotSetUp, (string?)null), (view.State, view.Message, view.ErrorCode));
        Assert.NotNull(view.LastLog);

        // A bad pick never starts the setup.
        File.Delete(Path.Combine(home.Files.Scripts, "args.txt"));
        await home.Launcher.RunGameSetupAsync(Path.Combine(home.Rig.Root, "client.toml"), default);
        Assert.Equal((GameSetupState.Failed, "nogame"), (home.Launcher.GameSetup.State, home.Launcher.GameSetup.ErrorCode));
        Assert.Null(home.Files.Arguments);
    }

    [Fact]
    public async Task Cancel_stops_the_setup_and_play_waits_while_one_runs()
    {
        await using var home = await Home.New();
        home.Files.Script("echo step check", "echo step compile", "echo progress 10 397", home.Files.Hangs, "exit /b 1");
        var run = home.Launcher.RunGameSetupAsync(home.Files.Dump, default);
        var child = await home.Files.WaitForChild();
        await Rig.Until("the compile step to show", () => home.Launcher.GameSetup.Step == "compile");

        var view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.Running, "Building the game", true), (view.State, view.StepLabel, view.Busy));
        Assert.Equal(Strings.SetupRunning, home.Launcher.PlayProblem);
        // A second one is not started, and the runtime is not replaced under the running one.
        await home.Launcher.RunGameSetupAsync(home.Files.Dump, default);
        Assert.Equal(GameSetupState.Running, home.Launcher.GameSetup.State);
        await home.Launcher.InstallRuntimeAsync(null, default);
        Assert.Equal(Strings.SetupRunning, home.Launcher.Runtime.Note);

        await home.Launcher.CancelGameSetup();
        await run;

        view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.NeedsSetup, Strings.SetupCancelled), (view.State, view.Message));
        Assert.NotNull(view.LastLog);
        await Rig.Until("the setup's child process to end", () => !SetupRig.Alive(child));
        Assert.Equal(Strings.SetupNotSetUp, home.Launcher.PlayProblem);
    }

    [Fact]
    public async Task The_setup_is_refused_while_playing_and_for_a_game_without_one()
    {
        // The player's own upstream mkw-recomp: nothing for the launcher to set up.
        await using var rig = new Rig(new FakeSeatServer { Mood = SeatMood.QueuedFor, Count = 100_000 });
        Assert.Equal((GameSetupState.Idle, false), (rig.Launcher.GameSetup.State, rig.Launcher.GameSetup.Available));
        await rig.Launcher.RunGameSetupAsync(null, default);
        Assert.Equal((GameSetupState.Failed, Strings.SetupNone), (rig.Launcher.GameSetup.State, rig.Launcher.GameSetup.Message));
        await rig.Launcher.CancelGameSetup();
        Assert.Equal(GameSetupState.Idle, rig.Launcher.GameSetup.State);

        // While a session holds a seat (here: its place in the queue): STOP first.
        await rig.Launcher.PlayAsync();
        await Rig.Until("the session to queue", () => rig.Launcher.Session.Phase == SeatPhase.Queued);
        await rig.Launcher.RunGameSetupAsync(null, default);
        Assert.Equal(Strings.PressStopFirst, rig.Launcher.GameSetup.Message);
        await rig.Launcher.StopAsync();
    }

    /// <summary>A second copy of the game: a loose dump (exefs/main and romfs) beside the rig's .nup.</summary>
    private static string LooseDump(Home home)
    {
        var dump = Path.Combine(home.Files.Root, "loose dump");
        Directory.CreateDirectory(Path.Combine(dump, "exefs"));
        Directory.CreateDirectory(Path.Combine(dump, "romfs"));
        File.WriteAllText(Path.Combine(dump, "exefs", "main"), "NSO0");
        return dump;
    }

    /// <summary>The stand-in's lines of a change of copy: prepare.py redoes exefs, romfs and cleanup, and records the new source and mode.</summary>
    private static string[] Switches(Home home, string dump, string mode)
    {
        File.WriteAllText(Path.Combine(home.Files.Scripts, "switched.json"), SetupRig.StateJson(SetupRig.Pinned, dump, mode));
        return
        [
            "echo step check", "echo version 1.8.0", "echo step exefs", "echo step romfs", "echo skip generate", "echo skip compile",
            "echo skip shaders", "echo skip warm",
            $"copy /y \"{Path.Combine(home.Files.Scripts, "switched.json")}\" \"{Path.Combine(home.Rig.Game, "setup-state.json")}\" >nul",
            "echo step cleanup", "echo done", "exit /b 0",
        ];
    }

    [Fact]
    public async Task Another_copy_of_the_game_runs_the_setup_again_with_it_taken_as_the_last_one_was()
    {
        // Set up from the .nup, its data copied into the install.
        await using var home = await Home.New(arrange: (game, files) =>
        {
            Home.SetUp(game, files);
            File.WriteAllText(Path.Combine(game, "setup-state.json"), SetupRig.StateJson(SetupRig.Pinned, files.Dump, "copy"));
        });
        var view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.Idle, home.Files.Dump, GameDataMode.Copy), (view.State, view.DumpPath, view.Mode));
        Assert.Null(home.Launcher.PlayProblem);
        Assert.Null(home.Launcher.GameCopyChangeProblem(null));

        // The loose dump, no mode said: copied again, as before.
        var loose = LooseDump(home);
        home.Files.Script(Switches(home, loose, "copy"));
        Assert.Null(await home.Launcher.ChangeGameCopyAsync(loose, null, default));

        Assert.Contains($"\"--source\" \"{loose}\" \"--romfs\" \"copy\"", home.Files.Arguments, StringComparison.Ordinal);
        view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.Done, loose, GameDataMode.Copy, (GameSetupReason?)null), (view.State, view.DumpPath, view.Mode, view.Reason));
        Assert.Null(home.Launcher.PlayProblem);

        // Back to the .nup, now read in place: the player's choice wins over the last mode.
        home.Files.Script(Switches(home, home.Files.Dump, "link"));
        Assert.Null(await home.Launcher.ChangeGameCopyAsync(home.Files.Dump, GameDataMode.Link, default));
        Assert.Contains($"\"--source\" \"{home.Files.Dump}\" \"--romfs\" \"link\"", home.Files.Arguments, StringComparison.Ordinal);
        view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.Done, home.Files.Dump, GameDataMode.Link), (view.State, view.DumpPath, view.Mode));
        Assert.Equal(TargetStatus.Ready, home.Launcher.Game.Check!.Status);
    }

    [Fact]
    public async Task A_new_copy_without_a_mode_is_taken_as_the_last_setup_took_its_own()
    {
        await using var home = await Home.New(arrange: (game, files) =>
        {
            Home.SetUp(game, files);
            File.WriteAllText(Path.Combine(game, "setup-state.json"), SetupRig.StateJson(SetupRig.Pinned, files.Dump, "copy"));
        });
        // The Game Setup card's "Choose Another File…" (RunGameSetupAsync with a path) no longer drops a copied install back to linking.
        var loose = LooseDump(home);
        home.Files.Script(Switches(home, loose, "copy"));
        await home.Launcher.RunGameSetupAsync(loose, default);
        Assert.EndsWith("\"--romfs\" \"copy\"", home.Files.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_of_copy_stopped_halfway_is_not_a_ready_game()
    {
        // As prepare.py leaves it when stopped in "generate" after another copy was picked: the old copy's game code
        // (mkwgame.dll, the compile key) beside the new copy's exefs and romfs, and no generate key.
        await using var home = await Home.New(arrange: (game, files) =>
        {
            Home.SetUp(game, files);
            var state = System.Text.Json.Nodes.JsonNode.Parse(SetupRig.StateJson(SetupRig.Pinned, files.Dump))!;
            state["done"]!.AsObject().Remove("generate");
            state["done"]!["compile"] = $"{SetupRig.PinnedId} 1.8.0 windows 0123456789abcdef";
            File.WriteAllText(Path.Combine(game, "setup-state.json"), state.ToJsonString());
        });
        var view = home.Launcher.GameSetup;
        Assert.Equal((GameSetupState.NeedsSetup, GameSetupReason.Unfinished, false), (view.State, view.Reason, view.NeedsDump));
        Assert.Equal(Strings.SetupUnfinished, home.Launcher.PlayProblem);
    }

    [Fact]
    public async Task Changing_the_copy_is_refused_while_the_game_or_a_setup_runs_and_for_a_wrong_pick()
    {
        await using var home = await Home.New(arrange: (game, files) => Home.SetUp(game, files));
        var loose = LooseDump(home);
        home.Files.Script(Switches(home, loose, "link"));

        // The game runs (started from anywhere): close it first. Nothing starts, and the setup's state stays as it was.
        home.Files.Running = true;
        Assert.Equal(Strings.SetupCloseGame, home.Launcher.GameCopyChangeProblem(null));
        Assert.Equal(Strings.SetupCloseGame, await home.Launcher.ChangeGameCopyAsync(loose, GameDataMode.Link, default));
        Assert.Null(home.Files.Arguments);
        Assert.Equal((GameSetupState.Idle, home.Files.Dump), (home.Launcher.GameSetup.State, home.Launcher.GameSetup.DumpPath));
        home.Files.Running = false;

        // Not a copy of the game: the first setup's own check, and again nothing starts.
        var notGame = Path.Combine(home.Rig.Root, "client.toml");
        Assert.Equal(RecompSetup.DumpProblem(notGame), await home.Launcher.ChangeGameCopyAsync(notGame, null, default));
        Assert.Null(home.Files.Arguments);
        Assert.Equal(GameSetupState.Idle, home.Launcher.GameSetup.State);

        // A setup under way: one at a time.
        home.Files.Script("echo step check", "echo step exefs", home.Files.Hangs, "exit /b 1");
        var run = home.Launcher.RunGameSetupAsync(null, default);
        await home.Files.WaitForChild();
        await Rig.Until("the setup to run", () => home.Launcher.GameSetup.Busy);
        Assert.Equal(Strings.SetupAlreadyRunning, await home.Launcher.ChangeGameCopyAsync(loose, null, default));
        await home.Launcher.CancelGameSetup();
        await run;
    }

    [Fact]
    public async Task Changing_the_copy_is_refused_while_play_holds_the_game_and_for_a_game_without_a_setup()
    {
        await using var rig = new Rig(new FakeSeatServer { Mood = SeatMood.QueuedFor, Count = 100_000 });
        Assert.Equal(Strings.SetupNone, await rig.Launcher.ChangeGameCopyAsync(Path.Combine(rig.Root, "x.nup"), null, default));
        Assert.Equal(GameSetupState.Idle, rig.Launcher.GameSetup.State);

        // PLAY holds the game while its session holds a seat (here: its place in the queue, which does not end by
        // itself the way a stand-in game can before the check runs)
        await using var home = await Home.New(arrange: (game, files) => Home.SetUp(game, files), server: new FakeSeatServer { Mood = SeatMood.QueuedFor, Count = 100_000 });
        await home.Launcher.PlayAsync();
        await Rig.Until("the session to queue", () => home.Launcher.Session.Phase == SeatPhase.Queued);
        Assert.Equal(Strings.PressStopFirst, await home.Launcher.ChangeGameCopyAsync(home.Files.Dump, null, default));
        await home.Launcher.StopAsync();
    }

    [Fact]
    public async Task Installing_the_runtime_turns_its_updater_off_and_then_asks_for_the_setup()
    {
        using var files = new RuntimeRig();
        var home = Path.Combine(files.Root, "launcher");
        Directory.CreateDirectory(home);
        var config = Path.Combine(files.Root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { Name = "Ana", Pin = "123456", AutoUpdate = false, AdultConsent = true } }.Save(config);
        await using var launcher = new Launcher(config, home, new LauncherOptions
        {
            Version = "1.4.0",
            Targets = [new MkwRecompTarget("1.4.0") { RunningOverride = () => false }],
            BindLocalApi = false,
            BackgroundLoops = false,
            Runtime = files.Manifest,
            RuntimeFolder = files.Folder,
            RuntimeCache = files.Cache,
            RuntimeFreeSpace = _ => null,
        }, files.Host);
        Assert.Equal((GameSetupState.Idle, false), (launcher.GameSetup.State, launcher.GameSetup.Available)); // no game folder yet

        await launcher.InstallRuntimeAsync(null, default);

        Assert.Equal(RuntimeState.Installed, launcher.Runtime.State);
        // The file the zip shipped is kept, with the two settings the launcher owns added.
        var ini = File.ReadAllText(files.At("mkw.ini"));
        Assert.StartsWith("shipped = 1\n", ini, StringComparison.Ordinal);
        Assert.Contains("[launcher]\nupdate_check = 0\nstart_screen = skip\n", ini, StringComparison.Ordinal);
        Assert.NotNull(launcher.GameSettings.Schema!.Find("update_check")!.Locked);
        // The next thing in the player's way is the setup, with their own game.
        var view = launcher.GameSetup;
        Assert.Equal((GameSetupState.NeedsSetup, GameSetupReason.NotSetUp, true), (view.State, view.Reason, view.NeedsDump));
        Assert.Equal(Strings.SetupNotSetUp, launcher.PlayProblem);
        Assert.Same(RecompRuntime.Pinned.Credit, launcher.Runtime.Credit);
    }

    [Fact]
    public async Task A_target_without_a_setup_needs_none()
    {
        IGameTarget plain = new NoSetupTarget();
        var install = new GameInstall("emu", "C:\\emu", "C:\\emu\\emu.exe", "1.0", null, null);
        Assert.Null(plain.SetupStatus(install));
        Assert.Null(plain.SetupNeeded(install));
        Assert.Empty(plain.ApplyOwnedSettings(install));
        var result = await plain.RunSetupAsync(install, new GameSetupRequest(null), null, default);
        Assert.Equal((false, "none", Strings.SetupNone), (result.Ok, result.ErrorCode, result.Message));
    }

    /// <summary>An emulator-like target: only what the interface requires.</summary>
    private sealed class NoSetupTarget : IGameTarget
    {
        public string Id => "emu";
        public string DisplayName => "Emulator";
        public string FolderHint => "the one with emu.exe";
        public TargetCapabilities Capabilities => TargetCapabilities.None;
        public GameInstall? Detect(string folder) => null;
        public IEnumerable<string> SearchRoots() => [];
        public TargetCheck Check(GameInstall install) => new(TargetStatus.Ready, null, null, null, null);
        public IntegrationReport Inspect(GameInstall install, IntegrationPackage? package) => new(IntegrationState.Installed, "ok", []);
        public Task<IntegrationReport> InstallAsync(GameInstall install, IntegrationPackage package, CancellationToken cancel) => Task.FromResult(Inspect(install, package));
        public Task UninstallAsync(GameInstall install, CancellationToken cancel) => Task.CompletedTask;
        public void WriteSession(GameInstall install, SessionFile session) { }
        public void ForgetTicket(GameInstall install) { }
        public IGameProcess? Launch(GameInstall install, LaunchOptions options) => null;
        public bool IsRunning(GameInstall install) => false;
        public IEnumerable<SupportItem> SupportItems(GameInstall install) => [];
    }
}

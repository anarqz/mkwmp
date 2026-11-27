// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Buffers.Text;
using System.IO.Compression;
using System.Text;
using Mkwmp.Core.Targets.MkwRecomp;
using Mkwmp.Core.Updates;
using Mkwmp.Update;
using Mkwmp.UpdateHelper;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The launcher's own update (launcher/docs/UPDATES.md): the helper's swap and rollback
/// (Mkwmp.Update, the library updater.exe runs), its arguments and strings, and the
/// launcher's side: staging, "ready to install", the hand-over, and never under a running game.
/// GitHub is <see cref="FakeGitHub"/>; no process is started and no window is opened.
/// </summary>
public partial class SelfUpdateTests
{
    private static readonly string[] TestKeys = [FakeGitHub.TestKey];

    private static byte[] Zip(params (string Path, string Text)[] files) => ZipBytes([.. files.Select(f => (f.Path, Encoding.UTF8.GetBytes(f.Text)))]);

    private static byte[] ZipBytes(params (string Path, byte[] Bytes)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                using var entry = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
                entry.Write(bytes);
            }
        }
        return stream.ToArray();
    }

    /// <summary>A launcher folder with a few files, and a release staged in it as the launcher leaves it.</summary>
    private static UpdatePlan Staged(byte[] package, string kind = UpdatePlan.KindZip, string version = "1.5.0", string? root = null, string launcher = "mkwmp.exe")
    {
        if (root is null)
        {
            root = Rig.NewFolder("swap");
            File.WriteAllText(Path.Combine(root, launcher), "launcher 1.4.0");
            File.WriteAllText(Path.Combine(root, "client.toml"), "the player's settings");
            Directory.CreateDirectory(Path.Combine(root, "i18n"));
            Directory.CreateDirectory(Path.Combine(root, "licenses"));
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            File.WriteAllText(Path.Combine(root, "i18n", "en.json"), "{ \"old\": \"1.4.0\" }");
            File.WriteAllText(Path.Combine(root, "licenses", "A.txt"), "unchanged");
            File.WriteAllText(Path.Combine(root, "logs", "launcher-1.log"), "a session");
        }
        var plan = new UpdatePlan
        {
            Root = root,
            Version = version,
            FromVersion = "1.4.0",
            Kind = kind,
            Asset = Updater.ZipAsset(version),
            Launcher = launcher,
            Arguments = ["--tray", "my settings.toml"],
        };
        Directory.CreateDirectory(plan.Transaction);
        File.WriteAllBytes(plan.PackagePath, package);
        var sums = $"# mkwmp {version}\n{Updater.Sha256Hex(package)}  {plan.Asset}\n";
        File.WriteAllText(plan.SumsPath, sums);
        File.WriteAllText(plan.SignaturePath, FakeGitHub.Sign(sums));
        plan.Write();
        return plan;
    }

    private static byte[] Release150() => Zip(
        ("mkwmp/0-new.txt", "new in 1.5.0"),
        ("mkwmp/i18n/en.json", "{ \"new\": \"1.5.0\" }"),
        ("mkwmp/licenses/A.txt", "unchanged"),
        ("mkwmp/mkwmp.exe", "launcher 1.5.0"),
        ("mkwmp/updater.exe", "helper 1.5.0"),
        ("mkwmp/client.toml", "a release must never bring settings"),
        ("mkwmp/logs/x.log", "nor logs"));

    private static UpdateInstaller Installer(List<string>? log = null) =>
        new(ReleaseSignature.Verify, TestKeys, log is null ? null : log.Add) { Patience = TimeSpan.Zero };

    private static string Read(UpdatePlan plan, string path) => File.ReadAllText(Path.Combine(plan.Root, path));

    // ---- the plan and the command line ------------------------------------------------------

    [Fact]
    public void The_plan_round_trips_and_is_only_read_from_its_own_staging_folder()
    {
        var plan = Staged(Release150());
        var read = UpdatePlan.Read(plan.PlanPath);
        Assert.Equal((plan.Root, "1.5.0", "1.4.0", UpdatePlan.KindZip, "mkwmp-1.5.0-windows-x64.zip", "mkwmp.exe"), (read.Root, read.Version, read.FromVersion, read.Kind, read.Asset, read.Launcher));
        Assert.Equal(["--tray", "my settings.toml"], read.Arguments);
        Assert.Equal(Path.Combine(plan.Root, ".mkwmp-selfupdate", "1.5.0", "package.zip"), read.PackagePath);
        Assert.Equal(Path.Combine(plan.Root, "logs", "updater.log"), read.LogPath);

        // A copy of the plan somewhere else cannot point the helper at this folder.
        var elsewhere = Path.Combine(Rig.NewFolder("plan"), "plan.json");
        File.Copy(plan.PlanPath, elsewhere);
        Assert.Contains("not in its launcher's staging folder", Assert.Throws<UpdateException>(() => UpdatePlan.Read(elsewhere)).Message, StringComparison.Ordinal);
        Assert.Throws<UpdateException>(() => UpdatePlan.Read(Path.Combine(plan.Root, "nothing.json")));
        File.WriteAllText(elsewhere, "not json");
        Assert.Throws<UpdateException>(() => UpdatePlan.Read(elsewhere));
        Assert.Throws<UpdateException>(() => (plan with { Version = "..\\..\\x" }).Validated());
        Assert.Throws<UpdateException>(() => (plan with { Kind = "msi" }).Validated());
        Assert.Throws<UpdateException>(() => (plan with { Launcher = "..\\evil.exe" }).Validated());
        Assert.Throws<UpdateException>(() => (plan with { Root = "relative" }).Validated());
        Assert.True(UpdatePlan.ValidVersion("1.5.0-rc.1") && !UpdatePlan.ValidVersion("v1.5.0") && !UpdatePlan.ValidVersion("1/5"));
    }

    [Fact]
    public void The_helpers_arguments_parse_and_bad_ones_are_refused()
    {
        var plan = Staged(Release150());
        var args = UpdaterArgs.Parse(UpdaterArgs.For(plan, 4321, dark: true, "pt-BR", quiet: false), out var error)!;
        Assert.Null(error);
        Assert.Equal((plan.PlanPath, 4321, true, "pt-BR", false, null), (args.Plan, args.Pid, args.Dark, args.Locale, args.Quiet, args.Shot));
        // A locale that is no catalog name never reaches a path.
        Assert.Equal("en", UpdaterArgs.Parse(UpdaterArgs.For(plan, 1, false, "..\\..\\x", quiet: true), out _)!.Locale);
        Assert.True(UpdaterArgs.Parse(["--plan", "p", "--quiet"], out _)!.Quiet);

        var shot = UpdaterArgs.Parse(["--shot", "out.png", "--theme", "dark", "--locale", "pt-BR", "--step", "starting", "--progress", "0.5", "--scale", "2", "--version", "1.5.0"], out _)!;
        Assert.Equal(("out.png", true, UpdateStep.Starting, 0.5, 2.0, "1.5.0"), (shot.Shot, shot.Dark, shot.Step, shot.Progress, shot.Scale, shot.Version));

        foreach (var bad in new string[][] { [], ["--pid", "12"], ["--plan"], ["--plan", "p", "--pid", "x"], ["--plan", "p", "--theme", "pink"], ["--plan", "p", "--locale", "../x"], ["--plan", "p", "--what", "1"], ["stray", "p"], ["--plan", "p", "--step", "9"] })
        {
            Assert.Null(UpdaterArgs.Parse(bad, out error));
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void The_helpers_strings_come_from_the_catalogs_with_built_in_english_and_portuguese()
    {
        var dir = Rig.NewFolder("i18n");
        File.WriteAllText(Path.Combine(dir, "en.json"), "{ \"updater.title\": \"Installing…\", \"home.play\": \"PLAY\" }");
        File.WriteAllText(Path.Combine(dir, "de.json"), "{ \"updater.title\": \"Update wird installiert…\", \"updater.version\": \"Version {version}\" }");
        File.WriteAllText(Path.Combine(dir, "xx.json"), "broken {");
        var german = UpdaterStrings.Load(dir, "de");
        Assert.Equal("Update wird installiert…", german[UpdaterStrings.Title]);
        Assert.Equal("Waiting for MKWMP to close", german[UpdaterStrings.Waiting]); // not translated yet: English
        Assert.Equal("Version 1.5.0", german.Format(UpdaterStrings.Version, "version", "1.5.0"));
        Assert.Equal("Installing…", UpdaterStrings.Load(dir, "en")[UpdaterStrings.Title]);
        Assert.Equal("Installing…", UpdaterStrings.Load(dir, "xx")[UpdaterStrings.Title]);
        // No catalog at all (an install from before the helper): the built-in texts.
        var portuguese = UpdaterStrings.Load(Path.Combine(dir, "missing"), "pt-BR");
        Assert.Equal(("Instalando a atualização…", "Substituindo arquivos"), (portuguese[UpdaterStrings.Title], portuguese.For(UpdateStep.Replacing)));
        Assert.Equal("Installing the update…", UpdaterStrings.Load(null, "ja")[UpdaterStrings.Title]);
        // The catalogs the launcher ships carry every key of the helper.
        var shipped = Path.Combine(AppContext.BaseDirectory, "i18n");
        foreach (var locale in (string[])["en", "pt-BR"])
        {
            var catalog = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(shipped, locale + ".json")))!;
            Assert.All(UpdaterStrings.Keys, key => Assert.True(catalog.ContainsKey(key), $"{locale}.json lacks {key}"));
        }
    }

    // ---- the signature ----------------------------------------------------------------------

    [Fact]
    public void The_helper_and_the_launcher_check_the_same_signature()
    {
        const string Sums = "# mkwmp 1.5.0\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  mkwmp-1.5.0-windows-x64.zip\n";
        var bytes = Encoding.UTF8.GetBytes(Sums);
        var signature = FakeGitHub.Sign(Sums);
        foreach (var verifier in new Ed25519Verifier[] { ReleaseSignature.Verify, SelfUpdate.Verify })
        {
            Assert.Equal(64, ReleaseSums.Verify(bytes, signature, TestKeys, "1.5.0", verifier)["mkwmp-1.5.0-windows-x64.zip"].Length);
            var tampered = (byte[])bytes.Clone();
            tampered[20] ^= 1;
            Assert.Equal("SHA256SUMS signature does not verify", Assert.Throws<UpdateException>(() => ReleaseSums.Verify(tampered, signature, TestKeys, "1.5.0", verifier)).Message);
            Assert.Throws<UpdateException>(() => ReleaseSums.Verify(bytes, signature, ReleaseSums.ReleaseKeys, "1.5.0", verifier)); // only the release key
            Assert.Contains("not 1.5.1", Assert.Throws<UpdateException>(() => ReleaseSums.Verify(bytes, signature, TestKeys, "1.5.1", verifier)).Message, StringComparison.Ordinal);
            Assert.Equal("malformed signature", Assert.Throws<UpdateException>(() => ReleaseSums.Verify(bytes, "garbage", TestKeys, "1.5.0", verifier)).Message);
        }
        // Bit by bit: the two libraries agree on what is a signature.
        var key = Base64Url.DecodeFromChars(FakeGitHub.TestKey.AsSpan(2));
        var raw = Base64Url.DecodeFromChars(signature);
        Assert.True(ReleaseSignature.Verify(key, bytes, raw) && SelfUpdate.Verify(key, bytes, raw));
        for (var i = 0; i < raw.Length; i++)
        {
            var bad = (byte[])raw.Clone();
            bad[i] ^= 0x40;
            Assert.False(ReleaseSignature.Verify(key, bytes, bad));
            Assert.False(SelfUpdate.Verify(key, bytes, bad));
        }
        Assert.False(ReleaseSignature.Verify(key.AsSpan(1), bytes, raw));
        Assert.Equal("release1:Zuvb-AuF9DcXEzXRSu49EFrb2ilCQnJl1e4ApQJI9Dw", Assert.Single(ReleaseSums.ReleaseKeys));
    }

    // ---- the swap ---------------------------------------------------------------------------

    [Fact]
    public void A_release_zip_replaces_every_changed_file_and_nothing_of_the_players()
    {
        var plan = Staged(Release150());
        var log = new List<string>();
        var steps = new List<(UpdateStep, double)>();
        var installer = new UpdateInstaller(ReleaseSignature.Verify, TestKeys, log.Add) { Patience = TimeSpan.Zero, Progress = (step, done) => steps.Add((step, done)) };
        var installed = installer.Apply(plan);

        Assert.Equal(("launcher 1.5.0", "{ \"new\": \"1.5.0\" }", "new in 1.5.0", "helper 1.5.0"), (Read(plan, "mkwmp.exe"), Read(plan, "i18n/en.json"), Read(plan, "0-new.txt"), Read(plan, "updater.exe")));
        // Player data is the player's, whatever a zip holds.
        Assert.Equal(("the player's settings", "a session", false), (Read(plan, "client.toml"), Read(plan, "logs/launcher-1.log"), File.Exists(Path.Combine(plan.Root, "logs", "x.log"))));
        // Only what differs was touched; what was replaced is in the backup.
        Assert.Equal(["0-new.txt", "i18n/en.json", "updater.exe", "mkwmp.exe"], installed.Changed);
        Assert.Equal("launcher 1.4.0", File.ReadAllText(Path.Combine(plan.Transaction, "backup", "mkwmp.exe")));
        Assert.False(File.Exists(Path.Combine(plan.Transaction, "backup", "licenses", "A.txt")));
        Assert.Equal((UpdateStep.Verifying, -1), steps[0]);
        Assert.Equal((UpdateStep.Replacing, 1), steps[^1]);
        Assert.Contains("5 files in the package, 4 changed, 0 dropped", log);

        installed.Commit();
        Assert.False(Directory.Exists(Path.Combine(plan.Transaction, "incoming")));
        Assert.Equal(["0-new.txt", "i18n/en.json", "licenses/A.txt", "mkwmp.exe", "updater.exe"], File.ReadAllLines(Path.Combine(plan.Root, ".mkwmp-selfupdate", "installed.txt")));

        // The next release drops a file: it goes too (to that update's backup), and only that.
        var next = Staged(Zip(("mkwmp/mkwmp.exe", "launcher 1.6.0"), ("mkwmp/i18n/en.json", "{ \"new\": \"1.5.0\" }"), ("mkwmp/updater.exe", "helper 1.5.0")), version: "1.6.0", root: plan.Root);
        var second = Installer().Apply(next);
        Assert.Equal(["0-new.txt", "licenses/A.txt", "mkwmp.exe"], second.Changed);
        Assert.Equal((false, "launcher 1.6.0", "the player's settings"), (File.Exists(Path.Combine(plan.Root, "0-new.txt")), Read(plan, "mkwmp.exe"), Read(plan, "client.toml")));
        second.Rollback();
        Assert.Equal(("new in 1.5.0", "launcher 1.5.0", "unchanged"), (Read(plan, "0-new.txt"), Read(plan, "mkwmp.exe"), Read(plan, "licenses/A.txt")));
    }

    /// <summary>The portable layout (publish.ps1): the exe and its native libraries beside it, which must move together.</summary>
    [Fact]
    public void The_native_libraries_beside_the_exe_are_swapped_with_it_and_rolled_back_with_it()
    {
        static byte[] Portable(string version, params string[] dlls) => Zip([
            .. dlls.Select(d => ($"mkwmp/{d}", $"{d} {version}")),
            ("mkwmp/mkwmp.exe", $"launcher {version}"),
            ("mkwmp/updater.exe", "helper")]);
        string[] wpf = ["wpfgfx_cor3.dll", "PresentationNative_cor3.dll", "D3DCompiler_47_cor3.dll", "libsodium.dll"];

        // From a launcher that was one self-extracting file (nothing beside it): the libraries are added, the exe last.
        var plan = Staged(Portable("1.5.0", wpf));
        var installed = Installer().Apply(plan);
        Assert.Equal([.. wpf, "updater.exe", "mkwmp.exe"], installed.Changed);
        Assert.All(wpf, d => Assert.Equal($"{d} 1.5.0", Read(plan, d)));
        installed.Commit();

        // The next one: something holds one library, so none of it is installed, the exe included.
        var next = Staged(Portable("1.6.0", wpf), version: "1.6.0", root: plan.Root);
        using (new FileStream(Path.Combine(plan.Root, "libsodium.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<UpdateException>(() => Installer().Apply(next));
        }
        Assert.All(wpf, d => Assert.Equal($"{d} 1.5.0", Read(plan, d)));
        Assert.Equal("launcher 1.5.0", Read(plan, "mkwmp.exe"));

        // Free again: all of them, then the exe; a library this release no longer has goes, and comes back on a rollback.
        var later = Staged(Portable("1.7.0", wpf[..3]), version: "1.7.0", root: plan.Root);
        var swapped = Installer().Apply(later);
        Assert.Equal(("launcher 1.7.0", "wpfgfx_cor3.dll 1.7.0", false), (Read(plan, "mkwmp.exe"), Read(plan, "wpfgfx_cor3.dll"), File.Exists(Path.Combine(plan.Root, "libsodium.dll"))));
        Assert.Equal("mkwmp.exe", swapped.Changed[^1]);
        swapped.Rollback();
        Assert.All(wpf, d => Assert.Equal($"{d} 1.5.0", Read(plan, d)));
        Assert.Equal("launcher 1.5.0", Read(plan, "mkwmp.exe"));
    }

    [Fact]
    public void The_launcher_keeps_the_name_it_has()
    {
        // A test build of 1.4.0 from before the rename (mkwmp-launcher.exe): the zip's mkwmp.exe is that file, so the
        // player's shortcut to it keeps working.
        var plan = Staged(Release150(), launcher: "mkwmp-launcher.exe");
        Installer().Apply(plan);
        Assert.Equal(("launcher 1.5.0", false), (Read(plan, "mkwmp-launcher.exe"), File.Exists(Path.Combine(plan.Root, "mkwmp.exe"))));
        // Only a zip is a package.
        Assert.Throws<UpdateException>(() => (plan with { Kind = "exe" }).Validated());
    }

    [Fact]
    public void A_failure_half_way_puts_every_file_back()
    {
        var plan = Staged(Release150());
        var log = new List<string>();
        // Something still holds the helper's file: it cannot be moved aside. By then two files are already swapped.
        File.WriteAllText(Path.Combine(plan.Root, "updater.exe"), "helper 1.4.0");
        using (new FileStream(Path.Combine(plan.Root, "updater.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<UpdateException>(() => Installer(log).Apply(plan));
            Assert.False(error.Incomplete);
        }
        Assert.Equal(("launcher 1.4.0", "{ \"old\": \"1.4.0\" }", "helper 1.4.0", false), (Read(plan, "mkwmp.exe"), Read(plan, "i18n/en.json"), Read(plan, "updater.exe"), File.Exists(Path.Combine(plan.Root, "0-new.txt"))));
        Assert.Contains("the previous version was restored", log);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(plan.Transaction, "backup"), "*", SearchOption.AllDirectories));
        // And it can be tried again.
        Installer().Apply(plan);
        Assert.Equal(("launcher 1.5.0", "helper 1.5.0"), (Read(plan, "mkwmp.exe"), Read(plan, "updater.exe")));
    }

    [Fact]
    public void A_package_that_is_not_the_signed_one_or_reaches_outside_is_refused_untouched()
    {
        // Changed after the launcher verified it.
        var plan = Staged(Release150());
        var bytes = File.ReadAllBytes(plan.PackagePath);
        bytes[^10] ^= 1;
        File.WriteAllBytes(plan.PackagePath, bytes);
        Assert.Equal("mkwmp-1.5.0-windows-x64.zip: checksum mismatch", Assert.Throws<UpdateException>(() => Installer().Apply(plan)).Message);
        Assert.Equal("launcher 1.4.0", Read(plan, "mkwmp.exe"));

        // A list signed by somebody else.
        plan = Staged(Release150());
        Assert.Equal("SHA256SUMS signature does not verify", Assert.Throws<UpdateException>(() => new UpdateInstaller(ReleaseSignature.Verify).Apply(plan)).Message);

        // A signed list of another release, replayed for this one.
        plan = Staged(Release150());
        var other = "# mkwmp 1.4.9\n" + File.ReadAllLines(plan.SumsPath)[1] + "\n";
        File.WriteAllText(plan.SumsPath, other);
        File.WriteAllText(plan.SignaturePath, FakeGitHub.Sign(other));
        Assert.Contains("not 1.5.0", Assert.Throws<UpdateException>(() => Installer().Apply(plan)).Message, StringComparison.Ordinal);

        // Signed, but its paths leave the launcher's folder; or it is not a launcher at all.
        foreach (var (zip, why) in new[]
        {
            (Zip(("mkwmp/mkwmp.exe", "x"), ("mkwmp/../../evil.dll", "x")), "unsafe path"),
            (Zip(("mkwmp.exe", "x"), ("C:/Windows/evil.dll", "x")), "unsafe path"),
            (Zip(("mkwmp/readme.txt", "x")), "no mkwmp.exe"),
            (Zip(("mkwmp/mkwmp.exe", "x"), ("mkwmp/MKWMP.EXE", "y")), "twice"),
        })
        {
            plan = Staged(zip);
            Assert.Contains(why, Assert.Throws<UpdateException>(() => Installer().Apply(plan)).Message, StringComparison.Ordinal);
            Assert.Equal("launcher 1.4.0", Read(plan, "mkwmp.exe"));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(plan.Root)!, "evil.dll")));
        }
        Assert.True(UpdatePackage.IsPlayerData("client.toml") && UpdatePackage.IsPlayerData("logs/a.log") && UpdatePackage.IsPlayerData(".mkwmp-selfupdate/1.0.0/plan.json") && !UpdatePackage.IsPlayerData("game/winmm.dll"));
    }

    // ---- the helper's run -------------------------------------------------------------------

    [Fact]
    public void The_helper_installs_starts_the_new_launcher_and_on_a_failed_start_brings_the_old_one_back()
    {
        var plan = Staged(Release150());
        var log = new List<string>();
        var started = new List<string?>();
        var steps = new List<UpdateStep>();
        var runner = new UpdateRunner(new UpdateInstaller(ReleaseSignature.Verify, TestKeys, log.Add) { Patience = TimeSpan.Zero, Progress = (step, _) => steps.Add(step) }, log.Add)
        {
            StartLauncher = (p, failure) =>
            {
                started.Add(failure is null ? File.ReadAllText(p.LauncherPath) : "failed: " + failure);
                return true;
            },
            Progress = (step, _) => steps.Add(step),
        };
        Assert.Equal(UpdateOutcome.Installed, runner.Run(plan, launcherPid: 0));
        Assert.Equal(["launcher 1.5.0"], started);
        Assert.Equal([UpdateStep.Waiting, UpdateStep.Verifying, UpdateStep.Replacing, UpdateStep.Starting], steps.Distinct());
        Assert.True(File.Exists(Path.Combine(plan.Root, ".mkwmp-selfupdate", "installed.txt")));
        Assert.Contains(log, l => l.StartsWith("installing 1.4.0 -> 1.5.0", StringComparison.Ordinal));

        // The new launcher does not start: everything goes back and the old one is started, told why.
        plan = Staged(Release150());
        started.Clear();
        var shown = new List<(string, UpdateOutcome)>();
        runner = new UpdateRunner(Installer(), log.Add)
        {
            StartLauncher = (p, failure) =>
            {
                started.Add(failure is null ? "new" : $"{File.ReadAllText(p.LauncherPath)}: {failure}");
                return failure is not null;
            },
            Failed = (why, outcome) => shown.Add((why, outcome)),
        };
        Assert.Equal(UpdateOutcome.Kept, runner.Run(plan, 0));
        Assert.Equal(["new", "launcher 1.4.0: the new version did not start"], started);
        Assert.Equal([("the new version did not start", UpdateOutcome.Kept)], shown);
        Assert.Equal(("launcher 1.4.0", "{ \"old\": \"1.4.0\" }", false), (Read(plan, "mkwmp.exe"), Read(plan, "i18n/en.json"), File.Exists(Path.Combine(plan.Root, "0-new.txt"))));

        // A package that does not verify: nothing is touched, the old launcher opens again.
        File.WriteAllText(plan.SignaturePath, FakeGitHub.Sign("something else"));
        started.Clear();
        Assert.Equal(UpdateOutcome.Kept, runner.Run(plan, 0));
        Assert.Equal(["launcher 1.4.0: SHA256SUMS signature does not verify"], started);

        // The launcher never closes (here: this test's own process): nothing is touched, nothing is started.
        started.Clear();
        runner = new UpdateRunner(Installer(), log.Add) { LauncherPatience = TimeSpan.FromMilliseconds(150), StartLauncher = (_, _) => { started.Add("started"); return true; } };
        Assert.Equal(UpdateOutcome.LauncherStillRunning, runner.Run(Staged(Release150()), Environment.ProcessId));
        Assert.Empty(started);
        Assert.True(UpdateInstaller.WaitForExit(0, TimeSpan.Zero) && UpdateInstaller.WaitForExit(int.MaxValue - 7, TimeSpan.Zero));
    }

    // ---- the launcher's side ----------------------------------------------------------------

    private sealed class Game
    {
        public bool Running { get; set; }
    }

    private sealed record Started(string Helper, IReadOnlyList<string> Arguments);

    private static (Launcher Launcher, string Home, string Exe, Game Game, List<Started> Helpers) Launch(FakeGitHub github, string? home = null, string version = "1.4.0", bool headless = false, bool require = false, Func<string, string?>? environment = null)
    {
        _ = Rig.TestExe;
        var root = home is null ? Rig.NewFolder("selfupdate") : Path.GetDirectoryName(home)!;
        home ??= Path.Combine(root, "launcher");
        var game = Path.Combine(root, "mkw");
        var exe = Path.Combine(home, "mkwmp.exe");
        if (!Directory.Exists(home))
        {
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(Path.Combine(home, "i18n"));
            File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
            File.WriteAllText(exe, "launcher 1.4.0");
            File.WriteAllText(Path.Combine(home, "i18n", "en.json"), "{ \"old\": \"1.4.0\" }");
            new ClientConfig { Settings = new LauncherSettings { Game = game, Ui = new UiSettings { Theme = "dark", Locale = "pt-BR" } } }.Save(Path.Combine(home, "client.toml"));
        }
        var running = new Game();
        var helpers = new List<Started>();
        var launcher = new Launcher(Path.Combine(home, "client.toml"), home, new LauncherOptions
        {
            Version = version,
            Executable = exe,
            Arguments = ["--tray"],
            UpdateApi = FakeGitHub.Api,
            ReleaseKeys = [FakeGitHub.TestKey],
            Targets = [new MkwRecompTarget(version) { RunningOverride = () => running.Running }],
            BindLocalApi = false,
            Headless = headless,
            ApplyUpdatesAutomatically = headless,
            RequireUpdates = require,
            UpdateEnvironment = environment,
            Tick = TimeSpan.FromMilliseconds(30),
            Exit = _ => { },
            StartUpdateHelper = (helper, arguments) =>
            {
                helpers.Add(new Started(helper, arguments));
                return true;
            },
        }, github);
        return (launcher, home, exe, running, helpers);
    }

    private static FakeGitHub GitHubWith150(out byte[] zip)
    {
        zip = Release150();
        return new FakeGitHub().Release("1.5.0", new() { [Updater.ZipAsset("1.5.0")] = zip }, notes: "What's new in 1.5.0");
    }

    [Fact]
    public async Task The_release_is_staged_then_ready_to_install_and_the_helper_gets_it_when_the_player_says_so()
    {
        var github = GitHubWith150(out var zip);
        var (launcher, home, exe, _, helpers) = Launch(github);
        var stage = Path.Combine(home, ".mkwmp-selfupdate", "1.5.0");
        var settings = File.ReadAllText(Path.Combine(home, "client.toml"));
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            Assert.Equal((UpdateState.Available, "1.5.0", "What's new in 1.5.0"), (launcher.Update.State, launcher.Update.AvailableVersion, launcher.Update.Notes));
            var states = new List<UpdateView>();
            launcher.Changed += (_, _) =>
            {
                lock (states)
                {
                    states.Add(launcher.Update);
                }
            };

            // Download: into the staging folder, verified; nothing installed changes and nothing restarts.
            await launcher.DownloadUpdateAsync(default);
            Assert.Equal((UpdateState.ReadyToInstall, "1.5.0", "What's new in 1.5.0", Strings.UpdateReady("1.5.0"), false),
                (launcher.Update.State, launcher.Update.AvailableVersion, launcher.Update.Notes, launcher.Update.Status, launcher.Update.GameBlocks));
            Assert.Contains(states, s => s.State == UpdateState.Downloading && s.Total == zip.Length && s.Received == zip.Length && s.Progress == 1);
            Assert.Equal("launcher 1.4.0", File.ReadAllText(exe));
            Assert.Empty(helpers);
            Assert.Equal(zip, File.ReadAllBytes(Path.Combine(stage, "package.zip")));
            Assert.True(File.Exists(Path.Combine(stage, "SHA256SUMS.sig")));
            // The helper that will run is the new release's own.
            Assert.Equal("helper 1.5.0", File.ReadAllText(Path.Combine(stage, "worker.exe")));
            // Asking again downloads nothing.
            var before = github.Downloads;
            await launcher.DownloadUpdateAsync(default);
            await launcher.CheckForUpdatesAsync();
            Assert.Equal(UpdateState.ReadyToInstall, launcher.Update.State);
            Assert.DoesNotContain(github.Requests.Skip(before), r => r.Name.EndsWith(".zip", StringComparison.Ordinal));

            // "Install and Restart": the helper is started with the plan, this launcher's pid, theme and language.
            await launcher.ApplyUpdateAsync(default);
            Assert.Equal((UpdateState.Restarting, Strings.UpdateInstalling("1.5.0")), (launcher.Update.State, launcher.Update.Status));
            var started = Assert.Single(helpers);
            Assert.Equal(Path.Combine(stage, "worker.exe"), started.Helper);
            var args = UpdaterArgs.Parse(started.Arguments, out _)!;
            Assert.Equal((Path.Combine(stage, "plan.json"), Environment.ProcessId, true, "pt-BR", false), (args.Plan, args.Pid, args.Dark, args.Locale, args.Quiet));
            Assert.Equal("launcher 1.4.0", File.ReadAllText(exe)); // the launcher itself swaps nothing

            // What the helper then does with that plan (its own signature check, the test key in place of the release key).
            var plan = UpdatePlan.Read(args.Plan!);
            Assert.Equal((home, "1.5.0", "1.4.0", "mkwmp.exe"), (plan.Root, plan.Version, plan.FromVersion, plan.Launcher));
            Assert.Equal(["--tray"], plan.Arguments);
            settings = File.ReadAllText(Path.Combine(home, "client.toml"));
            var outcome = new UpdateRunner(Installer(), _ => { }) { StartLauncher = (_, _) => true }.Run(plan, 0);
            Assert.Equal((UpdateOutcome.Installed, "launcher 1.5.0", "helper 1.5.0"), (outcome, File.ReadAllText(exe), File.ReadAllText(Path.Combine(home, "updater.exe"))));
            Assert.Equal(settings, File.ReadAllText(Path.Combine(home, "client.toml")));
        }

        // The new launcher: nothing more to install, and what the update left is cleared.
        var (updated, _, _, _, _) = Launch(github, home, version: "1.5.0");
        await using (updated)
        {
            await updated.StartAsync(default);
            await updated.StartupUpdates;
            Assert.Equal(UpdateState.UpToDate, updated.Update.State);
            Assert.False(Directory.Exists(stage));
            Assert.True(File.Exists(Path.Combine(home, ".mkwmp-selfupdate", "installed.txt")));
        }
    }

    [Fact]
    public async Task A_download_that_was_cut_is_continued_and_a_staged_one_survives_a_restart_even_offline()
    {
        var big = new byte[400_000];
        new Random(7).NextBytes(big);
        var zip = ZipBytes(("mkwmp/mkwmp.exe", big), ("mkwmp/updater.exe", "helper 1.5.0"u8.ToArray()));
        var name = Updater.ZipAsset("1.5.0");
        var github = new FakeGitHub().Release("1.5.0", new() { [name] = zip });
        var (launcher, home, _, _, _) = Launch(github);
        var stage = Path.Combine(home, ".mkwmp-selfupdate", "1.5.0");
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            github.CutOnce = (name, 150_000);
            await launcher.DownloadUpdateAsync(default);
            Assert.Equal(UpdateState.Failed, launcher.Update.State);
            Assert.Contains("the connection was closed", launcher.Update.Status, StringComparison.Ordinal);
            Assert.Equal(150_000, new FileInfo(Path.Combine(stage, "package.zip.part")).Length);
            Assert.False(File.Exists(Path.Combine(stage, "package.zip")));

            // "Try again" asks only for the rest.
            await launcher.DownloadUpdateAsync(default);
            Assert.Equal(UpdateState.ReadyToInstall, launcher.Update.State);
            Assert.Equal([(name, null), (name, 150_000L)], github.Requests.Where(r => r.Name == name));
            Assert.Equal(zip, File.ReadAllBytes(Path.Combine(stage, "package.zip")));
            Assert.False(File.Exists(Path.Combine(stage, "package.zip.part")));
        }

        // Closed with "Later", opened again: still ready, nothing downloaded twice.
        var (again, _, _, _, _) = Launch(github, home);
        await using (again)
        {
            await again.StartAsync(default);
            await again.StartupUpdates;
            Assert.Equal((UpdateState.ReadyToInstall, "1.5.0"), (again.Update.State, again.Update.AvailableVersion));
            Assert.Equal(2, github.Requests.Count(r => r.Name == name));
        }

        // Without GitHub the verified download can still be installed.
        github.Down = true;
        var (offline, _, _, _, helpers) = Launch(github, home);
        await using (offline)
        {
            await offline.StartAsync(default);
            await offline.StartupUpdates;
            Assert.Equal((UpdateState.ReadyToInstall, "1.5.0"), (offline.Update.State, offline.Update.AvailableVersion));
            await offline.ApplyUpdateAsync(default);
            Assert.Equal(UpdateState.Restarting, offline.Update.State);
            Assert.Single(helpers);
        }

        // A staged package that was changed on disk is not "ready": it is offered for download again.
        github.Down = false;
        var bytes = File.ReadAllBytes(Path.Combine(stage, "package.zip"));
        bytes[1000] ^= 1;
        File.WriteAllBytes(Path.Combine(stage, "package.zip"), bytes);
        var (tampered, _, _, _, _) = Launch(github, home);
        await using (tampered)
        {
            await tampered.StartAsync(default);
            await tampered.StartupUpdates;
            Assert.Equal(UpdateState.Available, tampered.Update.State);
            await tampered.DownloadUpdateAsync(default);
            Assert.Equal(UpdateState.ReadyToInstall, tampered.Update.State);
            Assert.Equal(zip, File.ReadAllBytes(Path.Combine(stage, "package.zip")));
        }
    }

    [Fact]
    public async Task Nothing_is_installed_while_a_game_runs_and_after_the_game_it_is_when_asked()
    {
        var github = GitHubWith150(out _);
        var (launcher, _, exe, game, helpers) = Launch(github);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            game.Running = true;
            // The download goes on behind the game; installing is refused.
            await launcher.ApplyUpdateAsync(default);
            Assert.Equal((UpdateState.ReadyToInstall, true, false, Strings.UpdateNeedsNoGame), (launcher.Update.State, launcher.Update.GameBlocks, launcher.Update.AfterGame, launcher.Update.Status));
            Assert.Empty(helpers);
            Assert.Equal("launcher 1.4.0", File.ReadAllText(exe));

            // "Install After the Game", taken back, and asked for again.
            launcher.InstallUpdateAfterGame(true);
            Assert.Equal((UpdateState.ReadyToInstall, true, Strings.UpdateAfterGame), (launcher.Update.State, launcher.Update.AfterGame, launcher.Update.Status));
            launcher.InstallUpdateAfterGame(false);
            Assert.Equal((false, true, Strings.UpdateNeedsNoGame), (launcher.Update.AfterGame, launcher.Update.GameBlocks, launcher.Update.Status));
            await Task.Delay(200);
            Assert.Empty(helpers);
            launcher.InstallUpdateAfterGame(true);
            await Task.Delay(200);
            Assert.Empty(helpers); // the game still runs

            game.Running = false;
            await Rig.Until("the update to be handed to the helper", () => helpers.Count == 1 && launcher.Update.State == UpdateState.Restarting);
        }
    }

    // ── a real launcher: the update is required (UPDATES.md, section 1) ──

    [Fact]
    public async Task A_newer_release_is_required_downloaded_by_itself_and_nothing_is_played_before_it_is_installed()
    {
        var github = GitHubWith150(out var zip);
        var (launcher, home, exe, _, helpers) = Launch(github, require: true);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            // Nobody asked: it is there, verified, and required.
            Assert.Equal((UpdateState.ReadyToInstall, "1.5.0", true, false), (launcher.Update.State, launcher.Update.AvailableVersion, launcher.Update.Required, launcher.Update.GameBlocks));
            Assert.Equal(zip, File.ReadAllBytes(Path.Combine(home, ".mkwmp-selfupdate", "1.5.0", "package.zip")));
            Assert.Empty(helpers); // installing is the window's step ("Install and Restart", or its countdown)
            Assert.Equal("launcher 1.4.0", File.ReadAllText(exe));

            // PLAY is refused, and the update cannot be put off.
            await launcher.PlayAsync();
            Assert.False(launcher.Session.Busy);
            launcher.ContinueWithoutUpdate(); // only after a failure
            Assert.True(launcher.Update.Required);

            await launcher.ApplyUpdateAsync(default);
            Assert.Equal(UpdateState.Restarting, launcher.Update.State);
            Assert.Single(helpers);
        }
    }

    [Fact]
    public async Task Without_GitHub_nothing_is_required_and_the_launcher_plays_as_it_is()
    {
        var github = GitHubWith150(out _);
        github.Down = true;
        var (launcher, _, _, _, helpers) = Launch(github, require: true);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            Assert.Equal((UpdateState.UpToDate, false), (launcher.Update.State, launcher.Update.Required));
            Assert.Empty(helpers);
        }
    }

    [Fact]
    public async Task With_a_game_running_the_required_update_waits_for_it_and_then_installs_by_itself()
    {
        var github = GitHubWith150(out _);
        var (launcher, _, exe, game, helpers) = Launch(github, require: true);
        game.Running = true;
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            // Downloaded behind the game; armed for after it (the window does not block: GameBlocks).
            Assert.Equal((UpdateState.ReadyToInstall, true, true, true, Strings.UpdateAfterGame),
                (launcher.Update.State, launcher.Update.Required, launcher.Update.GameBlocks, launcher.Update.AfterGame, launcher.Update.Status));
            launcher.InstallUpdateAfterGame(false); // not something a required update lets go of
            Assert.True(launcher.Update.AfterGame);
            await Task.Delay(200);
            Assert.Empty(helpers); // never under a game
            Assert.Equal("launcher 1.4.0", File.ReadAllText(exe));

            game.Running = false;
            await Rig.Until("the update to be handed to the helper", () => helpers.Count == 1 && launcher.Update.State == UpdateState.Restarting);
        }
    }

    [Fact]
    public async Task A_required_update_that_fails_says_so_and_lets_the_player_go_on_and_a_rolled_back_one_does_not_loop()
    {
        var big = new byte[300_000];
        new Random(3).NextBytes(big);
        var name = Updater.ZipAsset("1.5.0");
        var github = new FakeGitHub().Release("1.5.0", new() { [name] = ZipBytes(("mkwmp/mkwmp.exe", big), ("mkwmp/updater.exe", "helper 1.5.0"u8.ToArray())) });
        var (launcher, home, _, _, helpers) = Launch(github, require: true);
        await using (launcher)
        {
            // The download breaks: failed, still required (the card: Try Again, Continue Without Updating, Quit).
            github.CutOnce = (name, 100_000);
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            Assert.Equal((UpdateState.Failed, true), (launcher.Update.State, launcher.Update.Required));
            await launcher.PlayAsync();
            Assert.False(launcher.Session.Busy);

            // "Continue Without Updating": this run goes on as it is.
            launcher.ContinueWithoutUpdate();
            Assert.Equal((UpdateState.Failed, false), (launcher.Update.State, launcher.Update.Required));

            // "Try Again" (also later, from Settings): the rest is downloaded; no longer forced in this run.
            await launcher.CheckForUpdatesAsync();
            Assert.Equal((UpdateState.ReadyToInstall, false), (launcher.Update.State, launcher.Update.Required));
            Assert.Empty(helpers);
        }

        // The helper could not install it and started the previous launcher again: it says why, once, and neither
        // checks nor installs by itself (no loop), until the player says Try Again.
        var before = github.Requests.Count;
        var (back, _, _, _, again) = Launch(github, home, require: true,
            environment: variable => variable == UpdateRunner.UpdatedVariable ? "1" : variable == UpdateRunner.FailedVariable ? "mkwmp.exe is in use" : null);
        await using (back)
        {
            await back.StartAsync(default);
            await back.StartupUpdates;
            Assert.Equal((UpdateState.Failed, true, Strings.UpdateNotInstalled("mkwmp.exe is in use")), (back.Update.State, back.Update.Required, back.Update.Status));
            Assert.Equal(before, github.Requests.Count);
            Assert.Empty(again);
            back.ContinueWithoutUpdate();
            Assert.False(back.Update.Required);
        }

        var (retry, _, _, _, third) = Launch(github, home, require: true,
            environment: variable => variable == UpdateRunner.UpdatedVariable ? "1" : variable == UpdateRunner.FailedVariable ? "mkwmp.exe is in use" : null);
        await using (retry)
        {
            await retry.StartAsync(default);
            await retry.StartupUpdates;
            await retry.CheckForUpdatesAsync(); // Try Again: the kept download is ready at once, and required
            Assert.Equal((UpdateState.ReadyToInstall, true), (retry.Update.State, retry.Update.Required));
            await retry.ApplyUpdateAsync(default);
            Assert.Single(third);
        }
    }

    [Fact]
    public async Task Headless_updates_by_itself_in_place_from_the_zip()
    {
        var github = GitHubWith150(out _);
        var (launcher, home, exe, _, helpers) = Launch(github, headless: true);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            // No window to ask and no helper window to show: verified, swapped by this process, restarted (the test's Exit).
            Assert.Equal((UpdateState.Restarting, Strings.UpdatedRestarting), (launcher.Update.State, launcher.Update.Status));
            Assert.Empty(helpers);
            Assert.Equal(("launcher 1.5.0", "helper 1.5.0", "{ \"new\": \"1.5.0\" }"), (File.ReadAllText(exe), File.ReadAllText(Path.Combine(home, "updater.exe")), File.ReadAllText(Path.Combine(home, "i18n", "en.json"))));
            Assert.Equal("launcher 1.4.0", File.ReadAllText(Path.Combine(home, ".mkwmp-selfupdate", "1.5.0", "backup", "mkwmp.exe")));
        }
    }

    /// <summary>
    /// The way from the Rust launcher (1.3.x), by hand: the player unzipped 1.4.0 into the 1.3.x folder, so mkwmp.exe stands
    /// beside the Rust launcher's mkwmp-launcher.exe (README.txt says to delete it), with game/ of 1.4.0 and the Rust
    /// launcher's client.toml.
    /// </summary>
    [Fact]
    public async Task A_1_3_folder_the_new_zip_was_unzipped_into_keeps_the_player_s_settings_and_updates_from_the_zip()
    {
        _ = Rig.TestExe;
        var root = Rig.NewFolder("from-rust");
        var home = Path.Combine(root, "mkwmp");
        var game = Path.Combine(root, "mkw-recomp");
        Directory.CreateDirectory(Path.Combine(home, "game"));
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
        var exe = Path.Combine(home, "mkwmp.exe");
        File.WriteAllText(exe, "launcher 1.4.0");
        File.WriteAllText(Path.Combine(home, "updater.exe"), "helper 1.4.0");
        var rustExe = Path.Combine(home, "mkwmp-launcher.exe");
        File.WriteAllText(rustExe, "the rust launcher 1.3.1");
        File.WriteAllText(Path.Combine(home, "LICENSE"), "GPL");
        File.WriteAllText(Path.Combine(home, "game", "SHA256SUMS"), "# mkwmp 1.4.0\n" + new string('a', 64) + "  mkwmp-mod.zip\n");
        // As the Rust launcher's ClientConfig::save writes it (rust/client/src/config.rs).
        var rust = string.Join('\n',
            "# mkwmp launcher settings (edited by the launcher window).", "# Guide: https://anarqz.github.io/mkwmp/", "",
            "server = \"https://mkw.example.net\"", "name = \"Ana_1\"", "pin = \"482913\"", $"game = {ClientConfig.Quote(game)}", "launch = true",
            "seat = \"online\"", "local_port = 47821", "update = true", "slot = 1", "launch_command = \"\"", "patch = true", "adult_consent = true", "",
            "[background]", "close_to_tray = false", "start_with_system = true", "notify_online = true", "notify_playing = false", "notify_requests = true",
            "tray_hint_shown = true", "", "[voice]", "enabled = true", "input = \"{0.0.1.00000000}.{mic}\"", "output = \"\"", "input_gain = 70",
            "output_gain = 90", "inputs = []", "outputs = []", "", "[[voice.mutes]]", "account = \"123\"", "name = \"Bia\"", "");
        File.WriteAllText(Path.Combine(home, "client.toml"), rust);
        var zip = Zip(
            ("mkwmp/mkwmp.exe", "launcher 1.4.1"),
            ("mkwmp/updater.exe", "helper 1.4.1"),
            ("mkwmp/wpfgfx_cor3.dll", "native 1.4.1"),
            ("mkwmp/i18n/en.json", "{ \"home.play\": \"Play\" }"),
            ("mkwmp/licenses/Nunito-LICENSE.txt", "OFL"),
            ("mkwmp/game/SHA256SUMS", "# mkwmp 1.4.1\n"));
        var github = new FakeGitHub().Release("1.4.1", new() { [Updater.ZipAsset("1.4.1")] = zip });

        var (launcher, _, _, _, helpers) = Launch(github, home);
        await using (launcher)
        {
            // Nothing to fill in again: a registered player with the consent is past the welcome.
            var s = launcher.Settings;
            Assert.Equal(("https://mkw.example.net", "Ana_1", "482913", game, true, true), (s.Server, s.Name, s.Pin, s.Game, s.AdultConsent, s.Ui.SetupDone));
            Assert.Equal((SeatMode.Online, 47821, true, false, true, false), (s.Seat, s.LocalPort, s.AutoUpdate, s.Background.CloseToTray, s.Background.StartWithSystem, s.Background.NotifyPlaying));
            Assert.Equal(("{0.0.1.00000000}.{mic}", 70, 90), (s.Voice.Input, s.Voice.InputGain, s.Voice.OutputGain));
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            Assert.Equal((UpdateState.Available, "1.4.1"), (launcher.Update.State, launcher.Update.AvailableVersion));

            // The next update is the zip, installed by the helper that comes out of it.
            await launcher.DownloadUpdateAsync(default);
            await launcher.ApplyUpdateAsync(default);
            var started = Assert.Single(helpers);
            Assert.Equal("helper 1.4.1", File.ReadAllText(started.Helper));
            var plan = UpdatePlan.Read(UpdaterArgs.Parse(started.Arguments, out _)!.Plan!);
            Assert.Equal((UpdatePlan.KindZip, "1.4.0", "mkwmp.exe"), (plan.Kind, plan.FromVersion, plan.Launcher));
            var settings = File.ReadAllText(Path.Combine(home, "client.toml"));
            Assert.Equal(UpdateOutcome.Installed, new UpdateRunner(Installer(), _ => { }) { StartLauncher = (_, _) => true }.Run(plan, 0));
            Assert.Equal(("launcher 1.4.1", "helper 1.4.1", "native 1.4.1", "{ \"home.play\": \"Play\" }", "GPL"),
                (File.ReadAllText(exe), File.ReadAllText(Path.Combine(home, "updater.exe")), File.ReadAllText(Path.Combine(home, "wpfgfx_cor3.dll")),
                 File.ReadAllText(Path.Combine(home, "i18n", "en.json")), File.ReadAllText(Path.Combine(home, "LICENSE"))));
            Assert.Equal(settings, File.ReadAllText(Path.Combine(home, "client.toml")));
            // No release brought the Rust launcher's file: it is left as it is (the player deletes it).
            Assert.Equal("the rust launcher 1.3.1", File.ReadAllText(rustExe));
        }

        // The file the C# launcher writes still holds what the Rust launcher reads (name, PIN, game), should the player go back.
        var after = ClientConfig.Load(Path.Combine(home, "client.toml"));
        Assert.Equal(("Ana_1", "482913", game), (after.Settings.Name, after.Settings.Pin, after.Settings.Game));
        Assert.Equal([new MutedPlayer("123", "Bia")], after.Mutes);
    }
}

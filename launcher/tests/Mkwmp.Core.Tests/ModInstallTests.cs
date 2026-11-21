// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;
using Mkwmp.Core;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core.Tests;

/// <summary>On an mkw-recomp that loads mods the integration is the MKWMP mod: installed from the launcher's package, turned on in mkw.ini, the older game patch taken out.</summary>
public sealed class ModInstallTests
{
    private const string V = "1.4.0";
    private static readonly string[] TestKeys = [FakeGitHub.TestKey];

    private static bool EnvironmentDecides => Environment.GetEnvironmentVariable("MKW_MODS") is not null || Probe.DevProbe
        || Environment.GetEnvironmentVariable(Launcher.DevPackageEnv) is not null;

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var to = new StreamWriter(zip.CreateEntry(name).Open());
                to.Write(text);
            }
        }
        return memory.ToArray();
    }

    private static byte[] ModZip(string version, string dll, string voice = "voice 1") => Zip(
        ("mkwmp/mod.json", $"{{ \"title\": \"MKWMP online play\", \"version\": \"{version}\", \"has_plugin\": true }}"),
        ("mkwmp/plugin/mkwmp.dll", dll),
        ("mkwmp/mkwmp-voice.dll", voice));

    /// <summary>A package holding the zip, as the core hands it over once verified.</summary>
    private static IntegrationPackage Package(byte[] zip, bool development = false)
    {
        var path = Path.Combine(Rig.NewFolder("modpkg"), Mod.Zip);
        File.WriteAllBytes(path, zip);
        return new IntegrationPackage(V, new Dictionary<string, string> { [Mod.Zip] = path }, new Dictionary<string, string> { [Mod.Zip] = Updater.Sha256Hex(zip) }) { Development = development };
    }

    private static string ModGame(string name, string? ini = null)
    {
        _ = Rig.TestExe;
        var d = Rig.NewFolder(name);
        File.WriteAllBytes(Path.Combine(d, "mkw.exe"), Rig.TestModExe);
        if (ini is not null)
        {
            File.WriteAllText(Path.Combine(d, "mkw.ini"), ini);
        }
        return d;
    }

    private static string Ini(string game) => File.ReadAllText(Path.Combine(game, "mkw.ini"));

    private static string ModFile(string game, string name) => Path.Combine(game, "mods", "mkwmp", name);

    [Fact]
    public async Task The_mod_is_installed_updated_repaired_and_uninstalled_and_only_its_own_things_change()
    {
        if (EnvironmentDecides)
        {
            return;
        }
        var d = ModGame("mod-life", "# mine\r\n[display]\r\nfullscreen = 1\r\n\r\n[launcher]\r\nmods = Other,Second\r\nmods_played = Other\r\nmods_ack = 1\r\n");
        var settings = Path.Combine(d, "profile", "mod-settings", "mkwmp.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "camera=1\n");
        Directory.CreateDirectory(Path.Combine(d, "mods", "Other"));
        File.WriteAllText(Path.Combine(d, "mods", "Other", "mod.json"), "{}");
        var notes = new List<string>();
        var running = false;
        var target = new MkwRecompTarget(V, notes.Add) { RunningOverride = () => running };
        var install = target.Detect(d)!;
        var asset = Assert.Single(target.PackageAssetsFor(install));
        Assert.Equal((Mod.Zip, true, Mod.Unpacked), (asset.Name, asset.Required, asset.Unpacked));
        var one = Package(ModZip("1.4.0", "dll 1"));
        var missing = target.Inspect(install, one);
        Assert.Equal((IntegrationState.Missing, false, "Not installed: press INSTALL"), (missing.State, missing.Ok, missing.Summary));
        Assert.True(target.NeedsPackage(install));

        // never while the game runs
        running = true;
        Assert.Equal(Strings.RecompCloseForPatch, (await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(install, one, default))).Message);
        Assert.False(Directory.Exists(Path.Combine(d, "mods", "mkwmp")));
        running = false;

        var report = await target.InstallAsync(install, one, default);
        Assert.Equal((IntegrationState.Installed, true, "MKWMP mod installed (v1.4.0)"), (report.State, report.Ok, report.Summary));
        Assert.Contains("files: 3 of 3 verified against v1.4.0", report.Lines);
        Assert.Contains("mkw.ini [launcher] mods: enabled", report.Lines);
        Assert.Equal("dll 1", File.ReadAllText(ModFile(d, "plugin/mkwmp.dll")));
        Assert.Equal("voice 1", File.ReadAllText(ModFile(d, "mkwmp-voice.dll")));
        // appended, as the game's own Mods page turns a mod on; every other line and the line endings as they were
        Assert.Equal("# mine\r\n[display]\r\nfullscreen = 1\r\n\r\n[launcher]\r\nmods = Other,Second,mkwmp\r\nmods_played = Other\r\nmods_ack = 1\r\n", Ini(d));
        Assert.Contains(notes, n => n.StartsWith("mod: installed the MKWMP mod v1.4.0", StringComparison.Ordinal) && n.Contains("3 of 3 files written", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(d, "mods", "mkwmp"), Recomp.ActiveMod(d));
        Assert.False(target.NeedsPackage(install)); // the mod runs: offline, PLAY needs no package
        Assert.Equal((IntegrationState.Mod, true), (target.Inspect(install, null).State, target.Inspect(install, null).Ok));

        // what the mod keeps in its own folder stays through an update, and only changed files are written
        File.WriteAllText(ModFile(d, "mkwmp.log"), "the mod's own");
        var voiceWritten = File.GetLastWriteTimeUtc(ModFile(d, "mkwmp-voice.dll"));
        var two = Package(ModZip("1.4.1", "dll 2"));
        Assert.Equal((IntegrationState.Outdated, false), (target.Inspect(install, two).State, target.Inspect(install, two).Ok));
        notes.Clear();
        running = true;
        await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(install, two, default));
        Assert.Equal("dll 1", File.ReadAllText(ModFile(d, "plugin/mkwmp.dll")));
        running = false;
        report = await target.InstallAsync(install, two, default);
        Assert.Equal((IntegrationState.Installed, "MKWMP mod installed (v1.4.1)"), (report.State, report.Summary));
        Assert.Contains(notes, n => n.Contains("2 of 3 files written", StringComparison.Ordinal));
        Assert.Equal("dll 2", File.ReadAllText(ModFile(d, "plugin/mkwmp.dll")));
        Assert.Equal(voiceWritten, File.GetLastWriteTimeUtc(ModFile(d, "mkwmp-voice.dll")));
        Assert.Equal("the mod's own", File.ReadAllText(ModFile(d, "mkwmp.log")));
        Assert.False(Directory.Exists(ModFile(d, ".mkwmp-new")) || Directory.Exists(ModFile(d, ".mkwmp-backup")));
        Assert.Contains("mods = Other,Second,mkwmp", Ini(d), StringComparison.Ordinal);

        // nothing to do: also fine while the game runs
        running = true;
        notes.Clear();
        Assert.Equal(IntegrationState.Installed, (await target.InstallAsync(install, two, default)).State);
        Assert.DoesNotContain(notes, n => n.StartsWith("mod:", StringComparison.Ordinal));
        running = false;

        // damaged: a file changed, a file gone
        File.WriteAllText(ModFile(d, "mkwmp-voice.dll"), "changed by hand!");
        var damaged = target.Inspect(install, two);
        Assert.Equal((IntegrationState.Damaged, false, "Modified or damaged: press INSTALL to repair"), (damaged.State, damaged.Ok, damaged.Summary));
        Assert.Contains("mkwmp-voice.dll: another version, or changed", damaged.Lines);
        File.Delete(ModFile(d, "plugin/mkwmp.dll"));
        Assert.Contains("plugin/mkwmp.dll: missing", target.Inspect(install, two).Lines);
        Assert.Equal(IntegrationState.Damaged, target.Inspect(install, null).State); // no library: damaged whatever there is to compare with
        Assert.Equal(IntegrationState.Installed, (await target.InstallAsync(install, two, default)).State);

        // turned off in mkw-recomp's Mods page: the files are fine, the game would not load them
        File.WriteAllText(Path.Combine(d, "mkw.ini"), Ini(d).Replace("mods = Other,Second,mkwmp", "mods = Other,Second", StringComparison.Ordinal));
        var off = target.Inspect(install, two);
        Assert.Equal((IntegrationState.Disabled, false, Strings.PatchState("mod_disabled")), (off.State, off.Ok, off.Summary));
        Assert.Contains("mkw.ini [launcher] mods: not enabled", off.Lines);
        notes.Clear();
        Assert.Equal(IntegrationState.Installed, (await target.InstallAsync(install, two, default)).State);
        Assert.Single(notes, n => n.StartsWith("mod:", StringComparison.Ordinal)); // only mkw.ini was written
        Assert.Contains("mods = Other,Second,mkwmp", Ini(d), StringComparison.Ordinal);

        // uninstall: the mod's folder and its name in the list; the other mods and the player's settings of the mod stay
        File.WriteAllText(Path.Combine(d, "mkwmp.toml"), "x");
        running = true;
        Assert.Equal("Close the game first", (await Assert.ThrowsAsync<LauncherException>(() => target.UninstallAsync(install, default))).Message);
        Assert.True(File.Exists(ModFile(d, "mod.json")));
        running = false;
        await target.UninstallAsync(install, default);
        Assert.False(Directory.Exists(Path.Combine(d, "mods", "mkwmp")));
        Assert.True(File.Exists(Path.Combine(d, "mods", "Other", "mod.json")));
        Assert.Equal("# mine\r\n[display]\r\nfullscreen = 1\r\n\r\n[launcher]\r\nmods = Other,Second\r\nmods_played = Other\r\nmods_ack = 1\r\n", Ini(d));
        Assert.Equal("camera=1\n", File.ReadAllText(settings));
        Assert.False(File.Exists(Path.Combine(d, "mkwmp.toml")));
        Assert.Equal(IntegrationState.Missing, target.Inspect(install, two).State);
        await target.UninstallAsync(install, default); // nothing there: nothing to do
    }

    [Fact]
    public void Mkw_ini_gets_the_mod_the_way_the_game_writes_its_list()
    {
        // no file yet (a fresh mkw-recomp that never ran): the section and the key
        Assert.Equal("[launcher]\nmods = mkwmp\n", Mod.WithMod("", on: true));
        Assert.Equal("[display]\nfullscreen = 1\n\n[launcher]\nmods = mkwmp\n", Mod.WithMod("[display]\nfullscreen = 1\n", on: true));
        // into the section the file has, beside what is there
        Assert.Equal("[launcher]\nupdate_check = 0\nmods = mkwmp\n", Mod.WithMod("[launcher]\nupdate_check = 0\n", on: true));
        Assert.Equal("[launcher]\nmods = A,mkwmp\n", Mod.WithMod("[launcher]\nmods = \"A\"\n", on: true));
        Assert.Equal("[launcher]\nmods = A,B,mkwmp\n", Mod.WithMod("[launcher]\nmods = A, B\n", on: true));
        // already there, in any case and anywhere in the list: not a byte changes
        foreach (var same in new[] { "[launcher]\nmods = mkwmp\n", "[launcher]\nmods = MKWMP, A\n", "[launcher]\r\nmods=A,mkwmp" })
        {
            Assert.Same(same, Mod.WithMod(same, on: true));
        }
        // a list outside [launcher] (the game reads the key anywhere, the mod only there): it moves
        var moved = Mod.WithMod("[display]\nmods = A\n", on: true);
        Assert.Equal("A,mkwmp", Recomp.ModsIn(moved));
        Assert.Equal(["A", "mkwmp"], Mod.Listed(moved));
        // off: its name leaves the list; the last mod out takes the line with it
        Assert.Equal("[launcher]\nmods = A,B\n", Mod.WithMod("[launcher]\nmods = A,mkwmp,B\n", on: false));
        Assert.Equal("[launcher]\nupdate_check = 0\n", Mod.WithMod("[launcher]\nmods = mkwmp\nupdate_check = 0\n", on: false));
        Assert.Same("", Mod.WithMod("", on: false));
        Assert.Equal("1.2.3", Mod.VersionIn("{ \"version\": \"1.2.3\" }"u8));
        Assert.Null(Mod.VersionIn("not json"u8));
    }

    [Fact]
    public async Task A_package_that_does_not_verify_installs_nothing_and_a_failure_half_way_puts_every_file_back()
    {
        if (EnvironmentDecides)
        {
            return;
        }
        var d = ModGame("mod-bad");
        var target = new MkwRecompTarget(V) { RunningOverride = () => false };
        var install = target.Detect(d)!;

        // the zip changed on disk after its hash was verified
        var swapped = Package(ModZip("1.4.0", "dll"));
        File.WriteAllBytes(swapped.Files[Mod.Zip], ModZip("1.4.0", "malware"));
        Assert.Equal("mkwmp-mod.zip: checksum mismatch", (await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(install, swapped, default))).Message);
        // a zip that is not the mod, or reaches outside its folder
        foreach (var bad in new[]
        {
            Zip(("other/mod.json", "{}"), ("other/plugin/mkwmp.dll", "x")),
            Zip(("mkwmp/mod.json", "{}"), ("mkwmp/plugin/mkwmp.dll", "x"), ("mkwmp/../../winmm.dll", "x")),
            Zip(("mkwmp/mod.json", "{}"), ("mkwmp/plugin/mkwmp.dll", "x"), ("mkwmp/C:/x.dll", "x")),
            Zip(("mkwmp/mod.json", "{}")),
            Zip(("mkwmp/plugin/mkwmp.dll", "x")),
            "not a zip"u8.ToArray(),
        })
        {
            await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(install, Package(bad), default));
        }
        Assert.False(Directory.Exists(Path.Combine(d, "mods")));
        Assert.False(File.Exists(Path.Combine(d, "mkw.ini")));
        Assert.Equal(IntegrationState.Missing, target.Inspect(install, swapped).State);
        // nothing to install the mod from
        Assert.Equal("the release lists no MKWMP mod", (await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(install, IntegrationPackage.Empty(V), default))).Message);

        // an update that cannot finish: the first file was replaced, the second cannot be
        await target.InstallAsync(install, Package(ModZip("1.4.0", "dll 1")), default);
        File.Delete(ModFile(d, "plugin/mkwmp.dll"));
        Directory.CreateDirectory(ModFile(d, "plugin/mkwmp.dll")); // a folder where the file must go
        var error = await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(install, Package(ModZip("1.4.1", "dll 2")), default));
        Assert.StartsWith("installing the MKWMP mod failed: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"1.4.0\"", File.ReadAllText(ModFile(d, "mod.json")), StringComparison.Ordinal); // the old one is back
        Assert.False(Directory.Exists(ModFile(d, ".mkwmp-new")) || Directory.Exists(ModFile(d, ".mkwmp-backup")));
    }

    [Fact]
    public async Task The_game_patch_of_an_older_install_gives_way_to_the_mod()
    {
        if (EnvironmentDecides)
        {
            return;
        }
        var d = ModGame("mod-switch");
        File.WriteAllText(Path.Combine(d, Probe.ProbeDll), "someone else's dll");
        Probe.InstallProbe(d, "MZ mkwmp probe 1.3.0"u8, false, out _);
        Probe.InstallFile(d, Probe.Voice, "voice"u8, false);
        File.WriteAllBytes(Path.Combine(d, Probe.Host), Rig.FakeWinmm());
        File.WriteAllText(Path.Combine(d, "mkwmp.toml"), "server = \"x\"\n");
        var notes = new List<string>();
        var target = new MkwRecompTarget(V, notes.Add) { RunningOverride = () => false };
        var install = target.Detect(d)!;
        var package = Package(ModZip("1.4.0", "dll"));

        // until the mod is there the patch still works: PLAY is not blocked, and the report says what INSTALL does
        var before = target.Inspect(install, package);
        Assert.True(before.Ok);
        Assert.Contains(before.Lines, l => l.Contains("loads MKWMP as a mod", StringComparison.Ordinal));

        var report = await target.InstallAsync(install, package, default);
        Assert.Equal((IntegrationState.Installed, "MKWMP mod installed (v1.4.0)"), (report.State, report.Summary));
        Assert.DoesNotContain(report.Lines, l => l.Contains("still beside mkw.exe", StringComparison.Ordinal));
        Assert.Equal("someone else's dll", File.ReadAllText(Path.Combine(d, Probe.ProbeDll))); // the player's own winmm.dll is back
        Assert.False(File.Exists(Path.Combine(d, Probe.Voice)) || File.Exists(Path.Combine(d, Probe.Host)) || File.Exists(Path.Combine(d, Probe.Marker)));
        Assert.True(File.Exists(Path.Combine(d, "mkwmp.toml"))); // the mod reads the launcher's mkwmp.toml
        Assert.Equal("[launcher]\nmods = mkwmp\n", Ini(d));
        Assert.Contains(notes, n => n.Contains("the mod carries it", StringComparison.Ordinal));

        // an older runtime keeps the game patch: nothing of the mod is asked for or installed there
        var old = Rig.NewFolder("mod-old");
        File.WriteAllBytes(Path.Combine(old, "mkw.exe"), Rig.TestExe);
        var oldInstall = target.Detect(old)!;
        Assert.Equal(["winmm.dll", "mkwmp-voice.dll"], target.PackageAssetsFor(oldInstall).Select(a => a.Name));
        Assert.Equal("the release lists no game patch", (await Assert.ThrowsAsync<LauncherException>(() => target.InstallAsync(oldInstall, package, default))).Message);
        Assert.False(Directory.Exists(Path.Combine(old, "mods")));
    }

    [Fact]
    public void The_bundled_mod_is_a_signed_zip_or_its_signed_files_and_an_unsigned_one_only_when_turned_on()
    {
        var assets = new[] { new PackageAsset(Mod.Zip, true, "MKWMP mod", Mod.Unpacked) };
        var zip = ModZip("1.4.0", "dll");

        // (a) the zip, in the signed list
        var d = Rig.NewFolder("bundle-zip");
        File.WriteAllBytes(Path.Combine(d, Mod.Zip), zip);
        var list = $"# mkwmp 1.4.0\n{Updater.Sha256Hex(zip)}  {Mod.Zip}\n";
        File.WriteAllText(Path.Combine(d, "SHA256SUMS"), list);
        Assert.Null(Launcher.Bundled(d, V, TestKeys, assets)); // no signature
        Assert.Null(Launcher.Bundled(d, V, TestKeys, assets, allowUnsigned: true)); // and not marked a development package
        File.WriteAllText(Path.Combine(d, "SHA256SUMS.sig"), FakeGitHub.Sign(list));
        var signed = Launcher.Bundled(d, V, TestKeys, assets)!;
        Assert.False(signed.Development);
        Assert.Equal(3, Mod.Load(signed)!.Count);
        Assert.Null(Launcher.Bundled(d, "1.4.1", TestKeys, assets));

        // the same files unpacked, each in the signed list
        var u = Rig.NewFolder("bundle-unpacked");
        var files = new (string Name, string Text)[] { ("mods/mkwmp/mod.json", "{ \"version\": \"1.4.0\" }"), ("mods/mkwmp/plugin/mkwmp.dll", "dll"), ("mods/mkwmp/mkwmp-camera.dll", "cam") };
        list = "# mkwmp 1.4.0\n";
        foreach (var (name, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(u, name))!);
            File.WriteAllText(Path.Combine(u, name), text);
            list += $"{Updater.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text))}  {name}\n";
        }
        File.WriteAllText(Path.Combine(u, "SHA256SUMS"), list);
        File.WriteAllText(Path.Combine(u, "SHA256SUMS.sig"), FakeGitHub.Sign(list));
        var unpacked = Launcher.Bundled(u, V, TestKeys, assets)!;
        Assert.Equal(["mkwmp-camera.dll", "mod.json", "plugin/mkwmp.dll"], Mod.Load(unpacked)!.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal("1.4.0", Mod.Expected(unpacked)!.Value.Version);
        File.WriteAllText(Path.Combine(u, "mods/mkwmp/plugin/mkwmp.dll"), "changed");
        Assert.Null(Launcher.Bundled(u, V, TestKeys, assets)); // every listed file must match

        // a development package: an unsigned list and the marker; used only when the player turned it on
        var dev = Rig.NewFolder("bundle-dev");
        File.WriteAllBytes(Path.Combine(dev, Mod.Zip), zip);
        File.WriteAllText(Path.Combine(dev, "SHA256SUMS"), $"# mkwmp 1.4.0\n{Updater.Sha256Hex(zip)}  {Mod.Zip}\n");
        File.WriteAllText(Path.Combine(dev, Launcher.DevPackageMarker), "unsigned\n");
        Assert.Null(Launcher.Bundled(dev, V, TestKeys, assets));
        Assert.Null(Launcher.Bundled(dev, V, Updater.ReleaseKeys, assets));
        var taken = Launcher.Bundled(dev, V, TestKeys, assets, allowUnsigned: true)!;
        Assert.True(taken.Development);
        Assert.Null(Launcher.Bundled(dev, "1.4.1", TestKeys, assets, allowUnsigned: true)); // still this version's only
        File.WriteAllBytes(Path.Combine(dev, Mod.Zip), ModZip("1.4.0", "other"));
        Assert.Null(Launcher.Bundled(dev, V, TestKeys, assets, allowUnsigned: true)); // and its files must match its list
        File.WriteAllBytes(Path.Combine(dev, Mod.Zip), zip);
        // a folder with a signature is always held to it, switch or not
        File.WriteAllText(Path.Combine(dev, "SHA256SUMS.sig"), FakeGitHub.Sign("# mkwmp 1.4.0\nsomething else\n"));
        Assert.Null(Launcher.Bundled(dev, V, TestKeys, assets, allowUnsigned: true));

        // the switch is a key a developer writes by hand: read, kept, never added
        Assert.False(ClientConfig.Parse("").Settings.DevPackage);
        Assert.DoesNotContain("dev_package", ClientConfig.Parse("").ToToml(), StringComparison.Ordinal);
        var on = ClientConfig.Parse("dev_package = true\n");
        Assert.True(on.Settings.DevPackage);
        Assert.True(ClientConfig.Parse(on.ToToml()).Settings.DevPackage);
    }

    private static (Launcher Launcher, string Home, string Game) Rigged(HttpMessageHandler github, bool updates, bool devPackage = false)
    {
        var root = Rig.NewFolder("mod-launcher");
        var game = Path.Combine(root, "mkw");
        Directory.CreateDirectory(game);
        _ = Rig.TestExe;
        File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestModExe);
        var home = Path.Combine(root, "launcher");
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { Game = game, AutoUpdate = updates, DevPackage = devPackage } }.Save(path);
        var launcher = new Launcher(path, home, new LauncherOptions
        {
            Version = V,
            UpdateApi = FakeGitHub.Api,
            ReleaseKeys = [FakeGitHub.TestKey],
            Targets = [new MkwRecompTarget(V) { RunningOverride = () => false }],
            BindLocalApi = false,
            BackgroundLoops = false,
            Headless = true,
            Exit = _ => { },
        }, github);
        return (launcher, home, game);
    }

    [Fact]
    public async Task A_new_player_gets_the_mod_from_game_beside_the_launcher_and_nothing_is_downloaded()
    {
        if (EnvironmentDecides)
        {
            return;
        }
        var zip = ModZip("1.4.0", "dll");
        // A release that still had the files loose: they are never fetched, the release zip's game/ is the package.
        var github = new FakeGitHub().Release(V, new() { [Mod.Zip] = zip, ["winmm.dll"] = "MZ mkwmp probe 1.4.0"u8.ToArray() });
        var (launcher, home, game) = Rigged(github, updates: true);
        var probe = "MZ mkwmp probe 1.4.0"u8.ToArray();
        Directory.CreateDirectory(Path.Combine(home, "game"));
        File.WriteAllBytes(Path.Combine(home, "game", Mod.Zip), zip);
        File.WriteAllBytes(Path.Combine(home, "game", "winmm.dll"), probe);
        var list = $"# mkwmp {V}\n{Updater.Sha256Hex(probe)}  winmm.dll\n{Updater.Sha256Hex(zip)}  {Mod.Zip}\n";
        File.WriteAllText(Path.Combine(home, "game", "SHA256SUMS"), list);
        File.WriteAllText(Path.Combine(home, "game", "SHA256SUMS.sig"), FakeGitHub.Sign(list));
        await using (launcher)
        {
            Assert.Equal(IntegrationState.Missing, launcher.Game.Integration!.State);
            Assert.NotNull(launcher.PlayProblem);
            await launcher.InstallIntegrationAsync();
            Assert.Equal(("MKWMP mod installed.", false), (launcher.Game.Note, launcher.Game.NoteIsError));
            Assert.Equal((IntegrationState.Installed, "MKWMP mod installed (v1.4.0)"), (launcher.Game.Integration.State, launcher.Game.Integration.Summary));
            Assert.Null(launcher.PlayProblem);
            Assert.Equal("dll", File.ReadAllText(ModFile(game, "plugin/mkwmp.dll")));
            Assert.Equal("[launcher]\nmods = mkwmp\n", Ini(game));
            Assert.False(File.Exists(Path.Combine(game, "winmm.dll"))); // the mod only: no game patch on this runtime
            Assert.Equal(0, github.Downloads);
            var before = github.Downloads;
            await launcher.SyncIntegrationAsync(force: false);
            Assert.Equal(before, github.Downloads);
            await launcher.UninstallIntegrationAsync();
            Assert.Equal((IntegrationState.Missing, false), (launcher.Game.Integration.State, Directory.Exists(Path.Combine(game, "mods", "mkwmp"))));
        }
    }

    [Fact]
    public async Task A_development_package_is_used_only_when_turned_on_and_says_what_it_is()
    {
        if (EnvironmentDecides)
        {
            return;
        }
        void Dev(string home)
        {
            var folder = Path.Combine(home, "game");
            Directory.CreateDirectory(folder);
            var zip = ModZip("1.4.0-dev", "local build");
            File.WriteAllBytes(Path.Combine(folder, Mod.Zip), zip);
            File.WriteAllText(Path.Combine(folder, "SHA256SUMS"), $"# mkwmp {V}\n{Updater.Sha256Hex(zip)}  {Mod.Zip}\n");
            File.WriteAllText(Path.Combine(folder, Launcher.DevPackageMarker), "unsigned\n");
        }

        // a release build's default: the folder counts for nothing, and the error says why
        var (off, offHome, offGame) = Rigged(new FakeGitHub(), updates: false);
        await using (off)
        {
            Dev(offHome);
            await off.InstallIntegrationAsync();
            Assert.True(off.Game.NoteIsError);
            Assert.Contains("MKWMP_DEV_PACKAGE=1", off.Game.Note, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(offGame, "mods")));
            Assert.Equal(IntegrationState.Missing, off.Game.Integration!.State);
        }
        // with updates on, the release is asked first; without one the same hint
        var (online, onlineHome, onlineGame) = Rigged(new FakeGitHub(), updates: true);
        await using (online)
        {
            Dev(onlineHome);
            await online.InstallIntegrationAsync();
            Assert.Contains("MKWMP_DEV_PACKAGE=1", online.Game.Note, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(onlineGame, "mods")));
        }

        // dev_package = true in client.toml
        var (on, onHome, onGame) = Rigged(new FakeGitHub(), updates: false, devPackage: true);
        await using (on)
        {
            Dev(onHome);
            await on.InstallIntegrationAsync();
            Assert.False(on.Game.NoteIsError);
            var report = on.Game.Integration!;
            Assert.Equal((IntegrationState.Installed, true, "MKWMP mod installed (v1.4.0-dev): development package (unsigned)"), (report.State, report.Ok, report.Summary));
            Assert.Contains(report.Lines, l => l.StartsWith("development package (unsigned)", StringComparison.Ordinal));
            Assert.Equal("local build", File.ReadAllText(ModFile(onGame, "plugin/mkwmp.dll")));
            Assert.Null(on.PlayProblem);
        }
    }

    [Fact]
    public void The_launchers_camera_test_looks_where_the_mod_keeps_its_libraries()
    {
        // mkwmp-camera.dll sits in mods/mkwmp/ (beside mod.json), not in plugin/: the camera test missed it.
        var game = Rig.NewFolder("camera-folders");
        var install = new GameInstall("mkw-recomp", game, Path.Combine(game, "mkw.exe"), "0.1.16-mkwmp", "1.8.0", null);
        var folders = new MkwRecompTarget("1.4.0").NativeLibraryFolders(install).ToList();
        Assert.Equal(Path.Combine(game, "mods", "mkwmp"), folders[0]);
        Assert.Contains(Path.Combine(game, "mods", "mkwmp", "plugin"), folders);
        Assert.Equal(game, folders[^1]);
    }
}
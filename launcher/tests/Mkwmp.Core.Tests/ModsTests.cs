// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mkwmp.Core.Modding;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

#pragma warning disable CA5351 // MD5 is GameBanana's own checksum of a download: the tests make the one it lists

namespace Mkwmp.Core.Tests;

/// <summary>
/// The game's mods (launcher/docs/MODS.md): the list from <c>mods/</c> and mkw.ini, on and off, an
/// install from an archive (what is refused, what the game then reads), a mod's declared options and
/// its settings file, GameBanana against recorded answers, and a download from a link. No network.
/// </summary>
public sealed class ModsTests : IDisposable
{
    private readonly string _root = Rig.NewFolder("mods");
    private readonly string _game;
    private readonly IGameTarget _target;
    private readonly GameInstall _install;
    private bool _running;

    public ModsTests()
    {
        _ = Rig.TestModExe; // registers the stand-in executables
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(_game, "romfs", "Layout"));
        File.WriteAllBytes(Path.Combine(_game, "mkw.exe"), Rig.TestModExe);
        File.WriteAllText(Path.Combine(_game, "romfs", "Layout", "Title.bin"), "the game's own title");
        _target = new MkwRecompTarget("1.4.0") { RunningOverride = () => _running };
        _install = _target.Detect(_game) ?? throw new InvalidOperationException("the test game is not an install");
    }

    public void Dispose()
    {
        ModImport.LimitBytes = ModImport.MaxBytes;
        ModImport.LimitFiles = ModImport.MaxFiles;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a temp folder
        }
    }

    private string Mods => Path.Combine(_game, "mods");

    private string Ini => File.Exists(Path.Combine(_game, "mkw.ini")) ? File.ReadAllText(Path.Combine(_game, "mkw.ini")) : "";

    private string Zip(string name, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in files)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
        return path;
    }

    private ModInstallResult Install(string path, Func<ModInstallRequest, ModInstallRequest>? edit = null) =>
        _target.InstallMod(_install, edit?.Invoke(new ModInstallRequest(path)) ?? new ModInstallRequest(path), null, CancellationToken.None);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void Folder(string id, string? modJson = null, bool plugin = false, string? options = null)
    {
        var dir = Path.Combine(Mods, id);
        Directory.CreateDirectory(dir);
        if (modJson is not null)
        {
            File.WriteAllText(Path.Combine(dir, "mod.json"), modJson);
        }
        if (plugin)
        {
            Directory.CreateDirectory(Path.Combine(dir, "plugin"));
            File.WriteAllText(Path.Combine(dir, "plugin", id + ".dll"), "code");
        }
        if (options is not null)
        {
            File.WriteAllText(Path.Combine(dir, "options.json"), options);
        }
    }

    // ---- the list ---------------------------------------------------------------------------

    [Fact]
    public void The_list_is_the_folders_of_mods_with_what_their_mod_json_says()
    {
        Folder("Timer", """{"name": "Timer", "title": "Race Timer", "version": "1.2", "author": "Ana", "description": "A timer.", "has_romfs": true, "romfs_files": 3, "warnings": ["shaders"]}""");
        Folder("broken", "{ not json");
        Folder("native", null, plugin: true);
        Folder("Classic", """{"title": "Classic GP", "switches": {"CLASSICGP": "1"}}""");
        Folder("mkwmp", """{"title": "MKWMP online play", "version": "1.4.0", "has_plugin": true}""", plugin: true, options: """{"options": [{"key": "voice", "type": "toggle"}]}""");
        Directory.CreateDirectory(Path.Combine(Mods, ".merged"));
        File.WriteAllText(Path.Combine(_game, "mkw.ini"), "[launcher]\nmods = Timer,mkwmp\n");

        var mods = _target.Mods(_install, null);

        Assert.Equal(["Classic", "Timer", "broken", "mkwmp", "native"], mods.Select(m => m.Id));
        var timer = mods.Single(m => m.Id == "Timer");
        Assert.Equal(("Race Timer", "1.2", "Ana", "A timer.", true, true, 3), (timer.Name, timer.Version, timer.Author, timer.Description, timer.Enabled, timer.HasFiles, timer.FileCount));
        Assert.Contains(Strings.ModsWarnShaders, timer.Problems);
        Assert.False(timer.HasPlugin);
        Assert.Null(timer.Locked);
        Assert.Equal("broken", mods.Single(m => m.Id == "broken").Name);
        Assert.True(mods.Single(m => m.Id == "native").HasPlugin); // a mod copied in by hand still shows its plugin
        var classic = mods.Single(m => m.Id == "Classic");
        Assert.True(classic.ChangesCode);
        Assert.Equal(Strings.ModsChangesCode, classic.Locked);
        // MKWMP's own mod: listed, locked, and with no options here (its settings are the launcher's own pages)
        var own = mods.Single(m => m.Id == "mkwmp");
        Assert.True(own.Managed && own.Enabled);
        Assert.Equal(Strings.ModsManaged, own.Locked);
        Assert.Null(own.Options);
    }

    [Fact]
    public void Enabled_mods_that_ship_the_same_game_file_say_so()
    {
        foreach (var id in new[] { "A", "B" })
        {
            Directory.CreateDirectory(Path.Combine(Mods, id, "romfs"));
            File.WriteAllText(Path.Combine(Mods, id, "romfs", "manifest.tsv"), "Layout/Title.bin\t1\tx\ty\nOnly/" + id + "\t1\tx\t-\n");
        }
        File.WriteAllText(Path.Combine(_game, "mkw.ini"), "[launcher]\nmods = A,B\n");
        var mods = _target.Mods(_install, null);
        Assert.Contains(Strings.ModsShares(1, "B"), mods.Single(m => m.Id == "A").Problems);
        Assert.Contains(Strings.ModsShares(1, "A"), mods.Single(m => m.Id == "B").Problems);
    }

    // ---- on and off -------------------------------------------------------------------------

    [Fact]
    public void Turning_a_mod_on_appends_it_and_off_takes_only_it_out()
    {
        const string ini = "# mine\n[video]\nfps = 60\n[launcher]\nmods = a,b\nmods_ack = 1\n";
        var on = Mod.WithMod(ini, "c", on: true);
        Assert.Contains("mods = a,b,c\n", on, StringComparison.Ordinal);
        Assert.Equal(ini, Mod.WithMod(on, "c", on: false));
        Assert.Contains("mods = b\n", Mod.WithMod(ini, "A", on: false), StringComparison.Ordinal); // names compare in any case, as Windows opens the folder
        Assert.DoesNotContain("mods =", Mod.WithMod(Mod.WithMod(ini, "a", false), "b", false), StringComparison.Ordinal);
        Assert.Contains("mods_ack = 1", Mod.WithMod(Mod.WithMod(ini, "a", false), "b", false), StringComparison.Ordinal);
        Assert.Equal("[launcher]\nmods = x\n", Mod.WithMod("", "x", on: true).Replace("\r", "", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mod_is_turned_on_and_off_in_mkw_ini_and_a_stale_merge_goes()
    {
        Folder("Skin", """{"title": "Skin", "has_romfs": true}""");
        Directory.CreateDirectory(Path.Combine(Mods, ".merged", "romfs"));
        File.WriteAllText(Path.Combine(_game, "mkw.ini"), "[launcher]\nmods = mkwmp\n");

        _target.SetModEnabled(_install, "Skin", on: true, acceptCode: false);
        Assert.Contains("mods = mkwmp,Skin", Ini, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Mods, ".merged"))); // made for another set of mods
        Assert.True(_target.Mods(_install, null).Single(m => m.Id == "Skin").Enabled);

        _target.SetModEnabled(_install, "Skin", on: false, acceptCode: false);
        Assert.Contains("mods = mkwmp\n", Ini.Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void What_is_refused_the_game_running_code_without_a_yes_and_mkwmp_s_own_mod()
    {
        Folder("native", null, plugin: true);
        Folder("mkwmp", null, plugin: true);
        Folder("Classic", """{"switches": {"CLASSICGP": "1"}}""");
        File.WriteAllText(Path.Combine(_game, "mkw.ini"), "[launcher]\nmods = mkwmp\n");

        Assert.Equal(Strings.ModsRunsCode, Assert.Throws<LauncherException>(() => _target.SetModEnabled(_install, "native", true, acceptCode: false)).Message);
        _target.SetModEnabled(_install, "native", true, acceptCode: true);
        Assert.Contains("mods = mkwmp,native", Ini, StringComparison.Ordinal);

        Assert.Equal(Strings.ModsManaged, Assert.Throws<LauncherException>(() => _target.SetModEnabled(_install, "mkwmp", false, true)).Message);
        Assert.Equal(Strings.ModsManaged, Assert.Throws<LauncherException>(() => _target.UninstallMod(_install, "mkwmp")).Message);
        Assert.Equal(Strings.ModsChangesCode, Assert.Throws<LauncherException>(() => _target.SetModEnabled(_install, "Classic", true, true)).Message);
        Assert.Throws<LauncherException>(() => _target.SetModEnabled(_install, "..", true, true));
        Assert.Throws<LauncherException>(() => _target.UninstallMod(_install, "nothing"));

        _running = true;
        Assert.Equal(Strings.ModsCloseGame, Assert.Throws<LauncherException>(() => _target.SetModEnabled(_install, "native", false, true)).Message);
        Assert.Equal(Strings.ModsCloseGame, Assert.Throws<LauncherException>(() => _target.UninstallMod(_install, "native")).Message);
        Assert.Equal(ModInstallCode.GameRunning, Install(Zip("r.zip", ("romfs/a", "a"))).Code);
        _running = false;

        _target.UninstallMod(_install, "native");
        Assert.False(Directory.Exists(Path.Combine(Mods, "native")));
        Assert.Contains("mods = mkwmp\n", Ini.Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(Mods, "mkwmp")));
    }

    // ---- installing -------------------------------------------------------------------------

    [Fact]
    public void An_archive_with_the_mod_under_folders_lands_as_mods_id_with_what_the_game_reads()
    {
        var zip = Zip("Cool Skin v2.zip",
            ("Cool Skin/readme.txt", "hi"),
            ("Cool Skin/RomFS/Layout/Title.bin", "a new title"),
            ("Cool Skin/RomFS/Layout/Extra.bin", "new file"),
            ("__MACOSX/Cool Skin/._x", "junk"));

        var result = Install(zip);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(("Cool Skin", false, true), (result.ModId, result.HasPlugin, result.Enabled));
        var dir = Path.Combine(Mods, "Cool Skin");
        Assert.True(File.Exists(Path.Combine(dir, "romfs", "Layout", "Title.bin"))); // RomFS became romfs: the name the game reads
        Assert.Equal(["romfs"], Directory.GetDirectories(dir).Select(Path.GetFileName));
        var manifest = File.ReadAllText(Path.Combine(dir, "romfs", "manifest.tsv"));
        Assert.Equal(
            $"Layout/Extra.bin\t8\t{Sha("new file")}\t-\nLayout/Title.bin\t11\t{Sha("a new title")}\t{Sha("the game's own title")}\n",
            manifest);
        var mod = _target.Mods(_install, null).Single();
        Assert.Equal(("Cool Skin", "Cool Skin", true, 2, true, false), (mod.Id, mod.Name, mod.HasFiles, mod.FileCount, mod.Enabled, mod.ChangesCode));
        Assert.Contains("\"has_romfs\": true", File.ReadAllText(Path.Combine(dir, "mod.json")), StringComparison.Ordinal);
        Assert.Contains("mods = Cool Skin", Ini, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Mods, RecompMods.Staging)));
    }

    [Fact]
    public void A_game_file_in_another_letter_case_is_a_new_file_as_the_game_sees_it()
    {
        // the game compares names byte for byte: "layout/title.bin" is not its "Layout/Title.bin", whatever Windows says
        var result = Install(Zip("case.zip", ("romfs/layout/title.bin", "x")));
        Assert.True(result.Ok, result.Message);
        Assert.EndsWith("\t-\n", File.ReadAllText(Path.Combine(Mods, "case", "romfs", "manifest.tsv")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mod_s_own_mod_json_keeps_its_title_version_and_author_and_the_source_is_remembered()
    {
        var zip = Zip("x.zip", ("Horns/mod.json", """{"title": "Better Horns", "version": "2.0", "author": "Bia", "description": "Horns."}"""), ("Horns/romfs/Sound/h.bin", "h"));
        var result = Install(zip, r => r with { Source = new ModSource("https://gamebanana.com/mods/77", 77, 900, "2.0", 1700000000) });
        Assert.True(result.Ok, result.Message);
        var mod = _target.Mods(_install, null).Single();
        Assert.Equal(("Horns", "Better Horns", "2.0", "Bia", "Horns."), (mod.Id, mod.Name, mod.Version, mod.Author, mod.Description));
        Assert.Equal(new ModSource("https://gamebanana.com/mods/77", 77, 900, "2.0", 1700000000), mod.Source);
    }

    [Fact]
    public void A_mod_with_a_plugin_installs_off_until_the_player_said_yes()
    {
        var zip = Zip("native.zip", ("Plugin/thing.dll", "MZ"), ("options.json", """{"options": [{"key": "speed", "type": "range", "min": 1, "max": 9, "default": 3}]}"""));
        var result = Install(zip);
        Assert.True(result.Ok, result.Message);
        Assert.Equal((true, false), (result.HasPlugin, result.Enabled));
        Assert.DoesNotContain("native", Ini, StringComparison.Ordinal);
        var mod = _target.Mods(_install, null).Single();
        Assert.True(mod.HasPlugin && mod.HasOptions && !mod.Enabled);

        var again = Install(zip, r => r with { Replace = true, AcceptCode = true });
        Assert.True(again.Ok && again.Enabled, again.Message);
        Assert.Contains("mods = native", Ini, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("romfs/../../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("romfs/a.txt:stream")]
    [InlineData("romfs/NUL.txt")]
    [InlineData("romfs/trailing./x")]
    public void A_name_that_leaves_the_folder_or_bends_on_Windows_refuses_the_whole_archive(string name)
    {
        var result = Install(Zip("bad.zip", ("romfs/ok.bin", "ok"), (name, "evil")));
        Assert.Equal(ModInstallCode.Unsafe, result.Code);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_game, "evil.txt")));
        Assert.Empty(Directory.Exists(Mods) ? Directory.GetDirectories(Mods) : []);
    }

    [Fact]
    public void A_link_inside_an_archive_and_a_name_twice_are_refused()
    {
        var path = Path.Combine(_root, "link.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("romfs/a").ExternalAttributes = unchecked((int)0xA1FF0000); // S_IFLNK
        }
        Assert.Equal(ModInstallCode.Unsafe, Install(path).Code);
        Assert.Equal(ModInstallCode.Unsafe, Install(Zip("twice.zip", ("romfs/A.bin", "1"), ("romfs/a.bin", "2"))).Code);
    }

    // ---- .7z and .rar (SharpCompress: no tar.exe, so the same under Wine/Proton) ----------

    private string SevenZip(string name, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(_root, name);
        using var stream = File.Create(path);
        using var writer = new SharpCompress.Writers.SevenZip.SevenZipWriter(stream, new SharpCompress.Writers.SevenZip.SevenZipWriterOptions(SharpCompress.Common.CompressionType.LZMA));
        foreach (var (entry, text) in files)
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(text));
            writer.Write(entry, data, new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));
        }
        return path;
    }

    /// <summary>A committed archive (tests/…/Fixtures/mods: rar is only made by WinRAR), copied so the install's cleanup cannot touch the original.</summary>
    private string Fixture(string name)
    {
        var path = Path.Combine(_root, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mods", name), path);
        return path;
    }

    [Fact]
    public void A_7z_installs_like_a_zip()
    {
        var result = Install(SevenZip("Cool Skin.7z",
            ("Cool Skin/RomFS/Layout/Title.bin", "a new title"),
            ("Cool Skin/RomFS/Layout/Extra.bin", "new file"),
            ("__MACOSX/Cool Skin/._x", "junk")));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(("Cool Skin", true), (result.ModId, result.Enabled));
        Assert.Equal(
            $"Layout/Extra.bin\t8\t{Sha("new file")}\t-\nLayout/Title.bin\t11\t{Sha("a new title")}\t{Sha("the game's own title")}\n",
            File.ReadAllText(Path.Combine(Mods, "Cool Skin", "romfs", "manifest.tsv")));
        Assert.Equal(["romfs"], Directory.GetDirectories(Path.Combine(Mods, "Cool Skin")).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(Mods, RecompMods.Staging)));
    }

    [Fact]
    public void A_rar_installs_like_a_zip()
    {
        // neon.rar: RAR5, solid, best compression (WinRAR 7): Neon Karts/{readme.txt, romfs/Layout/Title.bin, romfs/Layout/Extra.bin}
        var result = Install(Fixture("neon.rar"));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(("Neon Karts", true), (result.ModId, result.Enabled));
        var romfs = Path.Combine(Mods, "Neon Karts", "romfs");
        Assert.Equal("a title from a rar", File.ReadAllText(Path.Combine(romfs, "Layout", "Title.bin")));
        Assert.Equal(string.Concat(Enumerable.Repeat("new file ", 20)), File.ReadAllText(Path.Combine(romfs, "Layout", "Extra.bin")));
        Assert.EndsWith($"Layout/Title.bin\t18\t{Sha("a title from a rar")}\t{Sha("the game's own title")}\n", File.ReadAllText(Path.Combine(romfs, "manifest.tsv")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("romfs/../../evil.txt")]
    [InlineData("romfs/NUL.txt")]
    [InlineData("romfs/a.txt:stream")]
    public void A_7z_name_that_leaves_the_folder_or_bends_on_Windows_refuses_the_whole_archive(string name)
    {
        var result = Install(SevenZip("bad.7z", ("romfs/ok.bin", "ok"), (name, "evil")));
        Assert.Equal(ModInstallCode.Unsafe, result.Code);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_game, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Mods, "evil.txt")));
        Assert.Empty(Directory.Exists(Mods) ? Directory.GetDirectories(Mods) : []);
    }

    [Fact]
    public void A_rar_that_leaves_the_folder_is_refused()
    {
        // slip.rar: romfs/ok.bin and ../evil.txt (a WinRAR archive with that name put back, its header's CRC made again)
        var result = Install(Fixture("slip.rar"));
        Assert.Equal(ModInstallCode.Unsafe, result.Code);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Mods, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Mods, RecompMods.Staging, "evil.txt")));
        Assert.Empty(Directory.Exists(Mods) ? Directory.GetDirectories(Mods) : []);
    }

    [Fact]
    public void A_7z_holds_the_same_limits_and_refuses_a_name_twice_and_a_broken_archive()
    {
        Assert.Equal(ModInstallCode.Unsafe, Install(SevenZip("twice.7z", ("romfs/A.bin", "1"), ("romfs/a.bin", "2"))).Code);
        ModImport.LimitFiles = 3;
        Assert.Equal(ModInstallCode.TooBig, Install(SevenZip("many.7z", ("romfs/1", "1"), ("romfs/2", "2"), ("romfs/3", "3"), ("romfs/4", "4"))).Code);
        ModImport.LimitFiles = ModImport.MaxFiles;
        ModImport.LimitBytes = 100;
        Assert.Equal(ModInstallCode.TooBig, Install(SevenZip("big.7z", ("romfs/1", new string('x', 4000)))).Code);
        ModImport.LimitBytes = ModImport.MaxBytes;

        var good = File.ReadAllBytes(SevenZip("cut.7z", ("romfs/1", new string('x', 4000))));
        File.WriteAllBytes(Path.Combine(_root, "cut.7z"), good[..(good.Length / 2)]); // a download cut short
        Assert.Equal(ModInstallCode.Unpack, Install(Path.Combine(_root, "cut.7z")).Code);
        var rar = File.ReadAllBytes(Fixture("neon.rar"));
        File.WriteAllBytes(Path.Combine(_root, "neon.rar"), rar[..60]);
        Assert.Equal(ModInstallCode.Unpack, Install(Path.Combine(_root, "neon.rar")).Code);
        Assert.DoesNotContain(Directory.GetDirectories(Mods), d => !Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public void The_unpacking_limits_hold()
    {
        ModImport.LimitFiles = 3;
        Assert.Equal(ModInstallCode.TooBig, Install(Zip("many.zip", ("romfs/1", "1"), ("romfs/2", "2"), ("romfs/3", "3"), ("romfs/4", "4"))).Code);
        ModImport.LimitFiles = ModImport.MaxFiles;
        ModImport.LimitBytes = 100;
        Assert.Equal(ModInstallCode.TooBig, Install(Zip("big.zip", ("romfs/1", new string('x', 4000)))).Code);
        Assert.DoesNotContain(Directory.GetDirectories(Mods), d => !Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public void What_is_not_a_mod_a_code_mod_several_mods_and_a_name_in_use()
    {
        Assert.Equal(ModInstallCode.Layout, Install(Zip("none.zip", ("readme.txt", "hi"))).Code);
        File.WriteAllText(Path.Combine(_root, "text.zip"), "not an archive at all");
        Assert.Equal(ModInstallCode.Unpack, Install(Path.Combine(_root, "text.zip")).Code);
        Assert.Equal(ModInstallCode.ChangesCode, Install(Zip("code.zip", ("exefs/abc.ips", "p"), ("romfs/a", "a"))).Code);
        Assert.Equal(ModInstallCode.ChangesCode, Install(Zip("port.zip", ("mod.json", """{"switches": {"TIMER": "1"}}"""), ("romfs/a", "a"))).Code);
        Assert.Equal(ModInstallCode.Managed, Install(Zip("mkwmp.zip", ("plugin/mkwmp.dll", "x"))).Code);

        var several = Zip("pack.zip", ("Red/romfs/a", "r"), ("Blue/romfs/a", "b"));
        var asked = Install(several);
        Assert.Equal(ModInstallCode.Variants, asked.Code);
        Assert.Equal(["Blue", "Red"], asked.Variants);
        Assert.Equal("Blue", Install(several, r => r with { Variant = "Blue" }).ModId);

        var twice = Install(several, r => r with { Variant = "Blue" });
        Assert.Equal((ModInstallCode.Exists, "Blue"), (twice.Code, twice.ModId));
        Assert.Equal("b", File.ReadAllText(Path.Combine(Mods, "Blue", "romfs", "a")));

        var update = Install(Zip("blue2.zip", ("Blue/romfs/a", "b2")), r => r with { Replace = true });
        Assert.True(update.Ok, update.Message);
        Assert.Equal("b2", File.ReadAllText(Path.Combine(Mods, "Blue", "romfs", "a")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(Mods, RecompMods.Backups, "Blue", "romfs", "a"))); // the version before it
        Assert.Equal(["Blue"], _target.Mods(_install, null).Select(m => m.Id)); // the backup is not a mod
    }

    [Fact]
    public void A_folder_installs_like_an_archive_and_a_mod_that_replaces_files_needs_the_game_s_own()
    {
        var folder = Path.Combine(_root, "Loose Mod");
        Directory.CreateDirectory(Path.Combine(folder, "romfs", "Layout"));
        File.WriteAllText(Path.Combine(folder, "romfs", "Layout", "Title.bin"), "t");
        Assert.Equal("Loose Mod", Install(folder).ModId);
        Assert.True(File.Exists(Path.Combine(folder, "romfs", "Layout", "Title.bin"))); // copied, not moved

        Directory.Delete(Path.Combine(_game, "romfs"), recursive: true);
        Assert.Equal(ModInstallCode.GameFiles, Install(folder, r => r with { Name = "Second" }).Code);
        Assert.False(Directory.Exists(Path.Combine(Mods, "Second")));
    }

    [Theory]
    [InlineData("Cool Skin (v2)!", "Cool Skin _v2")]
    [InlineData("..", "")]
    [InlineData("  .hidden_  ", "hidden")]
    [InlineData("Ação é ótima", "A_o _ _tima")]
    public void A_mod_s_name_in_the_game_is_plain(string name, string id) => Assert.Equal(id, ModImport.CleanName(name));

    // ---- options ----------------------------------------------------------------------------

    private const string Options = """
        {
          "schema": 1,
          "note": {"en": "Read when the game starts.", "pt-BR": "Lido quando o jogo inicia."},
          "options": [
            {"key": "hud", "type": "toggle", "label": {"en": "Show the HUD", "pt-BR": "Mostrar o HUD"}, "description": "On screen.", "default": true, "group": "Display"},
            {"key": "style", "type": "choice", "label": "Style", "default": "b", "choices": [{"value": "a", "label": "Classic"}, {"value": "b", "label": {"en": "Modern", "pt": "Moderno"}}, "c"],
             "visible_when": {"key": "hud", "values": ["1"]}},
            {"key": "scale", "type": "range", "label": "Scale", "min": 0.5, "max": 2, "step": 0.25, "unit": "x", "default": 1, "restart": false},
            {"key": "tag", "type": "text", "label": "Tag", "max_length": 4, "pattern": "[A-Z]*"},
            {"key": "bad key!", "type": "toggle"},
            {"key": "what", "type": "colour"},
            {"key": "empty", "type": "choice", "choices": []},
            {"key": "hud", "type": "toggle", "label": "a second hud"}
          ]
        }
        """;

    [Fact]
    public void A_mod_s_options_json_is_the_game_settings_schema()
    {
        var schema = ModOptions.Parse(Encoding.UTF8.GetBytes(Options), "pt-BR", out var problem)!;
        Assert.Null(problem);
        Assert.Equal("Lido quando o jogo inicia.", schema.Note);
        Assert.Equal(["hud", "style", "scale", "tag"], schema.Settings.Select(s => s.Id)); // the wrong entries are left out, the rest stays
        var hud = schema.Find("hud")!;
        Assert.Equal((GameSettingKind.Toggle, "Mostrar o HUD", "1", "Display", true), (hud.Kind, hud.Label, hud.Default, hud.Group, hud.RequiresRestart));
        var style = schema.Find("style")!;
        Assert.Equal(["Classic", "Moderno", "c"], style.Options.Select(o => o.Label)); // "pt" serves "pt-BR"
        Assert.Equal(("b", "hud"), (style.Default, style.VisibleWhen!.Setting));
        var scale = schema.Find("scale")!;
        Assert.Equal((0.5, 2.0, 0.25, "x", "1", false), (scale.Min, scale.Max, scale.Step, scale.Unit, scale.Default, scale.RequiresRestart));
        Assert.Equal("Show the HUD", ModOptions.Parse(Encoding.UTF8.GetBytes(Options), null, out _)!.Find("hud")!.Label);

        Assert.Null(ModOptions.Parse("{ nope"u8, null, out problem));
        Assert.NotNull(problem);
        Assert.Null(ModOptions.Parse("""{"options": []}"""u8, null, out problem));
        Assert.Null(problem);
    }

    [Fact]
    public void Options_are_read_from_and_written_to_the_mod_s_settings_file_and_nothing_else_of_it_changes()
    {
        Folder("Hud", """{"title": "HUD"}""", options: Options);
        var settings = Path.Combine(_game, "profile", "mod-settings", "Hud.ini");
        Assert.NotNull(_target.Mods(_install, null).Single().Options);
        var mod = _target.Mods(_install, null).Single();

        // the game never ran: no profile to write into
        Assert.Throws<LauncherException>(() => _target.WriteModOptions(_install, mod, new Dictionary<string, string?> { ["hud"] = "0" }));

        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "secret=keep me\nscale=1.5\nstyle=zzz\n");
        Assert.Equal(new Dictionary<string, string> { ["scale"] = "1.5" }, _target.ReadModOptions(_install, mod)); // "zzz" is no option of style: its default shows

        _target.WriteModOptions(_install, mod, new Dictionary<string, string?> { ["hud"] = "0", ["scale"] = "2", ["tag"] = "ABC" });
        Assert.Equal("secret=keep me\nscale=2\nstyle=zzz\nhud=0\ntag=ABC\n", File.ReadAllText(settings));
        Assert.Equal("0", mod.Options!.Effective(_target.ReadModOptions(_install, mod))["hud"]);

        _target.WriteModOptions(_install, mod, new Dictionary<string, string?> { ["scale"] = null }); // back to the default: the line goes
        Assert.Equal("secret=keep me\nstyle=zzz\nhud=0\ntag=ABC\n", File.ReadAllText(settings));

        Assert.Throws<LauncherException>(() => _target.WriteModOptions(_install, mod, new Dictionary<string, string?> { ["scale"] = "9" }));
        Assert.Throws<LauncherException>(() => _target.WriteModOptions(_install, mod, new Dictionary<string, string?> { ["tag"] = "abc" }));
        Assert.Throws<LauncherException>(() => _target.WriteModOptions(_install, mod, new Dictionary<string, string?> { ["secret"] = "x" }));
        _running = true;
        Assert.Throws<LauncherException>(() => _target.WriteModOptions(_install, mod, new Dictionary<string, string?> { ["hud"] = "1" }));
        Assert.Equal("secret=keep me\nstyle=zzz\nhud=0\ntag=ABC\n", File.ReadAllText(settings));
    }

    // ---- before PLAY ------------------------------------------------------------------------

    [Fact]
    public void Before_the_first_start_with_another_set_of_mods_the_save_is_copied_once()
    {
        Directory.CreateDirectory(Path.Combine(_game, "profile", "save", "sub"));
        File.WriteAllText(Path.Combine(_game, "profile", "save", "sub", "a.sav"), "save");
        File.WriteAllText(Path.Combine(_game, "mkw.ini"), "[launcher]\nmods = mkwmp,Skin\nmods_played = mkwmp\n");
        var notes = new List<string>();

        RecompMods.BeforePlay(_install, new DateTime(2026, 11, 20, 9, 5, 7), notes.Add);

        Assert.Equal("save", File.ReadAllText(Path.Combine(_game, "profile", "save-before-mods", "20261120-090507", "sub", "a.sav")));
        Assert.Contains("mods_played = mkwmp,Skin", Ini, StringComparison.Ordinal);
        Assert.Single(notes);

        RecompMods.BeforePlay(_install, new DateTime(2026, 11, 20, 9, 6, 0), notes.Add); // the same set: nothing
        Assert.Single(Directory.GetDirectories(Path.Combine(_game, "profile", "save-before-mods")));
    }

    [Fact]
    public void Before_a_start_with_another_set_of_mods_every_players_save_is_copied_beside_it()
    {
        var profile = Path.Combine(_game, "profile");
        void Save(string dir, string text)
        {
            Directory.CreateDirectory(Path.Combine(dir, "save"));
            File.WriteAllText(Path.Combine(dir, "save", "a.sav"), text);
        }
        Save(profile, "first");
        Save(Path.Combine(profile, "players", "2"), "second");
        Save(Path.Combine(profile, "players", "10"), "tenth");
        Directory.CreateDirectory(Path.Combine(profile, "players", "3")); // a player who never played: no save, no copy
        Save(Path.Combine(profile, "players", "deleted"), "gone");          // taken off the list: not a player
        Save(Path.Combine(profile, "players", "0"), "zero");                // not a player number
        File.WriteAllText(Path.Combine(_game, "mkw.ini"), "[launcher]\nmods = Skin\n");
        var notes = new List<string>();

        RecompMods.BeforePlay(_install, new DateTime(2026, 11, 20, 9, 5, 7), notes.Add);

        string Copy(string dir) => File.ReadAllText(Path.Combine(dir, "save-before-mods", "20261120-090507", "a.sav"));
        Assert.Equal("first", Copy(profile));
        Assert.Equal("second", Copy(Path.Combine(profile, "players", "2")));
        Assert.Equal("tenth", Copy(Path.Combine(profile, "players", "10")));
        Assert.False(Directory.Exists(Path.Combine(profile, "players", "3", "save-before-mods")));
        Assert.False(Directory.Exists(Path.Combine(profile, "players", "deleted", "save-before-mods")));
        Assert.False(Directory.Exists(Path.Combine(profile, "players", "0", "save-before-mods")));
        Assert.False(Directory.Exists(Path.Combine(profile, "save-before-mods", "20261120-090507", "players"))); // only save/ is copied
        Assert.Equal(3, notes.Count);
        Assert.Contains("mods_played = Skin", Ini, StringComparison.Ordinal);
        Assert.Equal([profile, Path.Combine(profile, "players", "2"), Path.Combine(profile, "players", "10")],
            RecompMods.PlayerDirs(profile).Where(d => Directory.Exists(Path.Combine(d, "save"))));
    }

    [Fact]
    public void Each_player_keeps_its_newest_ten_save_copies()
    {
        var player = Path.Combine(_game, "profile", "players", "2");
        Directory.CreateDirectory(Path.Combine(player, "save"));
        File.WriteAllText(Path.Combine(player, "save", "a.sav"), "save");
        for (var i = 0; i < 10; i++)
        {
            Directory.CreateDirectory(Path.Combine(player, "save-before-mods", $"2026010{i % 9 + 1}-00000{i % 2}"));
        }
        Directory.CreateDirectory(Path.Combine(player, "save-before-mods", "mine")); // not a copy: never removed
        File.WriteAllText(Path.Combine(_game, "mkw.ini"), "[launcher]\nmods = Skin\n");

        RecompMods.BeforePlay(_install, new DateTime(2026, 11, 20, 9, 5, 7), _ => { });

        var left = Directory.GetDirectories(Path.Combine(player, "save-before-mods")).Select(Path.GetFileName).ToList();
        Assert.Equal(12 - 1, left.Count); // ten copies, the oldest gone, and "mine"
        Assert.Contains("20261120-090507", left);
        Assert.Contains("mine", left);
        Assert.DoesNotContain("20260101-000000", left);
    }

    // ---- GameBanana, from recorded answers --------------------------------------------------

    private sealed class Web : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = [];
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            var url = request.RequestUri!.AbsoluteUri;
            var route = Routes.FirstOrDefault(r => url.StartsWith(r.Key, StringComparison.Ordinal));
            var response = route.Value is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : route.Value(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }

        public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public static HttpResponseMessage File(byte[] body, string? finalUrl = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            if (finalUrl is not null)
            {
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl); // where the redirects ended
            }
            return response;
        }
    }

    // apiv11 Mod/Index, as GameBanana answered for game 22152 (trimmed to what is read; one row made up as rated)
    private const string IndexJson = """
        {"_aMetadata": {"_nRecordCount": 122, "_bIsComplete": false, "_nPerpage": 20},
         "_aRecords": [
          {"_idRow": 640001, "_sModelName": "Mod", "_sName": "Performance &amp; Mod", "_sProfileUrl": "https://gamebanana.com/mods/640001", "_tsDateAdded": 1794178931, "_tsDateModified": 1794194755,
           "_aPreviewMedia": {"_aImages": [{"_sType": "screenshot", "_sBaseUrl": "https://images.gamebanana.com/img/ss/mods", "_sFile": "6af0.jpg", "_sFile220": "220-90_6af0.jpg", "_sFile530": "530-90_6af0.jpg"}]},
           "_aSubmitter": {"_sName": "lintu!"}, "_aRootCategory": {"_sName": "Other/Misc"}, "_sVersion": "1.0.0", "_sInitialVisibility": "show", "_bHasContentRatings": false, "_nLikeCount": 11, "_nViewCount": 1933},
          {"_idRow": 640002, "_sModelName": "Mod", "_sName": "Rated Skin", "_aPreviewMedia": {"_aImages": [{"_sBaseUrl": "https://evil.example/img", "_sFile": "x.jpg"}]},
           "_aSubmitter": {"_sName": "bo"}, "_sInitialVisibility": "warn", "_bHasContentRatings": true, "_nLikeCount": 2, "_nViewCount": 40},
          {"_idRow": 640003, "_sModelName": "Tool", "_sName": "Not a mod"}
         ]}
        """;

    private static string ProfileJson(int game = GameBanana.Game, string scan = "clean", string md5 = "") => $$"""
        {"_idRow": 640001, "_sName": "Performance Mod", "_sProfileUrl": "https://gamebanana.com/mods/640001", "_tsDateAdded": 1794178931, "_tsDateModified": 1794194755,
         "_aPreviewMedia": {"_aImages": [{"_sBaseUrl": "https://images.gamebanana.com/img/ss/mods", "_sFile": "6af0.jpg", "_sFile530": "530-90_6af0.jpg"}]},
         "_sInitialVisibility": "show", "_sVersion": "1.1.0", "_nLikeCount": 11, "_nViewCount": 1933,
         "_sLicense": "<a href=\"https://creativecommons.org/licenses/by-nc/4.0/\"><img></a>",
         "_sText": "<p>Faster <b>menus</b>.</p><ul><li>One</li><li>Two &amp; three</li></ul><script>alert(1)</script>",
         "_aFiles": [
           {"_idRow": 1882164, "_sFile": "perf_v1.zip", "_nFilesize": {{PerfZip.Length}}, "_tsDateAdded": 1794178534, "_sDownloadUrl": "https://gamebanana.com/dl/1882164", "_sMd5Checksum": "{{md5}}", "_sAvResult": "{{scan}}", "_sVersion": "1.0.0", "_sDescription": "Initial release"},
           {"_idRow": 1882999, "_sFile": "perf_v1_1.zip", "_nFilesize": 10, "_tsDateAdded": 1794190000, "_sMd5Checksum": "", "_sAvResult": "clean", "_sVersion": "1.1.0", "_sDescription": ""}
         ],
         "_aSubmitter": {"_sName": "lintu!"}, "_aGame": {"_idRow": {{game}}, "_sName": "Mario Kart World"}, "_aCategory": {"_sName": "Other/Misc"} }
        """;

    private static readonly byte[] PerfZip = MakeZip(("perf/romfs/Layout/Title.bin", "faster"));

    private static byte[] MakeZip(params (string Path, string Text)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return memory.ToArray();
    }

    private (GameBanana Client, Web Web) Banana()
    {
        var web = new Web();
        web.Routes["https://gamebanana.com/apiv11/Mod/Index"] = _ => Web.Json(IndexJson);
        web.Routes["https://gamebanana.com/apiv11/Util/Search/Results"] = _ => Web.Json(IndexJson);
        web.Routes["https://gamebanana.com/apiv11/Mod/640001/ProfilePage"] = _ => Web.Json(ProfileJson(md5: Convert.ToHexStringLower(MD5.HashData(PerfZip))));
        web.Routes["https://gamebanana.com/dl/1882164"] = _ => Web.File(PerfZip, "https://files.gamebanana.com/mods/perf_v1.zip");
        return (new GameBanana(new HttpClient(web), Path.Combine(_root, "cache", "gamebanana"), "mkwmp-launcher/test"), web);
    }

    [Fact]
    public async Task GameBanana_s_list_is_asked_politely_once_and_read_as_mods()
    {
        var (client, web) = Banana();
        var page = await client.BrowseAsync(GbSort.Updated, 2, CancellationToken.None);

        Assert.Equal("https://gamebanana.com/apiv11/Mod/Index?_aFilters%5BGeneric_Game%5D=22152&_nPage=2&_nPerpage=20&_sSort=Generic_LatestUpdated", web.Seen.Single().RequestUri!.AbsoluteUri);
        Assert.Equal("mkwmp-launcher/test", web.Seen.Single().Headers.UserAgent.ToString());
        Assert.Equal((2, 122, 7), (page.Page, page.Total, page.Pages));
        Assert.Equal([640001, 640002], page.Mods.Select(m => m.Id)); // a Tool is not a mod
        var first = page.Mods[0];
        Assert.Equal(("Performance & Mod", "lintu!", 11, 1933, "Other/Misc", "https://gamebanana.com/mods/640001", false, "1.0.0"),
            (first.Name, first.Author, first.Likes, first.Views, first.Category, first.Url, first.Rated, first.Version));
        Assert.Equal("https://images.gamebanana.com/img/ss/mods/220-90_6af0.jpg", first.Thumb);
        Assert.True(page.Mods[1].Rated);
        Assert.Equal("", page.Mods[1].Thumb); // a picture from another host is never fetched

        await client.BrowseAsync(GbSort.Updated, 2, CancellationToken.None);
        Assert.Single(web.Seen); // answered from the cache for ten minutes
        var again = new GameBanana(new HttpClient(web), Path.Combine(_root, "cache", "gamebanana"), "x");
        await again.BrowseAsync(GbSort.Updated, 2, CancellationToken.None);
        Assert.Single(web.Seen); // and from the cache on disk by the next launcher

        await client.SearchAsync(" kart skin ", 1, CancellationToken.None);
        Assert.Equal("https://gamebanana.com/apiv11/Util/Search/Results?_sSearchString=kart%20skin&_idGameRow=22152&_sModelName=Mod&_nPage=1&_nPerpage=20", web.Seen[^1].RequestUri!.AbsoluteUri);
        Assert.Equal(2, client.Requests);
    }

    [Fact]
    public async Task A_mod_s_page_its_files_and_an_update()
    {
        var (client, web) = Banana();
        var details = await client.DetailsAsync(640001, CancellationToken.None);

        Assert.Equal("Faster menus.\nOne\nTwo & three\nalert(1)", details.Text); // plain text: nothing of the page's HTML is drawn
        Assert.Equal("CC BY-NC 4.0", details.License);
        Assert.Equal(new GbImage("https://images.gamebanana.com/img/ss/mods/530-90_6af0.jpg", "https://images.gamebanana.com/img/ss/mods/6af0.jpg"), details.Images.Single());
        Assert.Equal([1882164, 1882999], details.Files.Select(f => f.Id));
        Assert.Equal(("perf_v1.zip", true, "Initial release", "1.0.0", 1794178534L), (details.Files[0].Name, details.Files[0].Clean, details.Files[0].Note, details.Files[0].Version, details.Files[0].Added));

        Assert.Equal(1882999, Launcher.NewerFile(new ModSource(null, 640001, 1882164, "1.0.0", 1794178534), details)!.Id);
        Assert.Null(Launcher.NewerFile(new ModSource(null, 640001, 1882999, "1.1.0", 1794190000), details));

        web.Routes["https://gamebanana.com/apiv11/Mod/5/ProfilePage"] = _ => Web.Json(ProfileJson(game: 6498));
        Assert.Equal(Strings.GameBananaNotThisGame, (await Assert.ThrowsAsync<LauncherException>(() => client.DetailsAsync(5, CancellationToken.None))).Message);
        web.Routes["https://gamebanana.com/apiv11/Mod/6/ProfilePage"] = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.Equal(Strings.GameBananaFailed("HTTP 502"), (await Assert.ThrowsAsync<LauncherException>(() => client.DetailsAsync(6, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_GameBanana_file_is_downloaded_checked_and_installed_and_its_source_remembered()
    {
        var (client, _) = Banana();
        var details = await client.DetailsAsync(640001, CancellationToken.None);
        var downloads = Path.Combine(_root, "launcher", "downloads");
        var steps = new List<ModProgress>();

        var path = await client.DownloadAsync(details, details.Files[0], downloads, new Progress(steps), CancellationToken.None);

        Assert.Equal(Path.Combine(downloads, "perf_v1.zip"), path);
        Assert.Equal(PerfZip, File.ReadAllBytes(path));
        Assert.Equal(new ModProgress("download", PerfZip.Length, PerfZip.Length), steps[^1]);
        var result = Install(path, r => r with { Name = details.Mod.Name, Source = new ModSource(details.Mod.Url, details.Mod.Id, details.Files[0].Id, details.Files[0].Version, details.Files[0].Added) });
        Assert.True(result.Ok, result.Message);
        var mod = _target.Mods(_install, null).Single();
        Assert.Equal(("Performance Mod", "Performance Mod", 640001, 1882164), (mod.Id, mod.Name, mod.Source!.GameBananaMod, mod.Source.GameBananaFile));
    }

    private sealed class Progress(List<ModProgress> steps) : IProgress<ModProgress>
    {
        public void Report(ModProgress value) => steps.Add(value);
    }

    [Fact]
    public async Task A_file_the_scan_does_not_call_clean_a_wrong_checksum_and_another_host_are_refused()
    {
        var (client, web) = Banana();
        var downloads = Path.Combine(_root, "downloads");

        web.Routes["https://gamebanana.com/apiv11/Mod/640001/ProfilePage"] = _ => Web.Json(ProfileJson(md5: new string('0', 32)));
        var wrong = await client.DetailsAsync(640001, CancellationToken.None);
        Assert.Equal(Strings.GameBananaChecksum, (await Assert.ThrowsAsync<LauncherException>(() => client.DownloadAsync(wrong, wrong.Files[0], downloads, null, CancellationToken.None))).Message);
        Assert.Empty(Directory.GetFiles(downloads));

        web.Routes["https://gamebanana.com/dl/1882164"] = _ => Web.File(PerfZip, "https://files.example.net/perf_v1.zip");
        Assert.Equal(Strings.ModsDownloadFailed("files.example.net"), (await Assert.ThrowsAsync<LauncherException>(() => client.DownloadAsync(wrong, wrong.Files[0], downloads, null, CancellationToken.None))).Message);

        var dirty = GameBanana.ParseDetails(Encoding.UTF8.GetBytes(ProfileJson(scan: "flagged")), 640001);
        var before = web.Seen.Count;
        Assert.Equal(Strings.GameBananaNotClean("flagged"), (await Assert.ThrowsAsync<LauncherException>(() => client.DownloadAsync(dirty, dirty.Files[0], downloads, null, CancellationToken.None))).Message);
        Assert.Equal(before, web.Seen.Count); // never asked for
    }

    [Fact]
    public async Task A_picture_comes_only_from_GameBanana_s_image_host_and_once()
    {
        var (client, web) = Banana();
        web.Routes["https://images.gamebanana.com/img/ss/mods/a.jpg"] = _ => Web.File([1, 2, 3]);
        var path = await client.ImageAsync("https://images.gamebanana.com/img/ss/mods/a.jpg", CancellationToken.None);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path!));
        Assert.Equal(path, await client.ImageAsync("https://images.gamebanana.com/img/ss/mods/a.jpg", CancellationToken.None));
        Assert.Single(web.Seen);
        Assert.Null(await client.ImageAsync("https://evil.example/a.jpg", CancellationToken.None));
        Assert.Null(await client.ImageAsync("https://images.gamebanana.com/a.svg", CancellationToken.None));
        Assert.Single(web.Seen);
    }

    // ---- a link -----------------------------------------------------------------------------

    [Fact]
    public async Task A_zip_from_a_link_is_downloaded_beside_the_launcher_and_installed()
    {
        var web = new Web();
        web.Routes["https://mods.example.net/files/"] = _ =>
        {
            var response = Web.File(MakeZip(("Neon Karts/romfs/Layout/Title.bin", "neon")));
            response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "\"..\\..\\Neon Karts.zip\"" };
            return response;
        };
        var downloads = Path.Combine(_root, "launcher", "downloads");

        var path = await ModDownloader.DownloadAsync(new HttpClient(web), ModDownloader.Link(" https://mods.example.net/files/get?id=3 ")!, downloads, "mkwmp-launcher/test", null, CancellationToken.None);

        Assert.Equal(Path.Combine(downloads, "Neon Karts.zip"), path); // the answer's name, without the folders it tried to carry
        Assert.Empty(Directory.GetFiles(downloads, "*.part"));
        var result = Install(path, r => r with { Source = new ModSource("https://mods.example.net/files/get?id=3") });
        Assert.True(result.Ok, result.Message);
        Assert.Equal("Neon Karts", result.ModId);
        Assert.Equal("https://mods.example.net/files/get?id=3", _target.Mods(_install, null).Single().Source!.Url);
    }

    [Theory]
    [InlineData("http://mods.example.net/a.zip")]
    [InlineData("ftp://mods.example.net/a.zip")]
    [InlineData("https://user:pass@mods.example.net/a.zip")]
    [InlineData("C:\\mods\\a.zip")]
    [InlineData("mods.example.net/a.zip")]
    [InlineData("")]
    public void Only_an_https_link_is_one(string link) => Assert.Null(ModDownloader.Link(link));

    [Fact]
    public async Task A_download_that_fails_leaves_nothing_behind()
    {
        var web = new Web();
        var downloads = Path.Combine(_root, "downloads");
        var error = await Assert.ThrowsAsync<LauncherException>(() => ModDownloader.DownloadAsync(new HttpClient(web), new Uri("https://mods.example.net/missing.zip"), downloads, "x", null, CancellationToken.None));
        Assert.Equal(Strings.ModsDownloadFailed("HTTP 404"), error.Message);
        Assert.False(Directory.Exists(downloads) && Directory.GetFiles(downloads).Length > 0);
    }
}

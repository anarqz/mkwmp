// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Mkwmp.Core.Files;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The launcher's own file picker (Files/FileBrowser.cs): the places of a Windows machine and of a
/// Linux one seen through Wine (a temp folder stands in for <c>Z:\</c>), what a listing shows for
/// each purpose, the target's word on a choice, folders that cannot be listed, links that loop,
/// folders too large to list whole, and the folder remembered per purpose.
/// </summary>
public sealed class FilePickerTests
{
    private static string Dir(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Touch(string folder, string name, string text = "x")
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A Steam Deck: the home with a Steam library, an SD card with another, an older SteamOS mount, /mnt.</summary>
    private static (FileBrowser Browser, string Root) Deck(bool withSteam = true)
    {
        var root = Rig.NewFolder("wine");
        Dir(root, "proc");
        var home = Dir(root, "home", "deck");
        Dir(home, "Downloads");
        Dir(home, "Desktop");
        Dir(home, ".config");
        Dir(root, "run", "media", "deck", "SD");
        Dir(root, "run", "media", "mmcblk0p1");
        Dir(root, "mnt", "data");
        if (withSteam)
        {
            Dir(home, ".local", "share", "Steam", "steamapps", "common");
            Dir(root, "run", "media", "deck", "SD", "steamapps", "common");
            Touch(Path.Combine(home, ".local", "share", "Steam", "steamapps"), "libraryfolders.vdf",
                "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"/home/deck/.local/share/Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"/run/media/deck/SD\"\n\t}\n}\n");
        }
        return (new FileBrowser(FileHost.FakeWine(root)), Path.TrimEndingDirectorySeparator(root) + "\\");
    }

    [Fact]
    public void Under_Wine_the_places_are_the_Linux_ones_not_the_prefix()
    {
        var (browser, root) = Deck();
        var places = browser.Places();
        (PlaceKind, string)[] expected =
        [
            (PlaceKind.Home, root + @"home\deck"),
            (PlaceKind.Downloads, root + @"home\deck\Downloads"),
            (PlaceKind.Desktop, root + @"home\deck\Desktop"),
            // /run/media/deck holds the cards and is not one; an older SteamOS mounts the card right under /run/media
            (PlaceKind.Removable, root + @"run\media\deck\SD"),
            (PlaceKind.Removable, root + @"run\media\mmcblk0p1"),
            (PlaceKind.Steam, root + @"home\deck\.local\share\Steam\steamapps\common"),
            (PlaceKind.Steam, root + @"run\media\deck\SD\steamapps\common"),
            (PlaceKind.Mount, root + @"mnt\data"),
            (PlaceKind.Root, root),
            (PlaceKind.Computer, ""),
        ];
        Assert.Equal(expected, places.Select(p => (p.Kind, p.Path)).ToArray());
        Assert.Equal("SD", places.Single(p => p.Path.EndsWith(@"deck\SD", StringComparison.Ordinal)).Name);
        // Documents is not there on this machine: not offered. The prefix's C: only on request.
        Assert.DoesNotContain(places, p => p.Kind is PlaceKind.Documents or PlaceKind.Prefix or PlaceKind.Drive);
        Assert.Contains(browser.Places(showAll: true), p => p.Kind == PlaceKind.Prefix && p.Path == @"C:\");

        // "This device" lists the cards, the mounts and the root, never Z: or C: as drives.
        var device = browser.List("", PickFilter.Folders);
        Assert.Equal(
            [root + @"run\media\deck\SD", root + @"run\media\mmcblk0p1", root + @"mnt\data", root],
            device.Entries.Select(e => e.Path).ToArray());
        Assert.All(device.Entries, e => Assert.Equal(FileType.Drive, e.Type));
    }

    [Fact]
    public void Under_Wine_paths_read_the_Linux_way_and_a_typed_Linux_path_is_found()
    {
        var (browser, root) = Deck();
        var downloads = root + @"home\deck\Downloads";
        Assert.Equal("/home/deck/Downloads", browser.Display(downloads));
        Assert.Equal("/", browser.Display(root));
        Assert.Equal(@"C:\users\steamuser", browser.Display(@"C:\users\steamuser")); // not under the Linux root: as it is

        Assert.Equal(downloads, browser.Resolve("/home/deck/Downloads"));
        Assert.Equal(downloads, browser.Resolve("  \"/home/deck/Downloads/\" "));
        Assert.Equal(downloads, browser.Resolve("~/Downloads"));
        Assert.Equal(root + @"home\deck", browser.Resolve("~"));
        Assert.Equal(root + @"run\media\deck\SD", browser.Resolve("file:///run/media/deck/SD"));
        // Wine opens a path whatever its letter case; the picker shows and keeps the folders' own names.
        Assert.Equal(downloads, browser.Resolve("/HOME/Deck/downloads"));
        Assert.Equal(downloads, browser.Resolve("Downloads", current: root + @"home\deck"));
        Assert.Null(browser.Resolve("Downloads")); // a name alone, and nowhere to look
        Assert.Null(browser.Resolve("   "));
        // A path that is not there is still a path: the listing says it is gone.
        Assert.Equal(root + @"home\deck\nope", browser.Resolve("/home/deck/nope"));

        // Above the Linux root is "this device", and nothing is above that.
        Assert.Equal(root + @"home\deck", browser.Parent(downloads));
        Assert.Equal(root, browser.Parent(root + "home"));
        Assert.Equal("", browser.Parent(root));
        Assert.Null(browser.Parent(""));

        var places = browser.Places();
        Assert.Equal(
            [(PlaceKind.Home, ""), (null, "Downloads"), (null, "Games")],
            browser.Crumbs(downloads + @"\Games", places).Select(c => (c.Kind, c.Kind is null ? c.Name : "")).ToArray());
        Assert.Equal(
            [(PlaceKind.Removable, "SD"), (null, "roms")],
            browser.Crumbs(root + @"run\media\deck\SD\roms", places).Select(c => ((PlaceKind?)c.Kind, c.Name)).ToArray());
        Assert.Equal(
            [(PlaceKind.Root, root), (null, root + "etc")],
            browser.Crumbs(root + "etc", places).Select(c => (c.Kind, c.Path)).ToArray());
        Assert.Equal([PlaceKind.Computer], browser.Crumbs("", places).Select(c => c.Kind).ToArray());
    }

    [Fact]
    public void Under_Wine_dot_files_are_hidden_and_names_keep_their_case()
    {
        var (browser, root) = Deck(withSteam: false);
        var home = root + @"home\deck";
        Touch(home, ".bashrc");
        Touch(home, "notes.txt");
        var shown = browser.List(home, PickFilter.Any);
        Assert.Equal(["Desktop", "Downloads", "notes.txt"], shown.Entries.Select(e => e.Name).ToArray());
        Assert.Equal(2, shown.Suppressed); // .config and .bashrc
        var all = browser.List(home, PickFilter.Any, showAll: true);
        Assert.Equal([".config", "Desktop", "Downloads", ".bashrc", "notes.txt"], all.Entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void On_Windows_the_places_are_the_players_folders_Steam_and_the_drives()
    {
        var root = Rig.NewFolder("win");
        var host = new FileHost
        {
            Home = Dir(root, "me"),
            Desktop = Dir(root, "me", "Desktop"),
            Downloads = Dir(root, "elsewhere", "Downloads"), // moved by the player
            Documents = Path.Combine(root, "me", "Documents"), // not there
            LauncherFolder = Dir(root, "mkwmp"),
            SteamLibraries = () => [Dir(root, "SteamLibrary"), Dir(root, "Steam")],
            Drives = () =>
            [
                new DriveEntry(@"C:\", DriveKind.Fixed, "System", 120_000_000_000),
                new DriveEntry(@"E:\", DriveKind.Removable, "CARD", 30_000_000_000),
                new DriveEntry(@"N:\", DriveKind.Network),
            ],
        };
        Dir(root, "SteamLibrary", "steamapps", "common");
        var browser = new FileBrowser(host);
        var places = browser.Places();
        (PlaceKind, string)[] expected =
        [
            (PlaceKind.Home, host.Home),
            (PlaceKind.Desktop, host.Desktop),
            (PlaceKind.Downloads, host.Downloads),
            (PlaceKind.Steam, Path.Combine(root, "SteamLibrary", "steamapps", "common")),
            (PlaceKind.Steam, Path.Combine(root, "Steam")),
            (PlaceKind.Launcher, host.LauncherFolder),
            (PlaceKind.Drive, @"C:\"),
            (PlaceKind.Removable, @"E:\"),
            (PlaceKind.Network, @"N:\"), // listed without being asked whether it answers
            (PlaceKind.Computer, ""),
        ];
        Assert.Equal(expected, places.Select(p => (p.Kind, p.Path)).ToArray());
        Assert.Equal(("System", 120_000_000_000L), (places[6].Name, places[6].Free));

        // The folder of the last pick comes first; one that is gone gives its nearest folder still there; a place is never listed twice.
        var games = Dir(root, "me", "Games");
        Assert.Equal((PlaceKind.Last, games, "Games"), browser.Places(last: games) is [var first, ..] ? (first.Kind, first.Path, first.Name) : default);
        Assert.Equal(games, browser.Places(last: Path.Combine(games, "gone", "deeper"))[0].Path);
        Assert.Equal(PlaceKind.Home, browser.Places(last: host.Home)[0].Kind);
        Assert.Equal(PlaceKind.Home, browser.Places(last: @"Q:\not\a\drive")[0].Kind);

        // Paths: a bare drive is its root, %NAME% is filled in, the long-path prefix comes off, a share has nothing above it but this device.
        Assert.Equal(@"D:\", browser.Resolve("d:"));
        Assert.Equal(@"D:\Games", browser.Resolve(@"\\?\D:\Games\"));
        Assert.Equal(@"\\nas\share\dumps", browser.Resolve(@"\\?\UNC\nas\share\dumps"));
        Assert.Equal("", browser.Parent(@"\\nas\share"));
        Assert.Equal(@"\\nas\share", browser.Parent(@"\\nas\share\dumps"));
        Assert.Equal("", browser.Parent(@"C:\"));
        Assert.Equal(@"C:\", browser.Parent(@"C:\Games"));
        Environment.SetEnvironmentVariable("MKWMP_PICKER_TEST", games);
        Assert.Equal(games, browser.Resolve("%MKWMP_PICKER_TEST%"));
        Assert.Equal("/home/deck", browser.Display("/home/deck")); // no Linux root here: nothing is rewritten
        Assert.Equal(
            [(PlaceKind.Home, host.Home), (null, games)],
            browser.Crumbs(games, places).Select(c => (c.Kind, c.Path)).ToArray());
        Assert.Equal(
            [@"D:\", @"D:\Games", @"D:\Games\mkw"],
            browser.Crumbs(@"D:\Games\mkw", places).Select(c => c.Path).ToArray());
        Assert.Equal(
            [@"\\nas\share", @"\\nas\share\dumps"],
            browser.Crumbs(@"\\nas\share\dumps", places).Select(c => c.Path).ToArray());
    }

    [Fact]
    public void A_listing_shows_folders_and_the_files_of_its_purpose()
    {
        var root = Rig.NewFolder("list");
        var browser = new FileBrowser(new FileHost());
        var target = new MkwRecompTarget("1.4.0");
        Touch(root, "Mario Kart World.nup");
        Touch(root, "OTHER.NX");
        Touch(root, "readme.txt");
        Touch(root, "mkw.exe");
        Touch(root, "track10.nup");
        Touch(root, "track2.nup");
        File.SetAttributes(Touch(root, "secret.nup"), FileAttributes.Hidden);
        Dir(root, "saves");
        var loose = Dir(root, "dump");
        Touch(Path.Combine(loose, "exefs"), "main", "NSO0");
        Dir(loose, "romfs");
        Dir(root, "カート ñ 日本語");

        // The game copy: folders (the one that holds a copy is marked), then the packages, by name the way people count.
        var copies = browser.List(root, target.GameCopyFilter);
        Assert.Equal(
            ["dump", "saves", "カート ñ 日本語", "Mario Kart World.nup", "OTHER.NX", "track2.nup", "track10.nup"],
            copies.Entries.Select(e => e.Name).ToArray());
        Assert.Equal(["dump"], copies.Entries.Where(e => e.IsFolder && e.Match).Select(e => e.Name).ToArray());
        Assert.All(copies.Entries.Where(e => !e.IsFolder), e => Assert.True(e.Match && e.Type == FileType.Package && e.Size == 1));
        Assert.Equal((3, false, false), (copies.Suppressed, copies.Truncated, copies.SelfMatch)); // readme.txt, mkw.exe, the hidden one
        Assert.True(browser.List(loose, target.GameCopyFilter).SelfMatch);

        // "Show all files": everything, the others not to be picked.
        var all = browser.List(root, target.GameCopyFilter, showAll: true);
        Assert.Equal(0, all.Suppressed);
        Assert.Equal(["mkw.exe", "readme.txt"], all.Entries.Where(e => !e.IsFolder && !e.Match).Select(e => e.Name).ToArray());
        Assert.Equal(FileType.Program, all.Entries.Single(e => e.Name == "mkw.exe").Type);
        Assert.Contains(all.Entries, e => e.Name == "secret.nup" && e.Match);

        // A game folder: folders only; files come with "show all" and never match.
        var installs = new PickFilter { FolderMatches = target.LooksLikeInstall };
        var folders = browser.List(root, installs);
        Assert.All(folders.Entries, e => Assert.True(e.IsFolder));
        Assert.Equal((3, 7, true), (folders.Entries.Count, folders.Suppressed, folders.SelfMatch));
        Assert.DoesNotContain(browser.List(root, installs, showAll: true).Entries, e => e.Match);
        Assert.False(browser.List(loose, installs).SelfMatch);

        // Every file, for a purpose without a filter.
        Assert.Equal(6, browser.List(root, PickFilter.Any).Entries.Count(e => !e.IsFolder && e.Match));
        Assert.True(copies.Entries[0].Modified > DateTime.Now.AddMinutes(-5));
    }

    [Fact]
    public async Task A_choice_is_judged_by_the_target_itself()
    {
        await using var rig = new Rig(new FakeSeatServer());
        ILauncher launcher = rig.Launcher;

        // The game folder: the check of ChooseGameFolderAsync, without saving anything.
        Assert.Null(launcher.GameFolderProblem(rig.Game));
        Assert.Equal(Strings.NotAGameFolder(Strings.RecompFolderHint), launcher.GameFolderProblem(rig.Root));
        Assert.Equal(Strings.NotAGameFolder(Strings.RecompFolderHint), launcher.GameFolderProblem(Path.Combine(rig.Root, "gone")));
        Assert.Equal(rig.Game, launcher.Settings.Game);
        Assert.True(launcher.GameFolderFilter.FolderMatches!(rig.Game));
        Assert.False(launcher.GameFolderFilter.FolderMatches!(rig.Root));
        Assert.False(launcher.GameFolderFilter.Files);

        // The player's copy: the setup's own first look (a package by its first bytes, a folder by exefs and romfs).
        var nup = Path.Combine(rig.Root, "game.nup");
        File.WriteAllBytes(nup, [.. "NUP"u8, 1, 0, 0, 0, 0]);
        var photo = Touch(rig.Root, "holiday.nup", "a photo, renamed");
        Assert.Null(launcher.GameCopyProblem(nup));
        Assert.Equal(Strings.SetupDumpNotAGame(photo), launcher.GameCopyProblem(photo));
        Assert.Equal(Strings.SetupDumpNotAGame(rig.Root), launcher.GameCopyProblem(rig.Root));
        Assert.Equal(Strings.SetupDumpNotFound(Path.Combine(rig.Root, "gone.nup")), launcher.GameCopyProblem(Path.Combine(rig.Root, "gone.nup")));
        Assert.Equal([".nup", ".nx"], launcher.GameCopyFilter.Extensions);
        Assert.True(launcher.GameCopyFilter.Matches("Game.NUP"));
        Assert.False(launcher.GameCopyFilter.Matches("game.zip"));

        // A target that says nothing (the interface's defaults): every file, no objection, Detect decides the mark.
        IGameTarget plain = new PlainTarget();
        Assert.Same(PickFilter.Any, plain.GameCopyFilter);
        Assert.Null(plain.GameCopyProblem(photo));
        Assert.False(plain.LooksLikeInstall(rig.Game));
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_says_why()
    {
        var root = Rig.NewFolder("denied");
        var browser = new FileBrowser(new FileHost());
        var locked = Dir(root, "locked");
        Touch(locked, "inside.nup");
        var info = new DirectoryInfo(locked);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        try
        {
            var listing = browser.List(locked, PickFilter.Any);
            Assert.Equal((ListProblem.Denied, 0, locked), (listing.Problem, listing.Entries.Count, listing.Path));
            // Its parent still lists it: the refusal is the folder's, not the picker's.
            Assert.Contains(browser.List(root, PickFilter.Any).Entries, e => e.Name == "locked");
        }
        finally
        {
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
        Assert.Equal(ListProblem.None, browser.List(locked, PickFilter.Any).Problem);

        // Gone (a card taken out, a folder deleted meanwhile), and a file where a folder was asked for.
        Assert.Equal(ListProblem.NotFound, browser.List(Path.Combine(root, "gone"), PickFilter.Any).Problem);
        Assert.Equal(ListProblem.NotFound, browser.List(@"Q:\no\such\drive", PickFilter.Any).Problem);
        Assert.NotEqual(ListProblem.None, browser.List(Path.Combine(locked, "inside.nup"), PickFilter.Any).Problem);
        Assert.Equal(locked, browser.NearestExisting(Path.Combine(locked, "gone", "deeper")));
        Assert.Equal("", browser.NearestExisting(@"Q:\no\such\drive"));
        Assert.Equal("", browser.NearestExisting(null));
    }

    [Fact]
    public void A_loop_of_links_never_grows_the_path()
    {
        var root = Rig.NewFolder("loop");
        var browser = new FileBrowser(new FileHost());
        var a = Dir(root, "a");
        var b = Dir(a, "b");
        var elsewhere = Dir(root, "elsewhere");
        Junction(Path.Combine(b, "back"), a); // a\b\back -> a
        Junction(Path.Combine(a, "side"), elsewhere); // a\side -> elsewhere

        var listing = browser.List(b, PickFilter.Folders);
        Assert.Equal((ListProblem.None, "back", true), (listing.Problem, listing.Entries[0].Name, listing.Entries[0].Link));
        // Opening the link that points above itself goes there, instead of a\b\back\b\back\…
        Assert.Equal(a, browser.Enter(Path.Combine(b, "back")));
        // Any other link is opened where it stands; a plain folder and a missing one too.
        Assert.Equal(Path.Combine(a, "side"), browser.Enter(Path.Combine(a, "side")));
        Assert.Equal(b, browser.Enter(b));
        Assert.Equal(Path.Combine(root, "gone"), browser.Enter(Path.Combine(root, "gone")));
        // Through a link the listing is the target's.
        Touch(elsewhere, "there.txt");
        Assert.Equal(["there.txt"], browser.List(Path.Combine(a, "side"), PickFilter.Any).Entries.Select(e => e.Name).ToArray());

        // And a path no real folder has (typed, or built by a loop the system does not name) is not opened at all.
        var deep = string.Join('\\', Enumerable.Repeat("back", FileBrowser.MaxDepth + 1).Prepend(root));
        Assert.Equal(ListProblem.TooDeep, browser.List(deep, PickFilter.Folders).Problem);
    }

    [Fact]
    public void A_huge_folder_is_listed_in_part_and_says_so()
    {
        var root = Rig.NewFolder("huge");
        for (var i = 0; i < 60; i++)
        {
            Touch(root, $"file{i}.nup", "");
        }
        for (var i = 0; i < 5; i++)
        {
            Dir(root, $"folder{i}");
        }

        // Reading stops at the cap, whatever the folder holds.
        var scanned = new FileBrowser(new FileHost()) { MaxScan = 50 }.List(root, PickFilter.Any);
        Assert.Equal((50, true), (scanned.Entries.Count, scanned.Truncated));

        // Of what was read, folders are never the ones cut, and the files are the first by name.
        var shown = new FileBrowser(new FileHost()) { MaxShown = 10 }.List(root, PickFilter.Any);
        Assert.True(shown.Truncated);
        Assert.Equal(
            ["folder0", "folder1", "folder2", "folder3", "folder4", "file0.nup", "file1.nup", "file2.nup", "file3.nup", "file4.nup"],
            shown.Entries.Select(e => e.Name).ToArray());

        // Only so many folders are asked whether they hold what is looked for.
        var asked = 0;
        var marking = new FileBrowser(new FileHost()) { MaxMarked = 2 }.List(root, new PickFilter { FolderMatches = _ => ++asked > 0 });
        Assert.Equal((3, 2), (asked, marking.Entries.Count(e => e.Match))); // two folders, and the listed folder itself

        var whole = new FileBrowser(new FileHost()).List(root, PickFilter.Any);
        Assert.Equal((65, false), (whole.Entries.Count, whole.Truncated));

        // A cancel ends a listing.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new FileBrowser(new FileHost()).List(root, PickFilter.Any, cancel: cancelled.Token));
    }

    [Fact]
    public void Long_paths_and_names_in_any_script_are_listed()
    {
        var root = Rig.NewFolder("long");
        var browser = new FileBrowser(new FileHost());
        var deep = root;
        while (deep.Length < 320)
        {
            deep = Path.Combine(deep, "a folder with a long name ・ 長い名前 ・ длинное имя");
        }
        Directory.CreateDirectory(deep);
        Touch(deep, "Марио Карт ワールド.nup");
        var listing = browser.List(deep, new MkwRecompTarget("1.4.0").GameCopyFilter);
        Assert.Equal((ListProblem.None, "Марио Карт ワールド.nup"), (listing.Problem, listing.Entries.Single().Name));
        Assert.True(listing.Entries[0].Path.Length > 320);
        Assert.Equal(deep, browser.Resolve(@"\\?\" + deep));
        Assert.Equal(deep, browser.Resolve(deep.ToUpperInvariant())); // typed in another case: the disk's own names
        Assert.Equal(Path.GetDirectoryName(deep), browser.Parent(deep));
    }

    [Fact]
    public void The_last_folder_of_each_purpose_is_kept_in_the_settings_file()
    {
        var folders = PickerFolders.Empty.With("game_copy", @"D:\Dumps\Mario Kart").With("game_folder", "Z:\\home\\deck\\Games");
        Assert.Equal(@"D:\Dumps\Mario Kart", folders["game_copy"]);
        Assert.Equal("", folders["never_used"]);
        // Value equality: the settings are saved only when something changed.
        Assert.Equal(folders, PickerFolders.Empty.With("game_folder", "Z:\\home\\deck\\Games").With("game_copy", @"D:\Dumps\Mario Kart"));
        Assert.Same(folders, folders.With("game_copy", @"D:\Dumps\Mario Kart"));
        Assert.NotEqual(folders, folders.With("game_copy", @"E:\"));
        Assert.Equal(new LauncherSettings { Picker = folders }, new LauncherSettings { Picker = folders.With("x", "") });
        Assert.NotEqual(new LauncherSettings(), new LauncherSettings { Picker = folders });
        // A purpose is a bare key; nothing else is kept, and the table cannot grow without end.
        Assert.Same(folders, folders.With("Game Copy", @"D:\"));
        Assert.Same(folders, folders.With("game_copy", new string('x', PickerFolders.MaxPath + 1)));
        Assert.Single(folders.With("game_copy", "").All);
        var full = Enumerable.Range(0, 40).Aggregate(PickerFolders.Empty, (f, i) => f.With("p" + i, @"C:\"));
        Assert.Equal(PickerFolders.MaxEntries, full.All.Count);

        // client.toml: [picker], beside the launcher (portable), read back as it was written.
        var config = new ClientConfig { Settings = new LauncherSettings { Name = "Ana", Picker = folders } };
        var toml = config.ToToml();
        Assert.Contains("\n[picker]\ngame_copy = \"D:\\\\Dumps\\\\Mario Kart\"\ngame_folder = \"Z:\\\\home\\\\deck\\\\Games\"\n", toml, StringComparison.Ordinal);
        Assert.Equal(folders, ClientConfig.Parse(toml).Settings.Picker);
        Assert.True(config.SameAs(ClientConfig.Parse(toml)));
        // Nothing is written until the picker was used; a file without the table, or with odd things in it, loads.
        Assert.DoesNotContain("[picker]", new ClientConfig().ToToml(), StringComparison.Ordinal);
        Assert.Equal(PickerFolders.Empty, ClientConfig.Parse("name = \"Ana\"\n").Settings.Picker);
        var odd = ClientConfig.Parse("[picker]\ngame_copy = 3\n\"Bad Key\" = \"C:\\\\x\"\ngame_folder = \"C:\\\\Games\"\n").Settings.Picker;
        Assert.Equal([new KeyValuePair<string, string>("game_folder", @"C:\Games")], odd.All);
    }

    [Fact]
    public async Task The_launcher_saves_the_remembered_folder_with_its_settings()
    {
        await using var rig = new Rig(new FakeSeatServer());
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Picker = rig.Launcher.Settings.Picker.With("game_copy", rig.Root) });
        Assert.Equal(rig.Root, ClientConfig.Load(rig.ConfigPath).Settings.Picker["game_copy"]);
        Assert.Equal(rig.Game, ClientConfig.Load(rig.ConfigPath).Settings.Game);
    }

    /// <summary>A junction (a link to a folder that needs no privilege on Windows).</summary>
    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "mklink /J failed: " + mklink.StandardError.ReadToEnd());
    }

    /// <summary>A target that implements only what it must.</summary>
    private sealed class PlainTarget : IGameTarget
    {
        public string Id => "plain";
        public string DisplayName => "Plain";
        public string FolderHint => "any";
        public TargetCapabilities Capabilities => TargetCapabilities.None;
        public GameInstall? Detect(string folder) => null;
        public IEnumerable<string> SearchRoots() => [];
        public TargetCheck Check(GameInstall install) => new(TargetStatus.Ready, null, null, null, null);
        public IntegrationReport Inspect(GameInstall install, IntegrationPackage? package) => new(IntegrationState.Installed, "", []);
        public Task<IntegrationReport> InstallAsync(GameInstall install, IntegrationPackage package, CancellationToken cancel) => Task.FromResult(Inspect(install, package));
        public Task UninstallAsync(GameInstall install, CancellationToken cancel) => Task.CompletedTask;
        public void WriteSession(GameInstall install, SessionFile session)
        {
        }

        public void ForgetTicket(GameInstall install)
        {
        }

        public IGameProcess? Launch(GameInstall install, LaunchOptions options) => null;
        public bool IsRunning(GameInstall install) => false;
        public IEnumerable<SupportItem> SupportItems(GameInstall install) => [];
    }
}

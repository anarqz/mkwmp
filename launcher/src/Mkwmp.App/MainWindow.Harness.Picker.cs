// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using Mkwmp.App.ViewModels;
using Mkwmp.Core.Files;

namespace Mkwmp.App;

/// <summary>
/// The screenshot harness for the launcher's own file picker (docs/DESIGN.md, section 14):
/// <c>--picker copy|copy-folder|game</c> opens it for the player's copy of the game (a file or a
/// folder), for a folder only, or for the game folder; <c>--picker-at &lt;path&gt;</c> starts it there;
/// <c>--fake-wine</c> shows it as on a Steam Deck under Proton: a made-up Linux machine in a temp
/// folder that stands in for <c>Z:\</c> (a home, an SD card with a Steam library and a copy of the
/// game, an install of mkw-recomp), deleted when the launcher exits. Then <c>--then</c> presses work in it.
/// </summary>
public partial class MainWindow
{
    private async Task OpenHarnessPickerAsync(string[] args)
    {
        if (App.Option(args, "--picker") is not { } what)
        {
            return;
        }
        if (args.Contains("--fake-wine"))
        {
            var root = Path.Combine(Path.GetTempPath(), "mkwmp-harness-wine-" + Environment.ProcessId);
            BuildFakeDeck(root);
            Closed += (_, _) =>
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // a temp folder: the system clears it
                }
            };
            _vm.Files = new FileBrowser(FileHost.FakeWine(root) with { FreeSpace = folder => folder.Contains("SD512", StringComparison.Ordinal) ? 187_400_000_000 : folder.Contains("mnt", StringComparison.Ordinal) ? 902_000_000_000 : 41_300_000_000 });
        }
        var at = App.Option(args, "--picker-at") is { } start ? _vm.Files.Resolve(start) : null;
        var request = what switch
        {
            "game" => PickerRequests.GameFolder(_vm, "mkw-recomp", Mkwmp.Core.Strings.RecompFolderHint, at),
            "copy-folder" => PickerRequests.GameCopy(_vm, folderOnly: true, at),
            _ => PickerRequests.GameCopy(_vm, folderOnly: false, at),
        };
        _ = _vm.PickAsync(request);
        await Task.Delay(700).ConfigureAwait(true); // the panel slides in, the first listing arrives
    }

    /// <summary>A Steam Deck's files: /home/deck, an SD card under /run/media/deck, a drive under /mnt.</summary>
    private static void BuildFakeDeck(string root)
    {
        static string Dir(params string[] parts)
        {
            var path = Path.Combine(parts);
            Directory.CreateDirectory(path);
            return path;
        }

        static void Make(string folder, string name, long length, byte[]? head = null, int daysAgo = 3)
        {
            var path = Path.Combine(folder, name);
            using (var file = File.Create(path))
            {
                file.Write(head ?? []);
                file.SetLength(Math.Max(length, head?.Length ?? 0));
            }
            File.SetLastWriteTime(path, DateTime.Now.AddDays(-daysAgo));
        }

        byte[] nup = [.. "NUP"u8, 1, 0, 0, 0, 0];
        Dir(root, "proc");
        var home = Dir(root, "home", "deck");
        var downloads = Dir(home, "Downloads");
        Dir(home, "Desktop");
        Dir(home, "Documents");
        Dir(home, "Pictures");
        Dir(home, ".config");
        var recomp = Dir(home, "Games", "mkw-recomp");
        Make(recomp, "mkw.exe", 9_400_000, daysAgo: 6);
        Make(recomp, "mkw.ini", 2_300, daysAgo: 1);
        Dir(recomp, "mods", "mkwmp");
        Dir(recomp, "setup");
        Dir(home, "Games", "emulators");
        Dir(home, ".local", "share", "Steam", "steamapps", "common", "Proton 10.0");
        Dir(downloads, "mkwmp");
        Dir(downloads, "Screenshots 2026");
        Make(downloads, "Mario Kart World 1.8.0.nup", 7_800_000, nup, daysAgo: 12);
        Make(downloads, "holiday photos.zip", 3_600_000, daysAgo: 40);
        Make(downloads, "mkw-recomp-0.1.16-mkwmp.zip", 5_200_000, daysAgo: 2);
        Make(downloads, "notes.txt", 1_900, daysAgo: 1);

        var card = Dir(root, "run", "media", "deck", "SD512");
        Dir(card, "steamapps", "common", "Some Game");
        Dir(card, "roms");
        Dir(card, "Emulation");
        var dumps = Dir(card, "Dumps");
        var loose = Dir(dumps, "Mario Kart World [1.8.0]");
        Dir(loose, "romfs");
        Make(Dir(loose, "exefs"), "main", 6_100_000);
        Dir(dumps, "saves backup");
        Dir(dumps, "カート ワールド");
        Make(dumps, "Mario Kart World 1.8.0.nup", 7_800_000, nup, daysAgo: 12);
        Make(dumps, "Mario Kart World (old).nx", 6_900_000, [.. "NXAR"u8, 0, 0, 0, 0], daysAgo: 150);
        Make(dumps, "readme.txt", 640, daysAgo: 150);
        Dir(root, "mnt", "games");
        File.WriteAllText(
            Path.Combine(home, ".local", "share", "Steam", "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"/home/deck/.local/share/Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"/run/media/deck/SD512\"\n\t}\n}\n");
    }
}

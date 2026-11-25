// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The Play card's cover, made from the player's copy of the game (<see cref="RecompCover"/>).
/// Every file here is made by the test itself, block by block: a package, archives and textures in
/// the game's formats that hold nothing but flat colours. No game data is in the repository.
/// </summary>
public sealed class CoverArtTests
{
    private static readonly (byte R, byte G, byte B, byte A) Black = (0, 0, 0, 255);
    private static readonly (byte R, byte G, byte B, byte A) Sky = (40, 120, 220, 255);
    private static readonly (byte R, byte G, byte B, byte A) Sand = (220, 150, 60, 255);
    private static readonly (byte R, byte G, byte B, byte A) White = (255, 255, 255, 255);
    private static readonly (byte R, byte G, byte B, byte A) Clear = (0, 0, 0, 0);

    /// <summary>The menu picture, in small: its left quarter black (the game fades it for its menu), sky over sand at the right.</summary>
    private static byte[] Art() => SyntheticGame.Bntx(("GameMenu_00_00", 256, 144, (x, y) => x < 64 ? Black : y < 72 ? Sky : Sand));

    /// <summary>The title logo, in small: a white plate with a clear border of 8 pixels, the third texture of its file.</summary>
    private static byte[] Logo() => SyntheticGame.Sarc(
        ("blyt/TitleLogo_00.bflyt", [1, 2, 3]),
        ("timg/__Combined.bntx", SyntheticGame.Bntx(
            ("Gradation_00^s", 16, 16, (_, _) => Sky),
            ("None1x1^s", 4, 4, (_, _) => Black),
            ("TitleLogoL_00^w", 160, 80, (x, y) => x is >= 8 and < 152 && y is >= 8 and < 72 ? White : Clear))));

    private static (byte, byte, byte) At(RgbaImage image, int x, int y)
    {
        var at = ((y * image.Width) + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    private static GameInstall Install(string root, string? source)
    {
        Directory.CreateDirectory(root);
        if (source is not null)
        {
            File.WriteAllText(Path.Combine(root, "setup-state.json"), JsonSerializer.Serialize(new { version = RecompSetup.StateVersion, port = "0.1.16-mkwmp", source, romfs_mode = "link" }));
        }
        return new GameInstall(MkwRecompTarget.TargetId, root, Path.Combine(root, "mkw.exe"), "0.1.16-mkwmp", "1.8.0", null);
    }

    [Fact]
    public void Block_linear_storage_is_read_in_the_console_s_order()
    {
        // Inside a GOB (64 bytes by 8 rows): the Switch, and the handheld console with two bits of the offset swapped.
        Assert.Equal((32, 16, 64, 256), (BlockLinear.Offset(16, 0, 256, 0, false), BlockLinear.Offset(0, 1, 256, 0, false), BlockLinear.Offset(0, 2, 256, 0, false), BlockLinear.Offset(32, 0, 256, 0, false)));
        Assert.Equal((64, 16, 32, 128, 256), (BlockLinear.Offset(16, 0, 256, 0, true), BlockLinear.Offset(0, 1, 256, 0, true), BlockLinear.Offset(0, 2, 256, 0, true), BlockLinear.Offset(0, 4, 256, 0, true), BlockLinear.Offset(32, 0, 256, 0, true)));
        // The next GOB to the right is a whole block further; the next row of GOBs is under it in the same block.
        Assert.Equal((512 * 4, 512, 4 * 512 * 4), (BlockLinear.Offset(64, 0, 256, 2, true), BlockLinear.Offset(0, 8, 256, 2, true), BlockLinear.Offset(0, 32, 256, 2, true)));
        // Every byte of a level has a place of its own, and the copy puts each one back where it was.
        var rows = 21;
        var rowBytes = 18 * 16;
        var seen = new HashSet<long>();
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < rowBytes; x++)
            {
                Assert.True(seen.Add(BlockLinear.Offset(x, y, rowBytes, 1, true)));
            }
        }
        Assert.Null(BlockLinear.ToLinear(new byte[100], rowBytes, rows, 1, true)); // a file cut short
    }

    [Fact]
    public void A_texture_of_the_game_is_decoded_block_by_block()
    {
        // 70x50 is no multiple of the 4x4 blocks: 18 by 13 of them, each of its own colour.
        var bntx = SyntheticGame.Bntx(("First", 8, 8, (_, _) => Sand), ("Checks^w", 70, 50, (x, y) => ((byte)(x / 4 * 10), (byte)(y / 4 * 15), 200, 255)));
        var image = GameTextures.Texture(bntx, name => name.StartsWith("Checks", StringComparison.Ordinal))!;
        Assert.Equal((70, 50), (image.Width, image.Height));
        foreach (var (x, y) in new[] { (0, 0), (3, 3), (4, 0), (0, 4), (33, 21), (69, 49), (68, 3), (1, 48) })
        {
            Assert.Equal(((byte)(x / 4 * 10), (byte)(y / 4 * 15), (byte)200), At(image, x, y));
        }
        // Without a name: the first one.
        Assert.Equal((Sand.R, Sand.G, Sand.B), At(GameTextures.Texture(bntx)!, 5, 5));
        Assert.Null(GameTextures.Texture(bntx, name => name == "Missing"));
        // Packed as the game packs it, and inside an archive.
        Assert.Equal(bntx, GameTextures.Plain(SyntheticGame.Zs(bntx)));
        Assert.Equal(bntx, GameTextures.Plain(bntx));
        var archive = SyntheticGame.Sarc(("a/one.bflyt", [9, 9]), ("timg/__Combined.bntx", bntx), ("z/last.bin", [7]));
        Assert.Equal(bntx, GameTextures.InArchive(archive, ".bntx")!.Value.ToArray());
        Assert.Null(GameTextures.InArchive(archive, ".png"));
        // Not what it says it is: no picture, and no exception.
        Assert.Null(GameTextures.Texture([1, 2, 3]));
        Assert.Null(GameTextures.Texture(bntx.AsSpan(0, 0x300)));
        Assert.Null(GameTextures.Texture(Encoding.ASCII.GetBytes("BNTX" + new string('￿', 0x100))));
        Assert.Null(GameTextures.InArchive(bntx, ".bntx"));
        Assert.Null(GameTextures.Plain([0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3, 4]));
    }

    [Fact]
    public void The_cover_is_the_logo_over_the_right_part_of_the_menu_picture()
    {
        var art = GameTextures.Texture(Art())!;
        var logo = GameTextures.Texture(GameTextures.InArchive(Logo(), ".bntx")!.Value.Span, name => name.StartsWith("TitleLogoL_00", StringComparison.Ordinal))!;
        var cover = RecompCover.Compose(art, logo);
        Assert.Equal((RecompCover.Width, RecompCover.Height), (cover.Width, cover.Height));
        Assert.Equal((1280, 720), (cover.Width, cover.Height));
        // The black quarter of the picture is not in the cover: sky and sand from edge to edge.
        Assert.Equal((Sky.R, Sky.G, Sky.B), At(cover, 0, 10));
        Assert.Equal((Sky.R, Sky.G, Sky.B), At(cover, 1279, 10));
        Assert.Equal((Sand.R, Sand.G, Sand.B), At(cover, 0, 700));
        Assert.Equal((Sand.R, Sand.G, Sand.B), At(cover, 1279, 700));
        // The logo: in the middle, inside the square a card cuts out (x 280..1000), under the badge's corner and above the card's lines.
        Assert.Equal((255, 255, 255), At(cover, 640, 288));
        Assert.Equal((255, 255, 255), At(cover, 400, 200));
        Assert.Equal((255, 255, 255), At(cover, 880, 380));
        Assert.Equal((Sky.R, Sky.G, Sky.B), At(cover, 640, 100));  // above it
        Assert.Equal((Sand.R, Sand.G, Sand.B), At(cover, 640, 480)); // under it
        Assert.Equal((Sky.R, Sky.G, Sky.B), At(cover, 300, 288));  // beside it, still inside the square
        Assert.All(Enumerable.Range(0, cover.Width * cover.Height), i => Assert.Equal(255, cover.Pixels[(i * 4) + 3]));
        // A copy without the logo's file still has a cover: the picture alone.
        Assert.Equal((Sky.R, Sky.G, Sky.B), At(RecompCover.Compose(art, null), 640, 288));

        // As a file: a PNG of that size.
        var png = cover.ToPng();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal((1280, 720, 8, 2), (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)), png[24], png[25]));
        Assert.Equal("IEND", Encoding.ASCII.GetString(png, png.Length - 8, 4));
        Assert.Equal(SyntheticGame.Crc("IEND"u8), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(png.Length - 4)));
        Assert.Equal(SyntheticGame.Crc(png.AsSpan(12, 17)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(29)));
    }

    [Fact]
    public void The_cover_is_made_once_from_a_package_and_kept_beside_the_launcher()
    {
        var root = Rig.NewFolder("cover");
        var cache = Path.Combine(root, "launcher", "cache");
        var nup = Path.Combine(root, "dump", "Game.nup");
        Directory.CreateDirectory(Path.GetDirectoryName(nup)!);
        // A base game and its update in one package: the update's picture is the one read (packed with the package's own
        // codec), the logo is only in the base game; a DLC title of another id has a file of the same name that is not taken.
        SyntheticGame.Nup(nup,
            new SyntheticGame.Title(0x0400C3F00006E000, 0, new()
            {
                ["Layout/GameMenu_00_00.Nin_Ounce_NVN2.bntx.zs"] = (SyntheticGame.Zs(SyntheticGame.Bntx(("GameMenu_00_00", 64, 36, (_, _) => Black))), false),
                ["Layout/TitleLogo_00.Nin_Ounce_NVN2.blarc.zs"] = (SyntheticGame.Zs(Logo()), false),
                ["Layout/TitleLogo_00.Nin_Ounce_NVN2.bflyt"] = ([1, 2, 3], false),
                ["Mals/USen.Product.sarc"] = ([4, 5, 6], false),
            }),
            new SyntheticGame.Title(0x0400C3F00006E800, 917504, new()
            {
                ["Layout/GameMenu_00_00.Nin_Ounce_NVN2.bntx.zs"] = (SyntheticGame.Zs(Art()), true),
            }),
            new SyntheticGame.Title(0x0400C3F00006F001, 5, new()
            {
                ["Layout/GameMenu_00_00.Nin_Ounce_NVN2.bntx.zs"] = (SyntheticGame.Zs(SyntheticGame.Bntx(("GameMenu_00_00", 64, 36, (_, _) => White))), false),
            }));
        var install = Install(Path.Combine(root, "game"), nup);
        var target = new MkwRecompTarget("1.4.0");

        Assert.Equal(nup, RecompCover.Source(install));
        var composed = RecompCover.Compose(nup)!;
        Assert.Equal((Sky.R, Sky.G, Sky.B), At(composed, 20, 10)); // the update's picture, not the base game's black one
        Assert.Equal((255, 255, 255), At(composed, 640, 288)); // with the base game's logo

        var cover = target.CoverArt(install, cache)!;
        Assert.Equal(cache, Path.GetDirectoryName(cover));
        Assert.Matches("^cover-mkw-recomp-[0-9a-f]{16}\\.png$", Path.GetFileName(cover));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(cover)[..4]);
        Assert.Equal([Path.GetFileName(cover)], Directory.GetFiles(cache).Select(Path.GetFileName));
        // Nothing is written beside the copy or into the game folder, and the copy is as it was.
        Assert.Equal(["Game.nup"], Directory.GetFiles(Path.GetDirectoryName(nup)!).Select(Path.GetFileName));
        Assert.Equal(["setup-state.json"], Directory.GetFileSystemEntries(install.Root).Select(Path.GetFileName));

        // Again: the same file, not made again.
        var written = File.GetLastWriteTimeUtc(cover);
        File.WriteAllText(cover, "kept as it is");
        Assert.Equal(cover, target.CoverArt(install, cache));
        Assert.Equal("kept as it is", File.ReadAllText(cover));
        Assert.True(File.GetLastWriteTimeUtc(cover) >= written);

        // The copy is not there now (a drive that is not plugged in): the cover made from it still is.
        File.Move(nup, nup + ".away");
        Assert.Equal(cover, target.CoverArt(install, cache));
        File.Move(nup + ".away", nup);

        // Another copy of the game: its cover takes the place of the old one.
        File.SetLastWriteTimeUtc(nup, new DateTime(2031, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var other = target.CoverArt(install, cache)!;
        Assert.NotEqual(cover, other);
        Assert.Equal([Path.GetFileName(other)], Directory.GetFiles(cache).Select(Path.GetFileName));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(other)[..4]);
    }

    [Fact]
    public void A_loose_copy_is_read_too_and_a_copy_without_the_picture_has_no_cover()
    {
        var root = Rig.NewFolder("cover-loose");
        var cache = Path.Combine(root, "cache");
        var target = new MkwRecompTarget("1.4.0");

        // Loose files, romfs/ one folder below what the setup was given; the files not packed.
        var dump = Path.Combine(root, "dump");
        var layout = Path.Combine(dump, "MKW", "romfs", "Layout");
        Directory.CreateDirectory(layout);
        File.WriteAllBytes(Path.Combine(layout, "GameMenu_00_00.Nin_Ounce_NVN2.bntx"), Art());
        File.WriteAllBytes(Path.Combine(layout, "GameMenu_00_00.Nin_Ounce_NVN2.bntx.zs.changelist.json"), "{}"u8.ToArray());
        File.WriteAllBytes(Path.Combine(layout, "TitleLogo_00.Nin_Ounce_NVN2.blarc.zs"), SyntheticGame.Zs(Logo()));
        var loose = Install(Path.Combine(root, "game-loose"), dump);
        Assert.Equal(Path.Combine(dump, "MKW", "romfs"), RecompCover.Source(loose));
        Assert.NotNull(target.CoverArt(loose, cache));
        Assert.Equal((255, 255, 255), At(RecompCover.Compose(RecompCover.Source(loose)!)!, 640, 288));

        // The setup copied the game into the install: that copy is the one read, whatever the record says.
        var copied = Install(Path.Combine(root, "game-copied"), Path.Combine(root, "gone.nup"));
        Directory.CreateDirectory(Path.Combine(copied.Root, "romfs", "Layout"));
        File.WriteAllBytes(Path.Combine(copied.Root, "romfs", "Layout", "GameMenu_00_00.Nin_Ounce_NVN2.bntx.zs"), SyntheticGame.Zs(Art()));
        Assert.Equal(Path.Combine(copied.Root, "romfs"), RecompCover.Source(copied));
        Assert.Equal((Sky.R, Sky.G, Sky.B), At(RecompCover.Compose(RecompCover.Source(copied)!)!, 640, 288)); // no logo file: the picture alone

        // No cover, and never an exception: not set up, the copy gone, another kind of package, a damaged one,
        // a copy without the picture, a picture that is not a texture.
        var empty = Path.Combine(root, "cache-empty");
        Assert.Null(target.CoverArt(Install(Path.Combine(root, "game-new"), null), empty));
        Assert.Null(target.CoverArt(Install(Path.Combine(root, "game-gone"), Path.Combine(root, "nowhere.nup")), empty));
        var nx = Path.Combine(root, "game.nx");
        File.WriteAllBytes(nx, [.. "NXAR"u8, .. new byte[64]]);
        Assert.Null(target.CoverArt(Install(Path.Combine(root, "game-nx"), nx), empty));
        var cut = Path.Combine(root, "cut.nup");
        File.WriteAllBytes(cut, [.. "NUP\u0001"u8, .. new byte[4], 0x80, 0, 0, 0, .. new byte[200]]);
        Assert.Null(NupPackage.Open(cut));
        Assert.Null(target.CoverArt(Install(Path.Combine(root, "game-cut"), cut), empty));
        var wild = Path.Combine(root, "wild.nup");
        File.WriteAllBytes(wild, [.. "NUP\u0001"u8, .. new byte[4], 0x10, 0, 0, 0, .. new byte[4], .. "TOC\u0001"u8, .. Enumerable.Repeat((byte)0xFF, 0x60)]);
        Assert.Null(NupPackage.Open(wild));
        var bare = Path.Combine(root, "bare.nup");
        SyntheticGame.Nup(bare, new SyntheticGame.Title(0x0400C3F00006E000, 0, new() { ["Layout/Other.bntx.zs"] = (SyntheticGame.Zs(Art()), false) }));
        Assert.NotNull(NupPackage.Open(bare));
        Assert.Null(target.CoverArt(Install(Path.Combine(root, "game-bare"), bare), empty));
        var junk = Path.Combine(root, "junk.nup");
        SyntheticGame.Nup(junk, new SyntheticGame.Title(0x0400C3F00006E000, 0, new() { ["Layout/GameMenu_00_00.Nin_Ounce_NVN2.bntx.zs"] = (SyntheticGame.Zs(new byte[4096]), false) }));
        Assert.Null(target.CoverArt(Install(Path.Combine(root, "game-junk"), junk), empty));
        Assert.False(Directory.Exists(empty)); // and nothing was written for them

        // A target that has no cover says so by default.
        Assert.Null(((IGameTarget)new CoverlessTarget()).CoverArt(loose, cache));
    }

    [Fact]
    public async Task The_launcher_offers_the_cover_of_the_game_folder_in_use()
    {
        await using var rig = new Rig(new FakeSeatServer(), s => s with { Server = "" });
        var l = rig.Launcher;
        Assert.Null(l.GameCover); // a game that is not set up from a copy has none
        var nup = Path.Combine(rig.Root, "Game.nup");
        SyntheticGame.Nup(nup, new SyntheticGame.Title(0x0400C3F00006E000, 0, new()
        {
            ["Layout/GameMenu_00_00.Nin_Ounce_NVN2.bntx.zs"] = (SyntheticGame.Zs(Art()), false),
            ["Layout/TitleLogo_00.Nin_Ounce_NVN2.blarc.zs"] = (SyntheticGame.Zs(Logo()), false),
        }));
        // Another game folder, set up from that copy: its cover is made in the background, into cache\ beside the launcher.
        var other = Path.Combine(rig.Root, "game-with-cover");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "mkw.exe"), Rig.TestExe);
        File.WriteAllText(Path.Combine(other, "setup-state.json"), JsonSerializer.Serialize(new { version = RecompSetup.StateVersion, port = "0.1.16-mkwmp", source = nup, romfs_mode = "link" }));
        var changes = 0;
        l.Changed += (_, _) => Interlocked.Increment(ref changes);
        await l.SaveSettingsAsync(l.Settings with { Game = other });
        await Rig.Until("the cover", () => l.GameCover is not null);
        Assert.Equal(Path.Combine(rig.Root, "launcher", Launcher.CacheFolderName), Path.GetDirectoryName(l.GameCover));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(l.GameCover!)[..4]);
        Assert.True(Volatile.Read(ref changes) > 0); // the window was told

        // No game folder: no cover.
        await l.SaveSettingsAsync(l.Settings with { Game = Path.Combine(rig.Root, "nothing-here") });
        await Rig.Until("no cover", () => l.GameCover is null);
    }

    /// <summary>A target that implements nothing of the optional parts.</summary>
    private sealed class CoverlessTarget : IGameTarget
    {
        public string Id => "none";

        public string DisplayName => "none";

        public string FolderHint => "";

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

/// <summary>
/// Files in the game's formats, written by the tests: a <c>.nup</c> package, a SARC archive, a BNTX
/// texture file of the handheld console with ASTC 4x4 textures whose blocks are flat colours ("void extent"
/// blocks: 16 bytes that say one colour), and zstd frames. Each writer is the reverse of the reader
/// it tests, written from the formats' public descriptions.
/// </summary>
internal static class SyntheticGame
{
    public sealed record Title(ulong Id, uint Version, Dictionary<string, (byte[] Data, bool Packed)> Romfs);

    public static byte[] Zs(byte[] plain)
    {
        using var packer = new ZstdSharp.Compressor(3);
        return packer.Wrap(plain).ToArray();
    }

    /// <summary>One BNTX with these textures; <c>colour(x, y)</c> is asked once per 4x4 block.</summary>
    public static byte[] Bntx(params (string Name, int Width, int Height, Func<int, int, (byte R, byte G, byte B, byte A)> Colour)[] textures)
    {
        const int Table = 0x198; // as the game's files have it
        var file = new List<byte>(new byte[Table + (textures.Length * 8)]);
        void Put(int at, ReadOnlySpan<byte> bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                file[at + i] = bytes[i];
            }
        }
        void Put32(int at, uint value)
        {
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(word, value);
            Put(at, word);
        }
        void Put64(int at, ulong value)
        {
            Span<byte> word = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(word, value);
            Put(at, word);
        }
        Put(0, "BNTX"u8);
        Put(0x20, "Ounc"u8);
        Put32(0x24, (uint)textures.Length);
        Put64(0x28, Table);
        for (var t = 0; t < textures.Length; t++)
        {
            var (name, width, height, colour) = textures[t];
            var info = file.Count;
            Put64(Table + (t * 8), (ulong)info);
            file.AddRange(new byte[0xA0]);
            var nameAt = file.Count;
            var text = Encoding.UTF8.GetBytes(name);
            file.AddRange([(byte)text.Length, (byte)(text.Length >> 8), .. text, 0]);
            var levels = file.Count;
            file.AddRange(new byte[8]);
            while (file.Count % 0x200 != 0)
            {
                file.Add(0);
            }
            var data = file.Count;
            // The block height the console would choose: as high as the texture needs, 16 GOBs at most.
            var rows = (height + 3) / 4;
            var rowBytes = (width + 3) / 4 * 16;
            var log2 = 4;
            while (log2 > 0 && (8 << (log2 - 1)) >= rows)
            {
                log2--;
            }
            var blockRows = 8 << log2;
            var storage = new byte[(rows + blockRows - 1) / blockRows * ((rowBytes + 63) / 64) * 512 * (1 << log2)];
            for (var by = 0; by < rows; by++)
            {
                for (var bx = 0; bx < rowBytes / 16; bx++)
                {
                    var (r, g, b, a) = colour(bx * 4, by * 4);
                    // An ASTC "void extent" block: the marker, no extent (all ones), then R, G, B, A as 16 bits each.
                    byte[] block = [0xFC, 0xFD, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, r, r, g, g, b, b, a, a];
                    block.CopyTo(storage, BlockLinear.Offset(bx * 16, by, rowBytes, log2, switch2: true));
                }
            }
            file.AddRange(storage);
            Put(info, "BRTI"u8);
            file[info + 0x11] = 2; // 2D
            Put32(info + 0x1C, 0x2D06); // ASTC 4x4, sRGB
            Put32(info + 0x24, (uint)width);
            Put32(info + 0x28, (uint)height);
            Put32(info + 0x2C, 1);
            Put32(info + 0x30, 1);
            Put32(info + 0x34, (uint)log2);
            Put64(info + 0x60, (ulong)nameAt);
            Put64(info + 0x70, (ulong)levels);
            Put64(levels, (ulong)data);
        }
        return [.. file];
    }

    public static byte[] Sarc(params (string Name, byte[] Data)[] files)
    {
        var names = new List<byte>();
        var data = new List<byte>();
        var entries = new List<byte>();
        foreach (var (name, bytes) in files)
        {
            var entry = new byte[16].AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(entry, Hash(name));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 0x01000000 | (uint)(names.Count / 4));
            while (data.Count % 0x100 != 0)
            {
                data.Add(0);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)data.Count);
            data.AddRange(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)data.Count);
            entries.AddRange(entry.ToArray());
            names.AddRange(Encoding.UTF8.GetBytes(name));
            names.Add(0);
            while (names.Count % 4 != 0)
            {
                names.Add(0);
            }
        }
        var head = new byte[0x14 + 12 + entries.Count + 8 + names.Count];
        var dataAt = (head.Length + 0xFF) & ~0xFF;
        "SARC"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), 0x14);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(6), 0xFEFF);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), (uint)(dataAt + data.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)dataAt);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(16), 0x100);
        "SFAT"u8.CopyTo(head.AsSpan(0x14));
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0x18), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0x1A), (ushort)files.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x1C), 0x65);
        entries.CopyTo(head, 0x20);
        "SFNT"u8.CopyTo(head.AsSpan(0x20 + entries.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0x24 + entries.Count), 8);
        names.CopyTo(head, 0x28 + entries.Count);
        return [.. head, .. new byte[dataAt - head.Length], .. data];
    }

    private static uint Hash(string name) => name.Aggregate(0u, (hash, c) => (hash * 0x65) + c);

    /// <summary>
    /// A package as mkw-recomp's tools/prepare/nup.py lays one out: the header, the table of contents at 0x80
    /// (titles, dirs, files, children, names), then the files. A file marked packed is stored with the package's
    /// own codec (zstd).
    /// </summary>
    public static void Nup(string path, params Title[] titles)
    {
        var dirs = new List<(string Name, List<(bool Dir, int Index)> Kids)> { ("", []) };
        var files = new List<(string Name, byte[] Plain, bool Packed)>();
        var parts = new List<(int Exefs, int Romfs)>();
        int Dir(int parent, string name)
        {
            foreach (var (isDir, index) in dirs[parent].Kids)
            {
                if (isDir && dirs[index].Name == name)
                {
                    return index;
                }
            }
            dirs.Add((name, []));
            dirs[parent].Kids.Add((true, dirs.Count - 1));
            return dirs.Count - 1;
        }
        for (var t = 0; t < titles.Length; t++)
        {
            var top = Dir(0, $"title{t}");
            var exefs = Dir(top, "exefs");
            var romfs = Dir(top, "romfs");
            parts.Add((exefs, romfs));
            foreach (var (name, (data, packed)) in titles[t].Romfs)
            {
                var pieces = name.Split('/');
                var dir = romfs;
                foreach (var piece in pieces[..^1])
                {
                    dir = Dir(dir, piece);
                }
                files.Add((pieces[^1], data, packed));
                dirs[dir].Kids.Add((false, files.Count - 1));
            }
        }
        var pool = new List<byte>();
        (uint Offset, uint Length) Name(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            pool.AddRange(bytes);
            return ((uint)(pool.Count - bytes.Length), (uint)bytes.Length);
        }
        var children = dirs.Sum(d => d.Kids.Count);
        var poolAt = 0x30 + (titles.Length * 0x40) + (dirs.Count * 24) + (files.Count * 80) + (children * 4);
        var dirNames = dirs.Select(d => Name(d.Name)).ToList();
        var fileNames = files.Select(f => Name(f.Name)).ToList();
        var stored = files.Select(f => f.Packed ? Zs(f.Plain) : f.Plain).ToList();
        var at = (0x80 + poolAt + pool.Count + 0xFFF) & ~0xFFF;
        var offsets = new List<long>();
        foreach (var bytes in stored)
        {
            offsets.Add(at);
            at = (at + bytes.Length + 15) & ~15;
        }

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write("NUP\u0001"u8);
        w.Write(0x10080u);
        w.Write(0x80u);
        w.Write((ulong)(poolAt + pool.Count));
        w.Write(new byte[0x80 - 20]);
        w.Write("TOC\u0001"u8);
        w.Write((uint)titles.Length);
        w.Write((ulong)dirs.Count);
        w.Write((ulong)files.Count);
        w.Write((ulong)poolAt);
        w.Write((ulong)pool.Count);
        w.Write(new byte[8]);
        for (var t = 0; t < titles.Length; t++)
        {
            w.Write(titles[t].Id);
            w.Write(0u);
            w.Write((uint)parts[t].Exefs);
            w.Write((uint)parts[t].Romfs);
            w.Write(titles[t].Version);
            w.Write(new byte[0x40 - 24]);
        }
        var first = 0u;
        for (var d = 0; d < dirs.Count; d++)
        {
            w.Write(dirNames[d].Offset);
            w.Write(dirNames[d].Length);
            w.Write(first);
            w.Write((uint)dirs[d].Kids.Count);
            w.Write(new byte[8]);
            first += (uint)dirs[d].Kids.Count;
        }
        for (var f = 0; f < files.Count; f++)
        {
            w.Write(fileNames[f].Offset);
            w.Write(fileNames[f].Length);
            w.Write((ulong)files[f].Plain.Length);
            w.Write((ulong)offsets[f]);
            w.Write((ulong)stored[f].Length);
            w.Write(new byte[32]);
            w.Write((byte)(files[f].Packed ? 1 : 0));
            w.Write(new byte[15]);
        }
        foreach (var (_, kids) in dirs)
        {
            foreach (var (isDir, index) in kids)
            {
                w.Write((uint)(isDir ? index : dirs.Count + index));
            }
        }
        w.Write(pool.ToArray());
        for (var f = 0; f < files.Count; f++)
        {
            file.Position = offsets[f];
            file.Write(stored[f]);
        }
        file.SetLength(Math.Max(at, file.Position));
    }

    /// <summary>CRC-32 as a PNG chunk carries it (over its type and data).</summary>
    public static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFF;
    }
}

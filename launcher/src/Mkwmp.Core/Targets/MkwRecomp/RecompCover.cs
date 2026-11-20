// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// The game's cover for the launcher's Play card, made from the player's own copy of the game.
/// <para>
/// A dump (a <c>.nup</c>, or loose <c>exefs/</c> and <c>romfs/</c>) holds no console icon: that is
/// in the title's control data, which a dump of the game's files leaves out. What it does hold is
/// the art the game itself shows: the picture behind its main menu
/// (<c>romfs/Layout/GameMenu_00_00.*.bntx.zs</c>, 2560x1440) and its title logo
/// (<c>TitleLogoL_00</c> in <c>romfs/Layout/TitleLogo_00.*.blarc.zs</c>), both zstd-packed ASTC
/// textures. The cover is the logo over that picture, 16:9, with the logo inside the middle square
/// so that a square card shows it whole.
/// </para>
/// <para>
/// Where the copy is: <c>&lt;data&gt;\romfs</c> when the setup copied the game into the install
/// (a folder, or a package linked there), else the copy the last setup read
/// (<c>setup-state.json</c>'s <c>source</c>), where it is. It is only read. A <c>.nx</c> package
/// is not read (only the runtime has its reader): no cover then, and the card keeps its own art.
/// </para>
/// <para>
/// The result is one PNG in the launcher's <c>cache\</c> (portable: beside the exe), named after
/// the copy it was made from (path, size, date), so it is made once and made again only for another
/// copy. It never leaves the PC: no report, no upload, no release carries it.
/// </para>
/// </summary>
internal static class RecompCover
{
    /// <summary>Changes when the picture is composed differently: older files are then made again.</summary>
    private const string Recipe = "2";

    public const int Width = 1280;
    public const int Height = 720;

    private const string Prefix = "cover-" + MkwRecompTarget.TargetId + "-";
    private const string ArtName = "GameMenu_00_00.";
    private const string LogoArchive = "TitleLogo_00.";
    private const string LogoTexture = "TitleLogoL_00";

    /// <summary>The cover file that could not be made the last time (its name says which copy it was).</summary>
    private static volatile string? _none;

    /// <summary>The cover's file for this install, made now when it is not there yet; null when there is none to make.</summary>
    public static string? Find(GameInstall install, string cacheFolder)
    {
        ArgumentNullException.ThrowIfNull(install);
        try
        {
            if (Source(install) is not { } source || Stamp(source) is not { } stamp)
            {
                // The copy is not there now (a drive that is not plugged in): the cover made from it still is.
                return Directory.Exists(cacheFolder)
                    ? Directory.EnumerateFiles(cacheFolder, Prefix + "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                    : null;
            }
            var path = Path.Combine(cacheFolder, Prefix + stamp + ".png");
            if (File.Exists(path))
            {
                return path;
            }
            if (path == _none)
            {
                return null; // this very copy was read before and has no cover: not read again (the launcher asks now and then)
            }
            if (Compose(source) is not { } cover)
            {
                _none = path;
                return null;
            }
            Directory.CreateDirectory(cacheFolder);
            foreach (var old in Directory.EnumerateFiles(cacheFolder, Prefix + "*.png"))
            {
                File.Delete(old); // the cover of another copy, or of an older recipe
            }
            File.WriteAllBytes(path + ".tmp", cover.ToPng());
            File.Move(path + ".tmp", path, overwrite: true);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null; // a cover is never worth an error: the card keeps its own art
        }
    }

    /// <summary>The game's RomFS: a folder, or a <c>.nup</c> package; null when none is known or it is gone.</summary>
    internal static string? Source(GameInstall install)
    {
        var data = RecompSetup.DataDir(install);
        var copied = Path.Combine(data, "romfs");
        if (Directory.Exists(copied) || (File.Exists(copied) && NupPackage.IsNup(copied)))
        {
            return copied;
        }
        if (RecompSetup.ReadState(data)?.Source is not { Length: > 0 } source)
        {
            return null;
        }
        if (File.Exists(source))
        {
            return NupPackage.IsNup(source) ? source : null;
        }
        // Loose: romfs/ in the folder, or up to two levels below it (as the setup looks for it).
        if (!Directory.Exists(source))
        {
            return null;
        }
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        return new[] { Path.Combine(source, "romfs") }.Concat(Directory.EnumerateDirectories(source, "romfs", options))
            .FirstOrDefault(d => Directory.Exists(d) || (File.Exists(d) && NupPackage.IsNup(d)));
    }

    /// <summary>What names the cover of this copy: the copy's path, size and date, and the recipe.</summary>
    private static string? Stamp(string source)
    {
        var (length, written) = File.Exists(source)
            ? (new FileInfo(source).Length, File.GetLastWriteTimeUtc(source))
            : (0L, Directory.GetLastWriteTimeUtc(Path.Combine(source, "Layout")));
        var text = string.Create(CultureInfo.InvariantCulture, $"{Recipe}|{Path.GetFullPath(source).ToUpperInvariant()}|{length}|{written.Ticks}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>The two textures of the copy, composed; null when the copy has not both (another game, a partial copy).</summary>
    internal static RgbaImage? Compose(string source)
    {
        Func<string, byte[]?> read;
        if (File.Exists(source))
        {
            if (NupPackage.Open(source) is not { } package)
            {
                return null;
            }
            read = prefix => package.ReadRomfs("Layout", name => Matches(name, prefix), GameTextures.MaxBytes);
        }
        else
        {
            read = prefix => Directory.Exists(Path.Combine(source, "Layout"))
                && Directory.EnumerateFiles(Path.Combine(source, "Layout"), prefix + "*").FirstOrDefault(f => Matches(Path.GetFileName(f), prefix)) is { } file
                && new FileInfo(file).Length <= GameTextures.MaxBytes
                    ? File.ReadAllBytes(file)
                    : null;
        }
        if (read(ArtName) is not { } artFile || GameTextures.Plain(artFile) is not { } artBntx || GameTextures.Texture(artBntx) is not { } art)
        {
            return null;
        }
        RgbaImage? logo = null;
        if (read(LogoArchive) is { } logoFile && GameTextures.Plain(logoFile) is { } archive && GameTextures.InArchive(archive, ".bntx") is { } logoBntx)
        {
            logo = GameTextures.Texture(logoBntx.Span, name => name.StartsWith(LogoTexture, StringComparison.Ordinal));
        }
        return Compose(art, logo);
    }

    /// <summary>A file of the game's Layout folder by the start of its name: "GameMenu_00_00.Nin_Ounce_NVN2.bntx.zs" (or not packed).</summary>
    private static bool Matches(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal)
        && (name.EndsWith(".bntx.zs", StringComparison.Ordinal) || name.EndsWith(".bntx", StringComparison.Ordinal)
            || name.EndsWith(".blarc.zs", StringComparison.Ordinal) || name.EndsWith(".blarc", StringComparison.Ordinal));

    /// <summary>
    /// The cover: the menu picture's right three quarters (its left quarter fades to black, for the menu's list),
    /// 16:9, and the logo over it in the upper part of the middle square. Without a logo, the picture alone.
    /// </summary>
    internal static RgbaImage Compose(RgbaImage art, RgbaImage? logo)
    {
        // A 16:9 window of 3/4 of the picture's width, against its right edge, centred a little above the middle.
        var w = art.Width * 3 / 4;
        var h = Math.Min(art.Height, w * Height / Width);
        w = Math.Min(w, h * Width / Height);
        var cover = art.Resample(art.Width - w, (art.Height - h) * 4 / 10, w, h, Width, Height);
        for (var i = 3; i < cover.Pixels.Length; i += 4)
        {
            cover.Pixels[i] = 255;
        }
        if (logo is { Width: > 0, Height: > 0 })
        {
            // As wide as 5/6 of the middle square, between a fifth and three fifths of the height: the card has its
            // badge in the top right corner and writes its own lines at the bottom.
            var width = Height * 5 / 6;
            var height = Math.Max(1, width * logo.Height / logo.Width);
            if (height > Height * 4 / 10)
            {
                (width, height) = (Math.Max(1, Height * 4 / 10 * logo.Width / logo.Height), Height * 4 / 10);
            }
            cover.Draw(logo.Resample(0, 0, logo.Width, logo.Height, width, height), (Width - width) / 2, (Height * 2 / 10) + (((Height * 4 / 10) - height) / 2));
        }
        return cover;
    }
}

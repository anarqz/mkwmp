// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AssetRipper.TextureDecoder.Astc;
using AssetRipper.TextureDecoder.Rgb.Formats;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>A picture in memory: <see cref="Width"/> x <see cref="Height"/> pixels of R, G, B, A bytes (not premultiplied), rows from the top.</summary>
internal sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
    public static RgbaImage Blank(int width, int height) => new(width, height, new byte[width * height * 4]);

    /// <summary>
    /// The rectangle (<paramref name="x"/>, <paramref name="y"/>, <paramref name="w"/>, <paramref name="h"/>) of this
    /// picture as a new one of <paramref name="width"/> x <paramref name="height"/>: every pixel the average of the
    /// source pixels under it (colour weighted by alpha), so a large picture made small stays smooth.
    /// </summary>
    public RgbaImage Resample(int x, int y, int w, int h, int width, int height)
    {
        var result = Blank(width, height);
        for (var dy = 0; dy < height; dy++)
        {
            var y0 = Math.Clamp(y + (int)((long)dy * h / height), 0, Height - 1);
            var y1 = Math.Clamp(y + (int)((((long)dy + 1) * h) / height), y0 + 1, Height);
            for (var dx = 0; dx < width; dx++)
            {
                var x0 = Math.Clamp(x + (int)((long)dx * w / width), 0, Width - 1);
                var x1 = Math.Clamp(x + (int)((((long)dx + 1) * w) / width), x0 + 1, Width);
                long r = 0, g = 0, b = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var at = ((sy * Width) + x0) * 4;
                    for (var sx = x0; sx < x1; sx++, at += 4)
                    {
                        int alpha = Pixels[at + 3];
                        r += Pixels[at] * alpha;
                        g += Pixels[at + 1] * alpha;
                        b += Pixels[at + 2] * alpha;
                        a += alpha;
                    }
                }
                var to = ((dy * width) + dx) * 4;
                if (a > 0)
                {
                    result.Pixels[to] = (byte)(r / a);
                    result.Pixels[to + 1] = (byte)(g / a);
                    result.Pixels[to + 2] = (byte)(b / a);
                    result.Pixels[to + 3] = (byte)(a / ((long)(x1 - x0) * (y1 - y0)));
                }
            }
        }
        return result;
    }

    /// <summary>Draws <paramref name="over"/> on this picture with its top left corner at (<paramref name="left"/>, <paramref name="top"/>), by its alpha. This picture is taken as opaque.</summary>
    public void Draw(RgbaImage over, int left, int top)
    {
        for (var y = Math.Max(0, -top); y < over.Height && top + y < Height; y++)
        {
            for (var x = Math.Max(0, -left); x < over.Width && left + x < Width; x++)
            {
                var from = ((y * over.Width) + x) * 4;
                var to = (((top + y) * Width) + left + x) * 4;
                int alpha = over.Pixels[from + 3];
                for (var c = 0; c < 3; c++)
                {
                    Pixels[to + c] = (byte)(((over.Pixels[from + c] * alpha) + (Pixels[to + c] * (255 - alpha)) + 127) / 255);
                }
                Pixels[to + 3] = 255;
            }
        }
    }

    /// <summary>The picture as a PNG file's bytes (8-bit RGB: the alpha is dropped, a cover is opaque).</summary>
    public byte[] ToPng()
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (Width * 3)];
            for (var y = 0; y < Height; y++)
            {
                row[0] = 1; // filter "Sub": each byte minus the one a pixel to its left
                for (var x = 0; x < Width; x++)
                {
                    var at = ((y * Width) + x) * 4;
                    for (var c = 0; c < 3; c++)
                    {
                        row[1 + (x * 3) + c] = (byte)(Pixels[at + c] - (x > 0 ? Pixels[at - 4 + c] : 0));
                    }
                }
                zlib.Write(row);
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], Height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", packed.GetBuffer().AsSpan(0, (int)packed.Length));
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c;
    })];

    private static void Chunk(Stream png, string kind, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        png.Write(word);
        Span<byte> name = stackalloc byte[4];
        Encoding.ASCII.GetBytes(kind, name);
        png.Write(name);
        png.Write(data);
        var crc = 0xFFFFFFFF;
        foreach (var b in name)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFF);
        png.Write(word);
    }
}

/// <summary>
/// The game's own texture files, read on the CPU: a <c>.bntx</c> (alone, or inside a <c>.blarc</c> /
/// SARC archive), both usually zstd-packed (<c>.zs</c>). Only what a cover needs: level 0 of a 2D
/// ASTC texture of one layer, as RGBA. The offsets are the ones mkw-recomp's runtime reads
/// (runtime/probes/texpre_probe.c, runtime/gfx/nvn/tex/tex_deswizzle.c); nothing here is game data.
/// </summary>
internal static class GameTextures
{
    /// <summary>The largest file taken apart here (the game's largest layout texture is 4 MiB).</summary>
    public const int MaxBytes = 64 << 20;

    private const int MaxSide = 8192;

    /// <summary>The ASTC block sizes of formats 0x2D..0x3A, in the format's order.</summary>
    private static readonly (int W, int H)[] AstcBlocks =
        [(4, 4), (5, 4), (5, 5), (6, 5), (6, 6), (8, 5), (8, 6), (8, 8), (10, 5), (10, 6), (10, 8), (10, 10), (12, 10), (12, 12)];

    /// <summary>A file as the game stores it: unpacked when it is a zstd frame, as it is otherwise.</summary>
    public static byte[]? Plain(byte[] file) =>
        file.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(file) == 0xFD2FB528 ? Zstd.Unpack(file, MaxBytes) : file;

    /// <summary>The file of a SARC archive whose name ends with <paramref name="suffix"/> (".bntx"); null when there is none or it is not a SARC.</summary>
    public static ReadOnlyMemory<byte>? InArchive(byte[] sarc, string suffix)
    {
        // "SARC" u16 header size, u16 byte order, u32 file size, u32 where the data starts; then "SFAT" (u16 header
        // size, u16 count, u32 hash key) with 16-byte entries (hash, name offset / 4 with a flag, start, end), then
        // "SFNT" (8 bytes) and the names.
        if (sarc.Length < 0x20 || !sarc.AsSpan(0, 4).SequenceEqual("SARC"u8) || sarc[6] != 0xFF || sarc[7] != 0xFE)
        {
            return null;
        }
        int header = BinaryPrimitives.ReadUInt16LittleEndian(sarc.AsSpan(4));
        long data = BinaryPrimitives.ReadUInt32LittleEndian(sarc.AsSpan(12));
        if (header + 12 > sarc.Length || !sarc.AsSpan(header, 4).SequenceEqual("SFAT"u8))
        {
            return null;
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(sarc.AsSpan(header + 6));
        var names = (long)header + 12 + (count * 16) + 8;
        for (var i = 0; i < count; i++)
        {
            var entry = header + 12 + (i * 16);
            if (entry + 16 > sarc.Length)
            {
                return null;
            }
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(sarc.AsSpan(entry + 4));
            long start = BinaryPrimitives.ReadUInt32LittleEndian(sarc.AsSpan(entry + 8));
            long end = BinaryPrimitives.ReadUInt32LittleEndian(sarc.AsSpan(entry + 12));
            var name = names + ((attributes & 0xFFFF) * 4L);
            if ((attributes & 0x01000000) == 0 || name >= sarc.Length || start > end || data + end > sarc.Length)
            {
                continue;
            }
            var length = sarc.AsSpan((int)name).IndexOf((byte)0);
            if (length > 0 && Encoding.UTF8.GetString(sarc, (int)name, length).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return sarc.AsMemory((int)(data + start), (int)(end - start));
            }
        }
        return null;
    }

    /// <summary>
    /// Level 0 of the texture of a BNTX file whose name <paramref name="isIt"/> takes (the first one when null), as
    /// RGBA; null when the file is not a BNTX, has no such texture, or the texture is not a 2D ASTC one.
    /// </summary>
    public static RgbaImage? Texture(ReadOnlySpan<byte> bntx, Func<string, bool>? isIt = null)
    {
        // "BNTX" ...; at 0x20 the container: the platform ("NX  " on the Switch, "Ounc" on the handheld console), i32 texture
        // count, u64 where the table of texture pointers is. A texture ("BRTI"): u8 dimension at 0x11 (2 = 2D), u32
        // format at 0x1C, u32 width, height, depth, layers at 0x24, u32 log2 of the block height at 0x34, u64 name
        // (u16 length + text) at 0x60, u64 table of level pointers at 0x70.
        if (bntx.Length < 0x40 || !bntx[..4].SequenceEqual("BNTX"u8))
        {
            return null;
        }
        var switch2 = bntx.Slice(0x20, 4).SequenceEqual("Ounc"u8);
        var count = BinaryPrimitives.ReadInt32LittleEndian(bntx[0x24..]);
        var table = BinaryPrimitives.ReadUInt64LittleEndian(bntx[0x28..]);
        for (var i = 0; i < count && i < 4096; i++)
        {
            if (!Fits(bntx, table + ((ulong)i * 8), 8))
            {
                return null;
            }
            var info = BinaryPrimitives.ReadUInt64LittleEndian(bntx[(int)(table + ((ulong)i * 8))..]);
            if (!Fits(bntx, info, 0x78) || !bntx.Slice((int)info, 4).SequenceEqual("BRTI"u8))
            {
                return null;
            }
            var brti = bntx[(int)info..];
            var nameAt = BinaryPrimitives.ReadUInt64LittleEndian(brti[0x60..]);
            var name = "";
            if (Fits(bntx, nameAt, 2))
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(bntx[(int)nameAt..]);
                name = Fits(bntx, nameAt + 2, length) ? Encoding.UTF8.GetString(bntx.Slice((int)nameAt + 2, length)) : "";
            }
            if (isIt is not null && !isIt(name))
            {
                continue;
            }
            var format = BinaryPrimitives.ReadUInt32LittleEndian(brti[0x1C..]);
            var width = BinaryPrimitives.ReadInt32LittleEndian(brti[0x24..]);
            var height = BinaryPrimitives.ReadInt32LittleEndian(brti[0x28..]);
            var layers = BinaryPrimitives.ReadUInt32LittleEndian(brti[0x30..]);
            var blockHeight = BinaryPrimitives.ReadUInt32LittleEndian(brti[0x34..]);
            var levels = BinaryPrimitives.ReadUInt64LittleEndian(brti[0x70..]);
            var kind = (int)(format >> 8);
            if (kind is < 0x2D or > 0x3A || brti[0x11] != 2 || layers != 1 || width is < 1 or > MaxSide || height is < 1 or > MaxSide
                || blockHeight > 5 || !Fits(bntx, levels, 8))
            {
                return null;
            }
            var level0 = BinaryPrimitives.ReadUInt64LittleEndian(bntx[(int)levels..]);
            if (level0 >= (ulong)bntx.Length)
            {
                return null;
            }
            var (blockW, blockH) = AstcBlocks[kind - 0x2D];
            var rowBytes = (width + blockW - 1) / blockW * 16;
            var rows = (height + blockH - 1) / blockH;
            var blocks = BlockLinear.ToLinear(bntx[(int)level0..], rowBytes, rows, (int)blockHeight, switch2);
            if (blocks is null)
            {
                return null;
            }
            AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(blocks, width, height, blockW, blockH, out var rgba);
            return rgba.Length == width * height * 4 ? new RgbaImage(width, height, rgba) : null;
        }
        return null;
    }

    private static bool Fits(ReadOnlySpan<byte> file, ulong offset, int length) => offset <= (ulong)file.Length && (ulong)length <= (ulong)file.Length - offset;
}

/// <summary>
/// The console GPU's "block linear" texture storage, undone: a level is stored as GOBs (64 bytes by 8
/// rows, 512 bytes), stacked 2^n high into blocks that fill the level row by row
/// (public descriptions: deko3d, Ryujinx's BlockLinearLayout). Inside a GOB the handheld console orders the
/// bytes differently from the Switch (it swaps two bits of the offset; mkw-recomp's
/// runtime/gfx/nvn/tex/tex_deswizzle.c documents both).
/// </summary>
internal static class BlockLinear
{
    /// <summary>Where the byte (<paramref name="x"/>, <paramref name="y"/>) of a level is in its block-linear storage.</summary>
    public static long Offset(int x, int y, int rowBytes, int blockHeightLog2, bool switch2)
    {
        var blockHeight = 1 << blockHeightLog2;
        long gobsWide = (rowBytes + 63) / 64;
        long block = 512L * blockHeight;
        var inGob = switch2
            ? (x % 64 / 32 * 256) + (y % 8 / 4 * 128) + (x % 32 / 16 * 64) + (y % 4 / 2 * 32) + (y % 2 * 16) + (x % 16)
            : (x % 64 / 32 * 256) + (y % 8 / 2 * 64) + (x % 32 / 16 * 32) + (y % 2 * 16) + (x % 16);
        return (y / (8 * blockHeight) * gobsWide * block) + (x / 64 * block) + (y % (8 * blockHeight) / 8 * 512L) + inGob;
    }

    /// <summary><paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes in reading order; null when the storage is too short for them.</summary>
    public static byte[]? ToLinear(ReadOnlySpan<byte> storage, int rowBytes, int rows, int blockHeightLog2, bool switch2)
    {
        var linear = new byte[rowBytes * rows];
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < rowBytes; x += 16) // 16 bytes in a row are together in the storage too
            {
                var from = Offset(x, y, rowBytes, blockHeightLog2, switch2);
                var length = Math.Min(16, rowBytes - x);
                if (from + length > storage.Length)
                {
                    return null;
                }
                storage.Slice((int)from, length).CopyTo(linear.AsSpan((y * rowBytes) + x));
            }
        }
        return linear;
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Buffers.Binary;
using System.Text;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// A read-only look into a <c>.nup</c> package (the dump format that packs a title's exefs/ and
/// romfs/ into one file), enough to take single files out of its RomFS. The layout is the one
/// mkw-recomp reads (its tools/prepare/nup.py and runtime/sdk/fs/nup.c):
/// <code>
/// 0x00  "NUP\x01"; u32 at 0x08: where the table of contents is
/// TOC   "TOC\x01"; u32 titles at 0x04; u64 dirs, files, pool offset, pool size at 0x08
///       0x30  titles, 0x40 each: u64 title id, u32 root dir, exefs dir, romfs dir, version
///       then  dirs, 24 each:  u32 name offset, name length, first child, child count
///       then  files, 80 each: u32 name offset, name length; u64 size, offset, stored size; 32-byte hash; u8 codec (0 raw, 1 zstd)
///       then  children, u32 each: a dir index, or the count of dirs plus a file index
///       pool  the names, UTF-8
/// </code>
/// A package can hold the base game and its updates as separate titles (ids that differ only in
/// their low 12 bits): a RomFS path is read from the newest title that has it. Titles of another
/// game (DLC: a higher id) are left out. Nothing is ever written.
/// </summary>
internal sealed class NupPackage
{
    /// <summary>The table of a real dump is some 30 MiB (200 000 files); more than this is not a table.</summary>
    private const long MaxTable = 512L << 20;

    private static readonly byte[] Magic = "NUP\u0001"u8.ToArray();
    private static readonly byte[] TocMagic = "TOC\u0001"u8.ToArray();

    private readonly string _path;
    private readonly byte[] _table;
    private readonly long _dirCount;
    private readonly long _fileCount;
    private readonly long _pool;
    private readonly long _dirs;
    private readonly long _files;
    private readonly long _children;

    /// <summary>The romfs dir of each title of the game, newest version first.</summary>
    private readonly List<uint> _romfs = [];

    private NupPackage(string path, byte[] table, uint titles, long dirCount, long fileCount, long pool)
    {
        (_path, _table, _dirCount, _fileCount, _pool) = (path, table, dirCount, fileCount, pool);
        _dirs = 0x30 + (titles * 0x40L);
        _files = _dirs + (dirCount * 24);
        _children = _files + (fileCount * 80);
        var all = new List<(ulong Id, uint Romfs, uint Version)>();
        for (var i = 0; i < titles; i++)
        {
            var at = table.AsSpan(0x30 + (i * 0x40));
            all.Add((BinaryPrimitives.ReadUInt64LittleEndian(at), BinaryPrimitives.ReadUInt32LittleEndian(at[16..]), BinaryPrimitives.ReadUInt32LittleEndian(at[20..])));
        }
        if (all.Count > 0)
        {
            var game = all.Min(t => t.Id & ~0xFFFUL);
            _romfs.AddRange(all.Where(t => (t.Id & ~0xFFFUL) == game).OrderByDescending(t => t.Version).Select(t => t.Romfs));
        }
    }

    public static bool IsNup(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[4];
            return file.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && head.SequenceEqual(Magic);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Reads the package's table; null when the file is not a package this reader understands (never throws for a bad file).</summary>
    public static NupPackage? Open(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[12];
            if (file.ReadAtLeast(head, 12, throwOnEndOfStream: false) != 12 || !head[..4].SequenceEqual(Magic))
            {
                return null;
            }
            long toc = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
            Span<byte> tocHead = stackalloc byte[0x30];
            file.Position = toc;
            if (file.ReadAtLeast(tocHead, 0x30, throwOnEndOfStream: false) != 0x30 || !tocHead[..4].SequenceEqual(TocMagic))
            {
                return null;
            }
            var titles = BinaryPrimitives.ReadUInt32LittleEndian(tocHead[4..]);
            var dirs = BinaryPrimitives.ReadUInt64LittleEndian(tocHead[8..]);
            var files = BinaryPrimitives.ReadUInt64LittleEndian(tocHead[16..]);
            var pool = BinaryPrimitives.ReadUInt64LittleEndian(tocHead[24..]);
            var poolSize = BinaryPrimitives.ReadUInt64LittleEndian(tocHead[32..]);
            if (titles > 4096 || dirs > 50_000_000 || files > 50_000_000 || pool > MaxTable || poolSize > MaxTable || pool + poolSize > MaxTable
                || toc + (long)(pool + poolSize) > file.Length)
            {
                return null;
            }
            // The fixed parts must fit before the pool.
            if (0x30 + (titles * 0x40UL) + (dirs * 24) + (files * 80) > pool)
            {
                return null;
            }
            var table = new byte[(int)(pool + poolSize)];
            file.Position = toc;
            file.ReadExactly(table);
            return new NupPackage(path, table, titles, (long)dirs, (long)files, (long)pool);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// One RomFS file, from the newest title that has it: <paramref name="folder"/> is a path of folders
    /// ("Layout"), the file is the first in it whose name <paramref name="isIt"/> takes. Null when there is
    /// none, when it is larger than <paramref name="maxBytes"/>, or when it cannot be read.
    /// </summary>
    public byte[]? ReadRomfs(string folder, Func<string, bool> isIt, int maxBytes)
    {
        foreach (var top in _romfs)
        {
            long dir = top;
            foreach (var part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                dir = dir < 0 ? -1 : ChildDir(dir, part);
            }
            if (dir < 0 || dir >= _dirCount)
            {
                continue;
            }
            foreach (var child in Children(dir))
            {
                if (child >= _dirCount && child - _dirCount < _fileCount && isIt(Name(_files + ((child - _dirCount) * 80))))
                {
                    return Read(_files + ((child - _dirCount) * 80), maxBytes);
                }
            }
        }
        return null;
    }

    private long ChildDir(long dir, string name)
    {
        if (dir >= _dirCount)
        {
            return -1; // a title without this part names a dir past the table
        }
        foreach (var child in Children(dir))
        {
            if (child < _dirCount && Name(_dirs + (child * 24)) == name)
            {
                return child;
            }
        }
        return -1;
    }

    private List<long> Children(long dir)
    {
        var record = _table.AsSpan((int)(_dirs + (dir * 24)));
        long first = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
        long count = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var result = new List<long>();
        for (var k = first; k < first + count && _children + ((k + 1) * 4) <= _pool; k++)
        {
            result.Add(BinaryPrimitives.ReadUInt32LittleEndian(_table.AsSpan((int)(_children + (k * 4)))));
        }
        return result;
    }

    private string Name(long record)
    {
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(_table.AsSpan((int)record));
        long length = BinaryPrimitives.ReadUInt32LittleEndian(_table.AsSpan((int)record + 4));
        return _pool + offset + length <= _table.Length ? Encoding.UTF8.GetString(_table, (int)(_pool + offset), (int)length) : "";
    }

    private byte[]? Read(long record, int maxBytes)
    {
        var at = _table.AsSpan((int)record);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(at[8..]);
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(at[16..]);
        var stored = BinaryPrimitives.ReadUInt64LittleEndian(at[24..]);
        var codec = at[0x40];
        if (size > (ulong)maxBytes || stored > (ulong)maxBytes || codec > 1 || (codec == 0 && stored != size))
        {
            return null;
        }
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset + stored > (ulong)file.Length)
            {
                return null;
            }
            var data = new byte[(int)stored];
            file.Position = (long)offset;
            file.ReadExactly(data);
            if (codec == 0)
            {
                return data;
            }
            var plain = Zstd.Unpack(data, (int)size); // the package's own codec (frames written with zstd --long)
            return plain?.Length == (int)size ? plain : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Zstandard, as the game's files (<c>*.zs</c>) and the packages use it: one frame, no dictionary.</summary>
internal static class Zstd
{
    /// <summary>The whole frame; null when it is not zstd or would be larger than <paramref name="maxBytes"/>.</summary>
    public static byte[]? Unpack(ReadOnlySpan<byte> packed, int maxBytes)
    {
        try
        {
            using var decoder = new Decompressor();
            decoder.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 31);
            var size = Decompressor.GetDecompressedSize(packed);
            if (size > (ulong)maxBytes)
            {
                return null; // also "unknown" (a frame without its size): the game's files always say it
            }
            var plain = new byte[(int)size];
            return decoder.Unwrap(packed, plain) == plain.Length ? plain : null;
        }
        catch (ZstdException)
        {
            return null;
        }
    }
}

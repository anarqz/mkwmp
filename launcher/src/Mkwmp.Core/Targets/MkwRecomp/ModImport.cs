// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// Puts a mod into an mkw-recomp install: what the game's own importer does (tools/mod/import.py there;
/// launcher/docs/MODS.md compares the two), in the launcher's own code so nothing of the archive is
/// trusted on the way. An archive (or a folder) holds the mod as GameBanana has it: <c>romfs/</c>,
/// <c>plugin/</c> and/or <c>exefs/</c> at the root, under one folder, deeper, or under
/// <c>atmosphere/contents/&lt;title id&gt;/</c>; an archive with several is a choice (<see cref="ModInstallCode.Variants"/>).
/// <list type="bullet">
/// <item>It is unpacked into a staging folder beside the mods (never over anything), with limits
/// (<see cref="MaxBytes"/>, <see cref="MaxFiles"/>), every name checked: nothing absolute, no <c>..</c>, no
/// drive or stream, no link, no name Windows would bend into another.</item>
/// <item>The mod becomes <c>mods/&lt;id&gt;/</c> with the names the game reads (<c>romfs</c>, <c>plugin</c>),
/// a <c>romfs/manifest.tsv</c> (each file's size and SHA-256, and the SHA-256 of the game's own file it
/// replaces: the game checks all three when it starts) and a <c>mod.json</c> the game's own Mods page shows.</item>
/// <item>A mod that patches the game's code (<c>exefs/</c>, or a port's <c>patches</c> / <c>switches</c>) is
/// refused: the game would have to be built again, which is its own start screen's work.</item>
/// <item>A mod already there is replaced only when asked; its old folder is kept in <c>mods/.mkwmp-backup/</c>.</item>
/// </list>
/// </summary>
internal static class ModImport
{
    /// <summary>The game's importer's own limits: the largest mods are a few hundred MB.</summary>
    public const long MaxBytes = 4L << 30;
    public const int MaxFiles = 50_000;

    /// <summary>The limits in use (the tests lower them).</summary>
    internal static long LimitBytes { get; set; } = MaxBytes;
    internal static int LimitFiles { get; set; } = MaxFiles;

    private const int MaxDepth = 24;
    private const int MaxPath = 200;
    private static readonly string[] Parts = ["romfs", "exefs", "plugin"];
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>A refusal on the way: its code and what the player reads.</summary>
    private sealed class Refused(ModInstallCode code, string message) : Exception(message)
    {
        public ModInstallCode Code { get; } = code;
    }

    /// <summary>A test's stand-in for the system's tar (7z and rar); null: <c>%SystemRoot%\System32\tar.exe</c>.</summary>
    internal static string? TarOverride { get; set; }

    public static ModInstallResult Install(IGameTarget target, GameInstall install, ModInstallRequest request, IProgress<ModProgress>? progress, CancellationToken cancel, Action<string>? note = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(request);
        if (!RecompMods.Capable(install))
        {
            return new(ModInstallCode.NoGame, Strings.ModsNone(target.DisplayName));
        }
        if (target.IsRunning(install))
        {
            return new(ModInstallCode.GameRunning, Strings.ModsCloseGame);
        }
        var mods = RecompMods.ModsDir(install);
        var staging = Path.Combine(mods, RecompMods.Staging, Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(staging);
            return Run(target, install, request, mods, staging, progress, note, cancel);
        }
        catch (Refused e)
        {
            return new(e.Code, e.Message);
        }
        catch (OperationCanceledException)
        {
            return new(ModInstallCode.Cancelled, Strings.ModsCancelled);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return new(ModInstallCode.Io, Strings.ModsFailed(e.Message));
        }
        finally
        {
            Drop(Path.Combine(mods, RecompMods.Staging));
        }
    }

    private static ModInstallResult Run(IGameTarget target, GameInstall install, ModInstallRequest request, string mods, string staging, IProgress<ModProgress>? progress, Action<string>? note, CancellationToken cancel)
    {
        // 1. the mod's files, in the staging folder
        string top;
        string stem;
        var unpacked = Path.Combine(staging, "x");
        if (Directory.Exists(request.Path))
        {
            stem = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Path)));
            top = request.Path;
        }
        else if (File.Exists(request.Path))
        {
            stem = Path.GetFileNameWithoutExtension(request.Path);
            Directory.CreateDirectory(unpacked);
            Unpack(request.Path, unpacked, progress, cancel);
            top = unpacked;
        }
        else
        {
            throw new Refused(ModInstallCode.Io, Strings.ModsFailed(Strings.ModsNoSuchFile(request.Path)));
        }

        // 2. the mod inside: the folders that hold romfs/, exefs/ or plugin/
        var roots = Roots(top);
        if (request.Variant is { Length: > 0 } variant)
        {
            roots = [.. roots.Where(r => Rel(top, r) == variant)];
        }
        if (roots.Count == 0)
        {
            throw new Refused(ModInstallCode.Layout, Strings.ModsLayout);
        }
        if (roots.Count > 1)
        {
            return new(ModInstallCode.Variants, Strings.ModsVariants, Variants: [.. roots.Select(r => Rel(top, r))]);
        }
        var source = roots[0];
        var relative = Rel(top, source);
        var last = relative.Split('/')[^1];
        var own = relative == "." || last.Equals("contents", StringComparison.OrdinalIgnoreCase) || last.Equals("0400c3f00006e000", StringComparison.OrdinalIgnoreCase) ? stem : last;

        // a folder the player picked is copied in (bounded, no links); an archive's is already staged
        var staged = Path.Combine(staging, "mod");
        if (top == unpacked)
        {
            Directory.Move(source, staged);
        }
        else
        {
            CopyTree(source, staged, progress, cancel);
        }
        foreach (var part in Parts)
        {
            // RomFS/, Plugin/ spelled otherwise: under the names the game reads
            if (Directory.EnumerateDirectories(staged).Select(Path.GetFileName).FirstOrDefault(d => d!.Equals(part, StringComparison.OrdinalIgnoreCase)) is { } found && found != part)
            {
                Directory.Move(Path.Combine(staged, found), Path.Combine(staged, part + ".mkwmp"));
                Directory.Move(Path.Combine(staged, part + ".mkwmp"), Path.Combine(staged, part));
            }
        }
        var hasRomfs = Directory.Exists(Path.Combine(staged, "romfs"));
        var hasPlugin = Directory.Exists(Path.Combine(staged, "plugin")) && Directory.EnumerateFiles(Path.Combine(staged, "plugin")).Any();
        var shipped = RecompMods.Manifest.Read(staged, "");
        if (Directory.Exists(Path.Combine(staged, "exefs")) || shipped.Code)
        {
            throw new Refused(ModInstallCode.ChangesCode, Strings.ModsCodeInstall);
        }
        if (!hasRomfs && !hasPlugin)
        {
            throw new Refused(ModInstallCode.Layout, Strings.ModsLayout);
        }

        // 3. its name in the game
        var wanted = request.Name is { Length: > 0 } asked ? asked : own;
        var id = CleanName(wanted);
        if (id.Length == 0)
        {
            throw new Refused(ModInstallCode.Name, Strings.ModsName);
        }
        if (id.Equals(Recomp.Mod, StringComparison.OrdinalIgnoreCase))
        {
            throw new Refused(ModInstallCode.Managed, Strings.ModsManaged);
        }
        var destination = Path.Combine(mods, id);
        var exists = Directory.Exists(destination) || File.Exists(destination);
        if (exists && !request.Replace)
        {
            return new(ModInstallCode.Exists, Strings.ModsExists(id), id, HasPlugin: hasPlugin);
        }
        if (!exists && Directory.EnumerateDirectories(mods).Select(Path.GetFileName).Count(RecompMods.ValidId) >= RecompMods.MaxMods)
        {
            throw new Refused(ModInstallCode.TooBig, Strings.ModsTooMany(RecompMods.MaxMods));
        }

        // 4. what the game reads: the file list of its romfs, and mod.json
        var (files, warnings) = hasRomfs ? WriteManifest(install, Path.Combine(staged, "romfs"), progress, cancel) : (0, []);
        var title = request.Name is { Length: > 0 } ? Short(request.Name, 96) : shipped.Title.Length > 0 && own == id ? shipped.Title : Short(wanted, 96);
        WriteModJson(staged, id, title, shipped, hasRomfs, files, hasPlugin, warnings);
        if (request.Source is { } from)
        {
            RecompMods.WriteSource(staged, from, DateTimeOffset.UtcNow);
        }
        else
        {
            File.Delete(Path.Combine(staged, RecompMods.SourceFile)); // an archive does not say where it came from
        }

        // 5. in place: the old folder (an update) steps aside first, and comes back when the new one does not arrive
        cancel.ThrowIfCancellationRequested();
        progress?.Report(new ModProgress("install", 0, 0));
        string? backup = null;
        if (exists)
        {
            backup = Path.Combine(mods, RecompMods.Backups, id);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            Drop(backup);
            Directory.Move(destination, backup);
        }
        try
        {
            Directory.Move(staged, destination);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (backup is not null && !Directory.Exists(destination))
            {
                Directory.Move(backup, destination);
            }
            throw;
        }
        note?.Invoke($"mods: installed {id} in {destination} ({files} game files{(hasPlugin ? ", a plugin" : "")}{(backup is null ? "" : "; the version before it is in " + backup)})");

        // 6. on: appended to the list, as the game's importer does; a mod with code only when the player said yes
        var enabled = RecompMods.Enabled(install).Mods.Contains(id, StringComparer.OrdinalIgnoreCase);
        if (!enabled && request.Enable && (!hasPlugin || request.AcceptCode))
        {
            RecompMods.SetEnabled(target, install, id, on: true, acceptCode: true, note);
            enabled = true;
        }
        else
        {
            RecompMods.Remerge(install, note); // its files changed under a merge made from the old ones
        }
        return new(ModInstallCode.Done, Strings.ModsInstalled(title), id, HasPlugin: hasPlugin, Enabled: enabled);
    }

    // ---- names ------------------------------------------------------------------------------

    /// <summary>
    /// The game's importer's clean_name, narrower: letters, digits, '-', '_' and spaces of ASCII (mkw.ini
    /// is not Unicode all the way), the rest '_'; no leading or trailing dots, underscores or spaces; at most 63 bytes.
    /// </summary>
    internal static string CleanName(string name)
    {
        var keep = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')]).Trim().Trim('.', '_', ' ');
        while (keep.Contains("__", StringComparison.Ordinal))
        {
            keep = keep.Replace("__", "_", StringComparison.Ordinal);
        }
        return (keep.Length > RecompMods.MaxName ? keep[..RecompMods.MaxName] : keep).Trim('.', '_', ' ');
    }

    private static string Short(string text, int most)
    {
        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length > most ? clean[..most] : clean;
    }

    private static string Rel(string top, string folder)
    {
        var rel = Path.GetRelativePath(top, folder).Replace('\\', '/');
        return rel.Length == 0 ? "." : rel;
    }

    /// <summary>One name of an archive's path, as it may be written on this PC; null when it may not.</summary>
    internal static string? SafePath(string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > MaxDepth || name.Length > MaxPath || name.StartsWith('/') || name.StartsWith('\\'))
        {
            return null;
        }
        foreach (var p in parts)
        {
            if (p is "." or ".." || p.Length > 120 || p.EndsWith('.') || p.EndsWith(' ') || p.StartsWith(' ')
                || p.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 || p.Any(char.IsControl)
                || Reserved.Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return string.Join('/', parts);
    }

    private static bool Skipped(string path) =>
        path.Split('/').Any(p => p == "__MACOSX" || p == ".DS_Store" || p.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || p.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));

    // ---- unpacking --------------------------------------------------------------------------

    private static void Unpack(string archive, string into, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        Span<byte> head = stackalloc byte[8];
        int read;
        using (var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        var magic = head[..read];
        if (magic.StartsWith("PK"u8))
        {
            Unzip(archive, into, progress, cancel);
        }
        else if (magic.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]) || magic.StartsWith("Rar!"u8))
        {
            Untar(archive, into, progress, cancel);
        }
        else
        {
            throw new Refused(ModInstallCode.Unpack, Strings.ModsUnpack(Strings.ModsNotAnArchive));
        }
    }

    private static void Unzip(string archive, string into, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(archive);
        }
        catch (InvalidDataException e)
        {
            throw new Refused(ModInstallCode.Unpack, Strings.ModsUnpack(e.Message));
        }
        using (zip)
        {
            var entries = new List<(ZipArchiveEntry Entry, string Path)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/'))
                {
                    continue; // a folder
                }
                // a link (Unix mode S_IFLNK in the high word) would be written as a file holding its target: not a mod's file
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    throw new Refused(ModInstallCode.Unsafe, Strings.ModsUnsafe(Strings.ModsUnsafeLink(name)));
                }
                if (SafePath(name) is not { } path)
                {
                    throw new Refused(ModInstallCode.Unsafe, Strings.ModsUnsafe(Strings.ModsUnsafeName(Printable(name))));
                }
                if (Skipped(path))
                {
                    continue;
                }
                if (!names.Add(path))
                {
                    throw new Refused(ModInstallCode.Unsafe, Strings.ModsUnsafe(Strings.ModsUnsafeTwice(path)));
                }
                total += entry.Length;
                entries.Add((entry, path));
                if (entries.Count > LimitFiles || total > LimitBytes)
                {
                    throw new Refused(ModInstallCode.TooBig, Strings.ModsTooBig(entries.Count, total));
                }
            }
            long done = 0;
            var buffer = new byte[1 << 16];
            foreach (var (entry, path) in entries)
            {
                cancel.ThrowIfCancellationRequested();
                var to = Path.Combine(into, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                using var from = entry.Open();
                using var file = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long written = 0;
                int n;
                while ((n = from.Read(buffer)) > 0)
                {
                    // the size an entry declares is only its word: what comes out is counted
                    if ((written += n) > entry.Length || (done += n) > LimitBytes)
                    {
                        throw new Refused(ModInstallCode.TooBig, Strings.ModsTooBig(entries.Count, done));
                    }
                    file.Write(buffer, 0, n);
                    cancel.ThrowIfCancellationRequested();
                }
                progress?.Report(new ModProgress("unpack", done, total));
            }
        }
    }

    /// <summary>
    /// .7z and .rar, the way the game's importer reads them: the system's tar (bsdtar: tar.exe of Windows
    /// 10 and later). The list is checked first; what was unpacked is checked again (no link, the limits).
    /// </summary>
    private static void Untar(string archive, string into, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        var tar = TarOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar))
        {
            throw new Refused(ModInstallCode.Unpack, Strings.ModsUnpack(Strings.ModsNoTar));
        }
        var (code, listing) = Tar(tar, ["-tf", archive], cancel);
        if (code != 0)
        {
            throw new Refused(ModInstallCode.Unpack, Strings.ModsUnpack(Strings.ModsNotAnArchive));
        }
        var names = listing.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.EndsWith('/')).ToList();
        if (names.Count > LimitFiles)
        {
            throw new Refused(ModInstallCode.TooBig, Strings.ModsTooBig(names.Count, 0));
        }
        if (names.FirstOrDefault(n => SafePath(n) is null) is { } bad)
        {
            throw new Refused(ModInstallCode.Unsafe, Strings.ModsUnsafe(Strings.ModsUnsafeName(Printable(bad))));
        }
        progress?.Report(new ModProgress("unpack", 0, 0));
        if (Tar(tar, ["-xf", archive, "-C", into], cancel).Code != 0)
        {
            throw new Refused(ModInstallCode.Unpack, Strings.ModsUnpack(Strings.ModsNotAnArchive));
        }
        long total = 0;
        var count = 0;
        foreach (var entry in new DirectoryInfo(into).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new Refused(ModInstallCode.Unsafe, Strings.ModsUnsafe(Strings.ModsUnsafeLink(entry.Name)));
            }
            if (entry is FileInfo file && ((total += file.Length) > LimitBytes || ++count > LimitFiles))
            {
                throw new Refused(ModInstallCode.TooBig, Strings.ModsTooBig(count, total));
            }
        }
    }

    private static (int Code, string Output) Tar(string tar, string[] arguments, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(tar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(start) ?? throw new Refused(ModInstallCode.Unpack, Strings.ModsUnpack(Strings.ModsNoTar));
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var output = process.StandardOutput.ReadToEndAsync(cancel);
            using var stop = cancel.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }
            });
            if (!process.WaitForExit(TimeSpan.FromMinutes(15)))
            {
                process.Kill(entireProcessTree: true);
                throw new Refused(ModInstallCode.Unpack, Strings.ModsUnpack(Strings.ModsNotAnArchive));
            }
            cancel.ThrowIfCancellationRequested();
            return (process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new Refused(ModInstallCode.Unpack, Strings.ModsUnpack(e.Message));
        }
    }

    private static string Printable(string name) => new([.. name.Take(80).Select(c => char.IsControl(c) ? '?' : c)]);

    // ---- the mod inside ---------------------------------------------------------------------

    /// <summary>
    /// The folders under <paramref name="top"/> that hold romfs/, exefs/ or plugin/ (any letter case),
    /// shallowest first; dot folders and macOS' __MACOSX skipped. One per mod; a mod's own folders are not looked into.
    /// </summary>
    internal static List<string> Roots(string top)
    {
        var roots = new List<string>();
        var queue = new Queue<(string Folder, int Depth)>();
        queue.Enqueue((top, 0));
        var seen = 0;
        while (queue.Count > 0 && seen++ < 20_000)
        {
            var (folder, depth) = queue.Dequeue();
            List<string> children;
            try
            {
                children = [.. new DirectoryInfo(folder).EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint) && !d.Name.StartsWith('.') && d.Name != "__MACOSX")
                    .Select(d => d.FullName).OrderBy(d => d, StringComparer.Ordinal)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (children.Any(c => Parts.Contains(Path.GetFileName(c), StringComparer.OrdinalIgnoreCase)))
            {
                roots.Add(folder);
                continue;
            }
            if (depth < 8)
            {
                foreach (var child in children)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
        return roots;
    }

    /// <summary>A picked folder's mod, copied: the same limits and name rules as an archive, and no link followed or copied.</summary>
    private static void CopyTree(string from, string to, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        long total = 0;
        var count = 0;
        var root = new DirectoryInfo(from);
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new Refused(ModInstallCode.Unsafe, Strings.ModsUnsafe(Strings.ModsUnsafeLink(root.Name)));
        }
        Directory.CreateDirectory(to);
        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            cancel.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(from, entry.FullName).Replace('\\', '/');
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new Refused(ModInstallCode.Unsafe, Strings.ModsUnsafe(Strings.ModsUnsafeLink(rel)));
            }
            if (Skipped(rel) || entry is not FileInfo file)
            {
                continue;
            }
            if (SafePath(rel) is not { } path)
            {
                throw new Refused(ModInstallCode.Unsafe, Strings.ModsUnsafe(Strings.ModsUnsafeName(Printable(rel))));
            }
            if ((total += file.Length) > LimitBytes || ++count > LimitFiles)
            {
                throw new Refused(ModInstallCode.TooBig, Strings.ModsTooBig(count, total));
            }
            var target = Path.Combine(to, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            file.CopyTo(target, overwrite: false);
            File.SetAttributes(target, FileAttributes.Normal);
            progress?.Report(new ModProgress("unpack", total, 0));
        }
    }

    // ---- what the game reads ----------------------------------------------------------------

    /// <summary>romfs/manifest.tsv: <c>path, size, sha256, the original's sha256 or "-" for a new file</c>, a line per file.</summary>
    private static (int Files, List<string> Warnings) WriteManifest(GameInstall install, string romfs, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        var original = GameRomfs.Open(install) ?? throw new Refused(ModInstallCode.GameFiles, Strings.ModsGameFiles(Strings.ModsGameFilesNone));
        var files = Directory.EnumerateFiles(romfs, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(romfs, f).Replace('\\', '/'))).Where(f => f.Rel != "manifest.tsv").OrderBy(f => f.Rel, StringComparer.Ordinal).ToList();
        var total = files.Sum(f => new FileInfo(f.Full).Length);
        long done = 0;
        var lines = new StringBuilder();
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (full, rel) in files)
        {
            cancel.ThrowIfCancellationRequested();
            if (rel.StartsWith("Shader/", StringComparison.Ordinal))
            {
                warnings.Add("shaders");
            }
            else if (rel.StartsWith("System/GymlArchive/general_format", StringComparison.Ordinal))
            {
                warnings.Add("graphics");
            }
            string sha;
            long size;
            using (var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                size = file.Length;
                sha = Convert.ToHexStringLower(SHA256.HashData(file));
            }
            var (found, hash) = original(rel);
            if (found && hash is null)
            {
                throw new Refused(ModInstallCode.GameFiles, Strings.ModsGameFiles(rel));
            }
            lines.Append(rel).Append('\t').Append(size).Append('\t').Append(sha).Append('\t').Append(found ? hash : "-").Append('\n');
            progress?.Report(new ModProgress("check", done += size, total));
        }
        File.WriteAllText(Path.Combine(romfs, "manifest.tsv"), lines.ToString(), new UTF8Encoding(false));
        return (files.Count, [.. warnings]);
    }

    /// <summary>mod.json as the game's importer writes it (its Mods page reads these fields), with the mod's own version, author and description kept.</summary>
    private static void WriteModJson(string folder, string id, string title, RecompMods.Manifest shipped, bool hasRomfs, int files, bool hasPlugin, List<string> warnings)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("name", id);
            json.WriteString("title", title.Replace('"', '\'')); // the game's page reads this file with a scanner that stops at a quote
            json.WriteBoolean("has_romfs", hasRomfs);
            json.WriteNumber("romfs_files", files);
            json.WriteBoolean("has_exefs", false);
            json.WriteBoolean("has_plugin", hasPlugin);
            json.WriteStartArray("exefs_builds");
            json.WriteEndArray();
            json.WriteBoolean("exefs_match", true);
            json.WriteStartObject("switches");
            json.WriteEndObject();
            json.WriteStartArray("warnings");
            foreach (var warning in warnings)
            {
                json.WriteStringValue(warning);
            }
            json.WriteEndArray();
            if (shipped.Version.Length > 0)
            {
                json.WriteString("version", shipped.Version);
            }
            if (shipped.Author.Length > 0)
            {
                json.WriteString("author", shipped.Author);
            }
            if (shipped.Description.Length > 0)
            {
                json.WriteString("description", shipped.Description);
            }
            json.WriteEndObject();
        }
        File.WriteAllBytes(Path.Combine(folder, RecompMods.ManifestFile), [.. stream.ToArray(), (byte)'\n']);
    }

    private static void Drop(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left behind: a dot folder of mods/, which the game does not read; cleared by the next install
        }
    }
}

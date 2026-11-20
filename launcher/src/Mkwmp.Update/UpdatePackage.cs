// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;

namespace Mkwmp.Update;

/// <summary>
/// The files of a release package and where each goes in the launcher's folder. A release zip
/// (tools/release/make_zips.py) holds everything under <c>mkwmp/</c>; that folder is the launcher's.
/// </summary>
public static class UpdatePackage
{
    public const string TopFolder = "mkwmp/";
    public const int MaxFiles = 512;
    public const long MaxBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// The player's own files in the launcher's folder, which no release may replace or remove:
    /// the settings (any <c>.toml</c> at the top), <c>logs/</c>, and the staging folder itself.
    /// </summary>
    public static bool IsPlayerData(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(UpdatePlan.StoreFolder + "/", StringComparison.OrdinalIgnoreCase)
            || (!path.Contains('/') && path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A relative path with <c>/</c>, made of plain names: nothing absolute, no <c>..</c>, no drive, no stream.</summary>
    public static bool SafePath(string? path)
    {
        if (path is not { Length: > 0 and <= 240 } || path.Contains('\\'))
        {
            return false;
        }
        var parts = path.Split('/');
        return parts.Length <= 12 && parts.All(UpdatePlan.ValidFileName);
    }

    /// <summary>
    /// Where each file entry of the zip is installed (relative, with <c>/</c>), in the zip's order.
    /// The launcher's executable is installed under the name it has here (<paramref name="launcher"/>).
    /// Player data in a zip is skipped; an unsafe path, a duplicate or an oversized package throws.
    /// </summary>
    public static IReadOnlyList<(ZipArchiveEntry Entry, string Path)> Files(ZipArchive zip, string launcher)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToArray();
        if (entries.Length is 0 or > MaxFiles)
        {
            throw new UpdateException(entries.Length == 0 ? "the update package is empty" : "the update package has too many files");
        }
        var strip = entries.All(e => e.FullName.StartsWith(TopFolder, StringComparison.Ordinal)) ? TopFolder.Length : 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(ZipArchiveEntry, string)>();
        long total = 0;
        foreach (var entry in entries)
        {
            var path = entry.FullName[strip..];
            if (!SafePath(path))
            {
                throw new UpdateException($"the update package has an unsafe path: {entry.FullName}");
            }
            if (IsPlayerData(path))
            {
                continue;
            }
            if (path.Equals(UpdatePlan.LauncherName, StringComparison.OrdinalIgnoreCase))
            {
                path = launcher;
            }
            if (!seen.Add(path))
            {
                throw new UpdateException($"the update package has {path} twice");
            }
            total = checked(total + entry.Length);
            if (total > MaxBytes)
            {
                throw new UpdateException("the update package is too large");
            }
            files.Add((entry, path));
        }
        if (!seen.Contains(launcher))
        {
            throw new UpdateException($"the update package has no {UpdatePlan.LauncherName}");
        }
        return files;
    }

    /// <summary>
    /// Copies the release's own helper out of a zip (already checked against SHA256SUMS) to
    /// <paramref name="destination"/>; false when the release ships none.
    /// </summary>
    public static bool ExtractHelper(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(TopFolder + UpdatePlan.HelperName) ?? zip.GetEntry(UpdatePlan.HelperName);
        if (entry is null || entry.Length is <= 0 or > 256L * 1024 * 1024)
        {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, overwrite: true);
        return true;
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;
using Mkwmp.Update;
using NSec.Cryptography;

namespace Mkwmp.Core.Updates;

/// <summary>A release downloaded into the staging folder and checked against its signed SHA256SUMS: ready to install.</summary>
/// <param name="Kind"><see cref="UpdatePlan.KindZip"/>.</param>
/// <param name="Asset">The package's name in SHA256SUMS.</param>
/// <param name="Helper">The helper to run (<c>worker.exe</c> in the staging folder), or null: this release and this install have none.</param>
internal sealed record StagedUpdate(string Root, string Version, string Kind, string Asset, string? Notes, string? Helper)
{
    public UpdatePlan Plan(string fromVersion, string launcherName, IReadOnlyList<string> arguments) => new()
    {
        Root = Root,
        Version = Version,
        FromVersion = fromVersion,
        Kind = Kind,
        Asset = Asset,
        Launcher = launcherName,
        Arguments = arguments,
    };
}

/// <summary>
/// The launcher's side of its own update (launcher/docs/UPDATES.md): the release is downloaded into
/// <c>&lt;launcher folder&gt;/.mkwmp-selfupdate/&lt;version&gt;/</c> (<see cref="UpdatePlan"/>), verified, and
/// left there until the player installs it, also across restarts. The swap itself is
/// <see cref="UpdateInstaller"/>'s, run by the helper (<c>updater.exe</c>).
/// </summary>
internal static class SelfUpdate
{
    /// <summary>Ed25519 with the launcher's own library (the helper has another; both check the same signature).</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        var ed = SignatureAlgorithm.Ed25519;
        return signature.Length == ed.SignatureSize && publicKey.Length == ed.PublicKeySize
            && PublicKey.TryImport(ed, publicKey, KeyBlobFormat.RawPublicKey, out var key) && key is not null && ed.Verify(key, message, signature);
    }

    /// <summary>
    /// Downloads <paramref name="release"/> into its staging folder, continuing a download that was
    /// cut short, and verifies it. The package is the release's zip (every file, the helper included):
    /// a release without it has nothing to install. Throws <see cref="LauncherException"/>.
    /// </summary>
    public static async Task<StagedUpdate> StageAsync(Updater updater, Release release, string root, IProgress<(long Got, long? Total)>? progress, CancellationToken cancel)
    {
        var version = release.Version?.ToString() ?? throw new LauncherException("release tag is not a version");
        try
        {
            var folder = UpdatePlan.TransactionFolder(root, version);
            Directory.CreateDirectory(folder);
            var sums = await updater.SumsAsync(release, cancel).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(folder, UpdatePlan.SumsName), sums.Bytes, cancel).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, UpdatePlan.SignatureName), sums.Signature, cancel).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, UpdatePlan.NotesName), release.Notes ?? "", cancel).ConfigureAwait(false);
            var zip = Updater.ZipAsset(version);
            if (!sums.Hashes.ContainsKey(zip) || !release.Has(zip))
            {
                throw new LauncherException($"release {release.Tag} has no {zip}");
            }
            var (kind, asset) = (UpdatePlan.KindZip, zip);
            var package = Path.Combine(folder, UpdatePlan.ZipName);
            var want = sums.Hashes[asset];
            if (!File.Exists(package) || await Task.Run(() => ReleaseSums.Sha256OfFile(package), cancel).ConfigureAwait(false) != want)
            {
                await updater.DownloadAsync(release.Url(asset), package, progress, cancel).ConfigureAwait(false);
                if (await Task.Run(() => ReleaseSums.Sha256OfFile(package), cancel).ConfigureAwait(false) != want)
                {
                    File.Delete(package); // nothing of it is kept: the next try starts clean
                    throw new LauncherException($"{asset}: checksum mismatch");
                }
            }
            return Describe(root, version, kind, asset, release.Notes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or UpdateException)
        {
            throw new LauncherException(e.Message, e);
        }
    }

    /// <summary>
    /// The newest verified download waiting in the staging folder that is newer than
    /// <paramref name="current"/> (or exactly <paramref name="version"/>), or null. Reads and hashes the package.
    /// </summary>
    public static StagedUpdate? FindStaged(string root, SemVer current, IReadOnlyList<string> keys, string? version = null)
    {
        try
        {
            var store = UpdatePlan.Store(root);
            if (!Directory.Exists(store))
            {
                return null;
            }
            foreach (var folder in Directory.EnumerateDirectories(store)
                .Select(d => (Folder: d, Version: SemVer.TryParse(Path.GetFileName(d))))
                .Where(d => d.Version is not null && d.Version > current && (version is null || d.Version.ToString() == version))
                .OrderByDescending(d => d.Version))
            {
                var name = folder.Version!.ToString();
                var (kind, asset) = (UpdatePlan.KindZip, Updater.ZipAsset(name));
                if (File.Exists(Path.Combine(folder.Folder, UpdatePlan.ZipName)))
                {
                    try
                    {
                        var notes = Path.Combine(folder.Folder, UpdatePlan.NotesName);
                        var staged = new StagedUpdate(root, name, kind, asset, File.Exists(notes) ? File.ReadAllText(notes) : null, null);
                        new UpdateInstaller(Verify, keys).VerifyStage(staged.Plan("", UpdatePlan.LauncherName, []));
                        return Describe(root, name, kind, asset, staged.Notes is { Length: > 0 } ? staged.Notes : null);
                    }
                    catch (UpdateException)
                    {
                        // not (or no longer) a verified download: it is downloaded again when asked for
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an unreadable staging folder holds nothing to install
        }
        return null;
    }

    /// <summary>
    /// The staged package as it will be installed: its list of files is checked now (so a bad zip
    /// fails here, with the launcher still open), and the helper is put in place as <c>worker.exe</c>:
    /// the new release's own, out of the verified zip, else the installed one.
    /// </summary>
    private static StagedUpdate Describe(string root, string version, string kind, string asset, string? notes)
    {
        var folder = UpdatePlan.TransactionFolder(root, version);
        var worker = Path.Combine(folder, UpdatePlan.WorkerName);
        var package = Path.Combine(folder, UpdatePlan.ZipName);
        using (var zip = ZipFile.OpenRead(package))
        {
            _ = UpdatePackage.Files(zip, UpdatePlan.LauncherName);
        }
        var has = UpdatePackage.ExtractHelper(package, worker);
        var installed = Path.Combine(root, UpdatePlan.HelperName);
        if (!has && File.Exists(installed))
        {
            File.Copy(installed, worker, overwrite: true);
            has = true;
        }
        return new StagedUpdate(root, version, kind, asset, notes, has ? worker : null);
    }

    /// <summary>
    /// Start-up: removes what earlier updates left (their staging folders up to this version, with the
    /// backup of the files they replaced and the helper's copy). A download of a newer version stays.
    /// </summary>
    public static void Clean(string root, SemVer current, Action<string> note)
    {
        try
        {
            var store = UpdatePlan.Store(root);
            if (!Directory.Exists(store))
            {
                return;
            }
            foreach (var folder in Directory.EnumerateDirectories(store))
            {
                if (SemVer.TryParse(Path.GetFileName(folder)) is { } version && version > current)
                {
                    continue;
                }
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    note($"update leftovers in {folder}: {e.Message} (removed at the next start)"); // the helper is still closing
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            note($"update leftovers: {e.Message}");
        }
    }
}

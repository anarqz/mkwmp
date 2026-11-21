// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Mkwmp.Update;

/// <summary>What the helper's window shows.</summary>
public enum UpdateStep
{
    Waiting,
    Verifying,
    Replacing,
    Starting,
    Failed,
}

/// <summary>
/// Installs a staged release (<see cref="UpdatePlan"/>) into the launcher's folder:
/// <list type="number">
/// <item>the staged package is checked again against the signed SHA256SUMS (signature, version, hash);</item>
/// <item>its files are unpacked into <c>incoming/</c>; a file that is already what is installed is left alone;</item>
/// <item>every changed file is swapped: the installed one is renamed into <c>backup/</c>, the new one renamed into
/// place (the launcher's executable last). A file the last release installed and this one dropped goes to
/// <c>backup/</c> too. Player data (<see cref="UpdatePackage.IsPlayerData"/>) is never touched;</item>
/// <item>any failure puts every file back from <c>backup/</c>, in reverse order.</item>
/// </list>
/// The caller starts the new launcher and then calls <see cref="InstalledUpdate.Commit"/>, or
/// <see cref="InstalledUpdate.Rollback"/> when it does not start.
/// </summary>
public sealed class UpdateInstaller(Ed25519Verifier verifier, IReadOnlyList<string>? keys = null, Action<string>? log = null)
{
    private readonly IReadOnlyList<string> _keys = keys ?? ReleaseSums.ReleaseKeys;

    /// <summary>The step and, while replacing, the fraction done (0..1; negative: unknown).</summary>
    public Action<UpdateStep, double>? Progress { get; init; }

    /// <summary>How long a rename is retried: the launcher has just exited, and a scanner may still hold a file.</summary>
    public TimeSpan Patience { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Waits for the launcher to be gone. True at once for a process that no longer exists;
    /// false when it still runs after <paramref name="timeout"/>.
    /// </summary>
    public static bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0)
        {
            return true;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // gone while it was being opened
        }
    }

    /// <summary>
    /// The staged package against the signed list: the signature is a release key's, the list is this
    /// version's, and the package's SHA-256 is the one listed. Throws <see cref="UpdateException"/>.
    /// </summary>
    public void VerifyStage(UpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validated();
        try
        {
            var sums = ReleaseSums.Verify(File.ReadAllBytes(plan.SumsPath), File.ReadAllText(plan.SignaturePath), _keys, plan.Version, verifier);
            if (!sums.TryGetValue(plan.Asset, out var want))
            {
                throw new UpdateException($"{plan.Asset} is not in SHA256SUMS");
            }
            if (new FileInfo(plan.PackagePath).Length > UpdatePackage.MaxBytes || ReleaseSums.Sha256OfFile(plan.PackagePath) != want)
            {
                throw new UpdateException($"{plan.Asset}: checksum mismatch");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"the staged update cannot be read: {e.Message}", e);
        }
    }

    /// <summary>Verifies and swaps. Throws <see cref="UpdateException"/>; what was installed before is then back in place.</summary>
    public InstalledUpdate Apply(UpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Progress?.Invoke(UpdateStep.Verifying, -1);
        VerifyStage(plan);
        var transaction = plan.Transaction;
        var incoming = Path.Combine(transaction, "incoming");
        var backup = Path.Combine(transaction, "backup");
        var done = new List<Swapped>();
        var installed = new InstalledUpdate(plan, backup, incoming, done, this);
        try
        {
            DeleteTree(incoming);
            if (Directory.Exists(backup))
            {
                // An earlier attempt that was cut short: its originals are kept, never overwritten.
                Directory.Move(backup, $"{backup}-{DateTime.UtcNow.Ticks}");
            }
            var files = Unpack(plan, incoming);
            installed.Paths = [.. files];
            var changed = files.Where(path => !Same(Within(incoming, path), Within(plan.Root, path))).ToList();
            var retired = Retired(plan, files);
            log?.Invoke($"{files.Count} files in the package, {changed.Count} changed, {retired.Count} dropped");
            // The launcher's executable last: until then a start still finds the old launcher.
            changed = [.. changed.OrderBy(path => path.Equals(plan.Launcher, StringComparison.OrdinalIgnoreCase) ? 1 : 0)];
            var steps = retired.Count + changed.Count;
            var at = 0;
            Progress?.Invoke(UpdateStep.Replacing, 0);
            foreach (var path in retired)
            {
                var swap = new Swapped(path);
                done.Add(swap);
                Move(Within(plan.Root, path), Within(backup, path));
                swap.BackedUp = true;
                log?.Invoke($"removed {path}");
                Progress?.Invoke(UpdateStep.Replacing, (double)++at / steps);
            }
            foreach (var path in changed)
            {
                var destination = Within(plan.Root, path);
                var swap = new Swapped(path);
                done.Add(swap);
                if (Directory.Exists(destination))
                {
                    throw new UpdateException($"{path} is a folder here");
                }
                if (File.Exists(destination))
                {
                    Move(destination, Within(backup, path));
                    swap.BackedUp = true;
                }
                Move(Within(incoming, path), destination);
                swap.Placed = true;
                log?.Invoke($"{(swap.BackedUp ? "replaced" : "added")} {path}");
                Progress?.Invoke(UpdateStep.Replacing, (double)++at / steps);
            }
            Progress?.Invoke(UpdateStep.Replacing, 1);
            return installed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or UpdateException or NotSupportedException)
        {
            log?.Invoke($"failed: {e.Message}");
            installed.Rollback();
            throw new UpdateException(e.Message, e);
        }
    }

    /// <summary>Unpacks the package into <paramref name="incoming"/>; the install paths.</summary>
    private static List<string> Unpack(UpdatePlan plan, string incoming)
    {
        var paths = new List<string>();
        using var zip = ZipFile.OpenRead(plan.PackagePath);
        foreach (var (entry, path) in UpdatePackage.Files(zip, plan.Launcher))
        {
            var target = Within(incoming, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var from = entry.Open())
            using (var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // The declared length was counted against the package's limit: a longer stream is a lie.
                var buffer = new byte[1 << 16];
                long written = 0;
                int n;
                while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += n;
                    if (written > entry.Length)
                    {
                        throw new UpdateException($"{path} is larger than the package says");
                    }
                    to.Write(buffer, 0, n);
                }
                to.Flush(flushToDisk: true);
            }
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Files the last installed release brought that this one no longer has, and that are still here.</summary>
    private static List<string> Retired(UpdatePlan plan, List<string> files)
    {
        var list = Path.Combine(UpdatePlan.Store(plan.Root), UpdatePlan.InstalledName);
        if (!File.Exists(list))
        {
            return [];
        }
        var now = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return [.. File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => UpdatePackage.SafePath(path) && !UpdatePackage.IsPlayerData(path) && !now.Contains(path)
                && !path.Equals(plan.Launcher, StringComparison.OrdinalIgnoreCase) && File.Exists(Within(plan.Root, path)))
            .Take(UpdatePackage.MaxFiles)];
    }

    internal static string Within(string root, string relative)
    {
        if (!UpdatePackage.SafePath(relative))
        {
            throw new UpdateException($"unsafe path: {relative}");
        }
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool Same(string fresh, string installed)
    {
        var (a, b) = (new FileInfo(fresh), new FileInfo(installed));
        if (!b.Exists || a.Length != b.Length)
        {
            return false;
        }
        try
        {
            using var x = a.OpenRead();
            using var y = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(x).AsSpan().SequenceEqual(SHA256.HashData(y));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // unreadable: replace it, and let the move say why if it cannot
        }
    }

    /// <summary>A rename on one volume, retried while something still holds the file.</summary>
    internal void Move(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        var began = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                File.Move(from, to, overwrite: false);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && e is not FileNotFoundException and not DirectoryNotFoundException
                && Stopwatch.GetElapsedTime(began) < Patience)
            {
                Thread.Sleep(200);
            }
        }
    }

    internal static void DeleteTree(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"cannot clear {folder}: {e.Message}", e);
        }
    }

    internal void Say(string line) => log?.Invoke(line);

    internal sealed class Swapped(string path)
    {
        public string Path { get; } = path;

        /// <summary>The installed file is in <c>backup/</c>.</summary>
        public bool BackedUp { get; set; }

        /// <summary>The new file is in place.</summary>
        public bool Placed { get; set; }
    }
}

/// <summary>A swap that went through: keep it (<see cref="Commit"/>) or undo it (<see cref="Rollback"/>).</summary>
public sealed class InstalledUpdate
{
    private readonly UpdatePlan _plan;
    private readonly string _backup;
    private readonly string _incoming;
    private readonly List<UpdateInstaller.Swapped> _done;
    private readonly UpdateInstaller _installer;

    internal InstalledUpdate(UpdatePlan plan, string backup, string incoming, List<UpdateInstaller.Swapped> done, UpdateInstaller installer)
    {
        (_plan, _backup, _incoming, _done, _installer) = (plan, backup, incoming, done, installer);
    }

    /// <summary>Every install path of the package.</summary>
    public IReadOnlyList<string> Paths { get; internal set; } = [];

    /// <summary>The paths that were replaced, added or removed.</summary>
    public IReadOnlyList<string> Changed => [.. _done.Where(s => s.Placed || s.BackedUp).Select(s => s.Path)];

    /// <summary>
    /// Puts back what was installed before, newest change first. Throws <see cref="UpdateException"/>
    /// naming <c>backup/</c> when a file cannot be put back (it stays there).
    /// </summary>
    public void Rollback()
    {
        var failed = new List<string>();
        for (var i = _done.Count - 1; i >= 0; i--)
        {
            var swap = _done[i];
            try
            {
                var destination = UpdateInstaller.Within(_plan.Root, swap.Path);
                if (swap.Placed)
                {
                    // Back to incoming/ rather than deleted: a rename works on a file something has open.
                    _installer.Move(destination, UpdateInstaller.Within(_incoming, swap.Path + ".undone"));
                    swap.Placed = false;
                }
                if (swap.BackedUp)
                {
                    _installer.Move(UpdateInstaller.Within(_backup, swap.Path), destination);
                    swap.BackedUp = false;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{swap.Path} ({e.Message})");
            }
        }
        _done.Clear();
        if (failed.Count > 0)
        {
            _installer.Say($"rollback incomplete: {string.Join("; ", failed)}");
            throw new UpdateException($"the previous version could not be fully restored; its files are in {_backup}: {string.Join("; ", failed)}") { Incomplete = true };
        }
        _installer.Say("the previous version was restored");
    }

    /// <summary>
    /// The new launcher runs: records what this release installed and drops <c>incoming/</c>. The
    /// backup and the package are left for the new launcher to clear (it may be this very process's
    /// old executable in there).
    /// </summary>
    public void Commit()
    {
        try
        {
            File.WriteAllLines(Path.Combine(UpdatePlan.Store(_plan.Root), UpdatePlan.InstalledName), Paths);
            if (Directory.Exists(_incoming))
            {
                Directory.Delete(_incoming, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _installer.Say($"tidying up: {e.Message}"); // the update is installed all the same
        }
    }
}

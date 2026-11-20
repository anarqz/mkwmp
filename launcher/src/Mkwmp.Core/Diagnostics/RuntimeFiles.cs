// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core.Diagnostics;

/// <summary>
/// What mkw-recomp itself leaves behind, for a report (read from its source, runtime/core/boot/main.c
/// and tools/prepare/prepare.py of the 0.1.15 / 0.1.16 line):
/// <list type="bullet">
/// <item><c>&lt;data&gt;\logs\mkw.log</c>, the game's run, and its rotation <c>mkw.1.log</c> … <c>mkw.N.log</c>;
/// the runtime's own crash reporter writes there too (<c>FATAL: … at host … (mkw+0x…)</c>, the guest
/// registers and frames) and then ends the process with exit code 71. It writes no dump and no
/// other crash file: the only dumps are MKWMP's own (<c>mkwmp-crash-*.dmp</c>, beside mkw.exe);</item>
/// <item><c>&lt;data&gt;\logs\&lt;name&gt;.log</c> of the runs that are not the player's: <c>warm.log</c> (the
/// setup's warm-up), <c>launcher.log</c> (its start screen), and <c>setup.log</c> (prepare.py);</item>
/// <item><c>&lt;data&gt;\setup-state.json</c> and <c>mkwgame.id</c> (which game code was built), and
/// <c>mkw.ini</c> beside mkw.exe (settings and paths; no secret is kept there, and a line whose key
/// looks like one is masked anyway). <c>mod-settings\</c> is never taken: the MKWMP mod keeps the PIN there.</item>
/// </list>
/// <c>&lt;data&gt;</c> is mkw.exe's folder, or mkw.ini's <c>game_dir</c> (<see cref="Recomp.DataDir(string)"/>; under Proton a <c>Z:\</c> path).
/// A file goes in when it changed since the session started, or is among the newest
/// <see cref="KeepRecent"/> of its folder; a long one keeps its tail (<see cref="MaxFileBytes"/>),
/// and all of them together stay under <see cref="MaxTotalBytes"/>, the newest first.
/// </summary>
public static class RuntimeFiles
{
    public const long MaxFileBytes = 4L * 1024 * 1024;
    public const long MaxTotalBytes = 20L * 1024 * 1024;
    public const int KeepRecent = 4;

    private static readonly string[] SecretWords = ["pin", "ticket", "token", "password", "secret", "key"];

    /// <summary>The zip's folder for them.</summary>
    public const string Folder = "mkw-recomp";

    /// <summary>
    /// Copies them into <c>&lt;bundle&gt;\mkw-recomp\</c> (and the launcher's own setup-driver logs,
    /// <c>logs\setup-*.log</c>, into <c>&lt;bundle&gt;\</c>); returns the lines for system.txt
    /// (the data folder, <c>mkwgame.id</c>, mkw.exe's SHA-256). Never throws: a file that cannot be read is one file less.
    /// </summary>
    public static string Collect(string bundle, string launcherFolder, GameInstall install, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(install);
        var system = new StringBuilder();
        var budget = MaxTotalBytes;
        try
        {
            var game = install.Root;
            var datas = new[] { Recomp.DataDir(game), install.DataRoot ?? game }.Where(d => d is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var into = Path.Combine(bundle, Folder);
            Directory.CreateDirectory(into);
            // the copies SupportBundle made whole: the capped ones below replace them
            foreach (var whole in new[] { "mkw.log", "mkw.1.log" })
            {
                File.Delete(Path.Combine(bundle, whole));
            }
            foreach (var data in datas)
            {
                system.Append(CultureInfo.InvariantCulture, $"mkw-recomp data folder (logs, setup-state.json): {data}\n");
                foreach (var small in new[] { "setup-state.json", "mkwgame.id" })
                {
                    budget -= Tail(Path.Combine(data, small), Path.Combine(into, small), Math.Min(MaxFileBytes, budget));
                }
                if (ReadSmall(Path.Combine(data, "mkwgame.id")) is { } id)
                {
                    system.Append(CultureInfo.InvariantCulture, $"mkwgame.id: {id.Trim()}\n");
                }
                foreach (var log in Pick(Path.Combine(data, "logs"), "*", since))
                {
                    if (budget <= 0)
                    {
                        break;
                    }
                    budget -= Tail(log.FullName, Path.Combine(into, log.Name), Math.Min(MaxFileBytes, budget));
                }
            }
            foreach (var ini in datas.Append(game).Distinct(StringComparer.OrdinalIgnoreCase).Select(d => Path.Combine(d, "mkw.ini")).Where(File.Exists).Take(1))
            {
                if (ReadSmall(ini) is { } text)
                {
                    File.WriteAllText(Path.Combine(into, "mkw.ini"), RedactIni(text));
                }
            }
            system.Append("mkw.exe sha256: ").Append(Sha256(install.Executable)).Append('\n');
            foreach (var log in Pick(Path.Combine(launcherFolder, "logs"), "setup-*.log", since))
            {
                if (budget <= 0)
                {
                    break;
                }
                budget -= Tail(log.FullName, Path.Combine(bundle, log.Name), Math.Min(MaxFileBytes, budget));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            system.Append(CultureInfo.InvariantCulture, $"mkw-recomp files: {e.Message}\n");
        }
        return system.ToString();
    }

    /// <summary>The folder's files changed since <paramref name="since"/>, plus its newest <see cref="KeepRecent"/>; newest first.</summary>
    internal static List<FileInfo> Pick(string folder, string pattern, DateTimeOffset since)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return [];
            }
            var all = new DirectoryInfo(folder).EnumerateFiles(pattern).Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(64).ToList();
            return [.. all.Where((f, i) => i < KeepRecent || f.LastWriteTimeUtc >= since.UtcDateTime.AddMinutes(-1))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Copies the last <paramref name="max"/> bytes of a file (from the start of a line, with a first line saying so); returns the bytes written.</summary>
    internal static long Tail(string from, string to, long max)
    {
        try
        {
            if (max <= 0 || !File.Exists(from))
            {
                return 0;
            }
            using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dest = File.Create(to);
            if (source.Length > max)
            {
                source.Seek(-max, SeekOrigin.End);
                for (int b; (b = source.ReadByte()) >= 0 && b != '\n';)
                {
                    // the rest of the line the cut fell in
                }
                dest.Write(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
                    $"[the last {source.Length - source.Position} of {source.Length} bytes: the report keeps the tail]\n")));
            }
            source.CopyTo(dest);
            return dest.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>mkw.ini as it is (settings and paths), with the value of any key that looks like a secret masked.</summary>
    internal static string RedactIni(string text)
    {
        var b = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var key = line.Split('=')[0].Trim().ToLowerInvariant();
            var secret = line.Contains('=', StringComparison.Ordinal) && !key.StartsWith('#') && !key.StartsWith(';')
                && SecretWords.Any(w => key == w || key.EndsWith("_" + w, StringComparison.Ordinal) || key.StartsWith(w + "_", StringComparison.Ordinal));
            b.Append(secret ? $"{line.Split('=')[0]}= <redacted>" : SessionLog.Redact(line)).Append('\n');
        }
        return b.ToString();
    }

    /// <summary>The bundle's <c>index.txt</c>: every file in it, with its size and when it was last written.</summary>
    public static void WriteIndex(string bundle)
    {
        try
        {
            var b = new StringBuilder("Files in this report (size in bytes, last written UTC, path). Long logs keep their tail; mkw-recomp/ is the runtime's own.\n");
            foreach (var file in new DirectoryInfo(bundle).EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            {
                b.Append(CultureInfo.InvariantCulture, $"{file.Length,10}  {file.LastWriteTimeUtc:yyyy-MM-dd'T'HH:mm:ss'Z'}  {Path.GetRelativePath(bundle, file.FullName).Replace('\\', '/')}\n");
            }
            File.WriteAllText(Path.Combine(bundle, "index.txt"), b.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a report without its list
        }
    }

    private static string? ReadSmall(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024 ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Sha256(string path)
    {
        try
        {
            return File.Exists(path) ? Updater.Sha256Hex(File.ReadAllBytes(path)) : "missing";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }
}

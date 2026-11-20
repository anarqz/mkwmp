// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// The support bundle: the logs and facts a bug report needs, collected in
/// <c>&lt;launcher folder&gt;/logs/bug-report-&lt;stamp&gt;/</c> and zipped beside it, with nothing
/// secret in it (the session file's token and ticket are masked, the logs already are).
/// </summary>
public static class SupportBundle
{
    /// <summary>A target's <see cref="SupportItem"/> with this name is its part of system.txt.</summary>
    public const string SystemFile = "system.txt";

    /// <summary><c>windows x86_64</c>, as the Rust launcher named it in logs and reports.</summary>
    public static string Os => "windows " + (RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x86_64" : RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());

    /// <summary>Masks the secrets of the hook's session file (mkwmp.toml).</summary>
    public static string RedactToml(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var b = new StringBuilder();
        var lines = text.Split('\n').ToList();
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var key = line.Split('=')[0].Trim();
            b.Append(key is "launcher_token" or "ticket" or "pin" && line.Contains('=', StringComparison.Ordinal)
                ? $"{key} = \"<redacted>\"\n"
                : $"{SessionLog.Redact(line)}\n");
        }
        return b.ToString();
    }

    /// <summary>
    /// Builds the bundle; the folder and the zip. <paramref name="launcherFolder"/> is where <c>logs/</c> lives.
    /// A crash or bug report (Diagnostics/BugReport.cs) adds its own files through <paramref name="extra"/>
    /// (called with the folder, before it is zipped; its failures are its own to swallow) and has the zip
    /// written as <paramref name="zipPath"/> (the Desktop) instead of beside the folder.
    /// </summary>
    public static (string Folder, string Zip) Save(string launcherFolder, string version, IGameTarget? target, GameInstall? install, TimeProvider? clock = null,
        Action<string>? extra = null, string? zipPath = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var logs = Path.Combine(launcherFolder, "logs");
        var folder = Path.Combine(logs, $"bug-report-{SessionLog.FileStamp(now)}");
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{folder}: {e.Message}", e);
        }
        // The two newest launcher sessions (this one, then the previous).
        foreach (var session in SessionLog.Sessions(logs).Take(2))
        {
            Copy(session, Path.Combine(folder, Path.GetFileName(session)));
        }
        var system = new StringBuilder($"launcher: {version}\nos: {Os}\n");
        if (target is not null && install is not null)
        {
            foreach (var item in target.SupportItems(install))
            {
                // Only a bare file name: a target cannot write outside the bundle.
                var name = Path.GetFileName(item.Name);
                if (name.Length == 0)
                {
                    continue;
                }
                if (name == SystemFile)
                {
                    system.Append(item.Text);
                }
                else if (item.Text is not null)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(folder, name), item.Text);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // one file less
                    }
                }
                else if (item.SourcePath is not null)
                {
                    Copy(item.SourcePath, Path.Combine(folder, name));
                }
            }
        }
        else
        {
            system.Append("game folder: not set\n");
        }
        system.Append("time: ").Append(SessionLog.Timestamp(now)).Append('\n');
        var zip = zipPath ?? folder + ".zip";
        try
        {
            File.WriteAllText(Path.Combine(folder, SystemFile), system.ToString());
            extra?.Invoke(folder);
            File.Delete(zip);
            ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{zip}: {e.Message}", e);
        }
        return (folder, zip);
    }

    /// <summary>A log may be missing or held open by the game: the bundle is made with what can be read.</summary>
    internal static void Copy(string from, string to)
    {
        try
        {
            if (File.Exists(from))
            {
                using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dest = File.Create(to);
                source.CopyTo(dest);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // one file less
        }
    }
}

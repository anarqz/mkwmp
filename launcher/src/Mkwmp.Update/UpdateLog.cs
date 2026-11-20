// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;

namespace Mkwmp.Update;

/// <summary>
/// <c>logs/updater.log</c> next to the launcher's session logs: what the last installs did, one line
/// per step, appended (kept under 256 KiB). Writing never fails an install.
/// </summary>
public sealed class UpdateLog(string path)
{
    private const long MaxBytes = 256 * 1024;
    private readonly Lock _gate = new();

    public string Path { get; } = path;

    public void Line(string text)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    File.Move(Path, Path + ".1", overwrite: true);
                }
                File.AppendAllText(Path, $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} {text}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // nowhere to say it
            }
        }
    }
}

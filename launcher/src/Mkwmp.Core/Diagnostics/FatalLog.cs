// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mkwmp.Core.Diagnostics;

/// <summary>
/// The launcher's last-chance record, <c>&lt;launcher folder&gt;/logs/launcher-crash.log</c>: one line when
/// a launcher starts, one when it leaves, and between them whatever killed it. Every line goes to
/// the disk before the call returns (the file is opened for appending, write-through), so a
/// process that dies in the next instruction has still said why.
/// <para>
/// A windowed program has no standard error, so what the .NET runtime itself prints as it dies
/// ("Fatal error. System.AccessViolationException…" with the stack, "Stack overflow", a
/// <c>FailFast</c> message) is lost: on Windows it is at least in the Event Log, under Wine /
/// Proton it is nowhere and the launcher just disappears. <see cref="Install"/> therefore makes
/// this file the process's standard error when it has none (or under Wine, where the one it has
/// leads nowhere a player would look).
/// </para>
/// <para>
/// A session whose start has no end in this file was killed from outside or died without the
/// runtime noticing: <see cref="Install"/> returns that, for the next session's log.
/// </para>
/// </summary>
public static class FatalLog
{
    public const string FileName = "launcher-crash.log";

    /// <summary>The file is cut to its last part when it grows past this.</summary>
    private const int MaxBytes = 256 * 1024;
    private const int KeepBytes = 64 * 1024;

    private const uint FileAppendData = 0x0004;
    private const uint Synchronize = 0x0010_0000;
    private const uint ShareAll = 0x7; // read, write, delete: a second launcher appends to the same file
    private const uint OpenAlways = 4;
    private const uint AttributeNormal = 0x80;
    private const uint WriteThrough = 0x8000_0000;
    private const int StdErrorHandle = -12;

    private static readonly Lock Gate = new();
    private static SafeFileHandle? _file;

    /// <summary>The file of this process; null until <see cref="Install"/> opened one.</summary>
    public static string? Path { get; private set; }

    /// <summary>
    /// Opens the record, writes this session's start, and hooks the process's end: an unhandled
    /// exception (with its text), and the exit (with its code). Returns what the file says of the
    /// session before this one when that one never wrote its exit; null otherwise. Never throws:
    /// without a file the launcher runs as before.
    /// </summary>
    /// <param name="launcherFolder">The folder that holds <c>logs/</c>.</param>
    /// <param name="version">The launcher's version, for the start line.</param>
    /// <param name="standardError">Also make the file the process's standard error (see the class).</param>
    public static string? Install(string launcherFolder, string version, bool standardError)
    {
        ArgumentException.ThrowIfNullOrEmpty(launcherFolder);
        string? before = null;
        lock (Gate)
        {
            if (_file is not null)
            {
                return null;
            }
            try
            {
                var logs = System.IO.Path.Combine(launcherFolder, "logs");
                Directory.CreateDirectory(logs);
                var path = System.IO.Path.Combine(logs, FileName);
                before = Unfinished(Trim(path));
                var handle = CreateFileW(path, FileAppendData | Synchronize, ShareAll, IntPtr.Zero, OpenAlways, AttributeNormal | WriteThrough, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return before;
                }
                (_file, Path) = (handle, path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                or DllNotFoundException or EntryPointNotFoundException)
            {
                return before;
            }
            if (standardError)
            {
                // The runtime writes its last words with GetStdHandle(STD_ERROR_HANDLE) at the moment it dies.
                _ = SetStdHandle(StdErrorHandle, _file.DangerousGetHandle());
            }
        }
        Write($"start pid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} mkwmp-launcher {version}{(Platform.WineHost.Detected ? " (Wine)" : "")}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"unhandled exception{(e.IsTerminating ? " (the process ends)" : "")}: {e.ExceptionObject}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Write($"exit pid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} code={Environment.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        return before;
    }

    /// <summary>Whether the process has a standard error to write to (a terminal, a pipe, a file).</summary>
    public static bool HasStandardError
    {
        get
        {
            try
            {
                var handle = GetStdHandle(StdErrorHandle);
                return handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>One timestamped line (secrets masked as in the session log), on the disk when this returns.</summary>
    public static void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var line = Encoding.UTF8.GetBytes($"{SessionLog.Timestamp(DateTimeOffset.UtcNow)} {SessionLog.Redact(message)}\r\n");
        lock (Gate)
        {
            if (_file is not { IsInvalid: false, IsClosed: false } file)
            {
                return;
            }
            // One WriteFile on an append handle: whole lines even with a second launcher writing.
            _ = WriteFile(file, line, (uint)line.Length, out _, IntPtr.Zero);
        }
    }

    /// <summary>Keeps the file small; returns its text (its end), or "" when there is none.</summary>
    internal static string Trim(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxBytes)
            {
                var from = Array.IndexOf(bytes, (byte)'\n', bytes.Length - KeepBytes) + 1;
                bytes = bytes[from..];
                File.WriteAllBytes(path, bytes);
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ""; // another launcher holds it: nothing to say about the last session
        }
    }

    /// <summary>
    /// What <paramref name="text"/> says of its last session when that one has a start and no exit
    /// (and its process is gone): the start line and everything after it. Null when the last
    /// session left properly, still runs, or the file is empty.
    /// </summary>
    internal static string? Unfinished(string text, Func<int, bool>? alive = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.FindLastIndex(l => Field(l, "start") is not null);
        if (start < 0 || Field(lines[start], "start") is not { } pid)
        {
            return null;
        }
        if (lines.Skip(start + 1).Any(l => Field(l, "exit") == pid) || (alive ?? IsAlive)(pid))
        {
            return null;
        }
        return string.Join(Environment.NewLine, lines.Skip(start).Take(60));
    }

    /// <summary>The pid of a "<c>&lt;time&gt; start pid=N …</c>" / "<c>… exit pid=N …</c>" line; null for any other line.</summary>
    private static int? Field(string line, string word)
    {
        var parts = line.Split(' ', 4);
        return parts.Length >= 3 && parts[1] == word && parts[2].StartsWith("pid=", StringComparison.Ordinal)
            && int.TryParse(parts[2].AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.Contains("mkwmp", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

#pragma warning disable SYSLIB1054 // plain DllImport, as the rest of Core
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WriteFile(SafeFileHandle file, byte[] buffer, uint count, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetStdHandle(int which, IntPtr handle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFileType(IntPtr handle);
#pragma warning restore SYSLIB1054
}

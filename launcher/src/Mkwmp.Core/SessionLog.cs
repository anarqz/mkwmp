// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text;

namespace Mkwmp.Core;

/// <summary>
/// The launcher's messages: printed (console mode, a terminal that started us) and appended to
/// <c>&lt;launcher folder&gt;/logs/launcher-&lt;UTC date&gt;-&lt;time&gt;.log</c>, flushed per line so a
/// crash loses nothing, with secrets (tickets, tokens, PINs) masked: players attach these files
/// to public bug reports.
/// </summary>
public sealed class SessionLog : IDisposable
{
    /// <summary>Session logs kept in <c>logs/</c>.</summary>
    public const int Keep = 10;

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private StreamWriter? _file;

    /// <summary>Opens a new session log in <c>&lt;directory&gt;/logs</c> and prunes old ones. Failures are silent: logging never stops the launcher.</summary>
    public SessionLog(string? directory, TimeProvider? clock = null, bool echo = true)
    {
        _clock = clock ?? TimeProvider.System;
        Echo = echo;
        if (directory is null)
        {
            return;
        }
        try
        {
            var logs = System.IO.Path.Combine(directory, "logs");
            Directory.CreateDirectory(logs);
            Path = System.IO.Path.Combine(logs, $"launcher-{FileStamp(_clock.GetUtcNow())}.log");
            _file = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            Prune(logs, Keep);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _file = null;
        }
    }

    /// <summary>This session's file (null when it could not be opened).</summary>
    public string? Path { get; }

    /// <summary>Also print each note as <c>mkwmp: …</c>.</summary>
    public bool Echo { get; set; }

    /// <summary>A message for the console and the log.</summary>
    public void Note(string message)
    {
        if (Echo)
        {
            try
            {
                Console.WriteLine($"mkwmp: {message}");
            }
            catch (IOException)
            {
                // no console to write to
            }
        }
        Line(message);
    }

    /// <summary>Appends a timestamped, redacted line to the session log (if open).</summary>
    public void Line(string message)
    {
        lock (_gate)
        {
            if (_file is null)
            {
                return;
            }
            try
            {
                _file.WriteLine($"{Timestamp(_clock.GetUtcNow())} {Redact(message)}");
                _file.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // a full disk never stops the launcher
            }
        }
    }

    /// <summary><c>launcher-*.log</c> files in <paramref name="logs"/>, newest first (names sort by time).</summary>
    public static IReadOnlyList<string> Sessions(string logs)
    {
        try
        {
            return [.. Directory.EnumerateFiles(logs, "launcher-*.log").OrderByDescending(p => System.IO.Path.GetFileName(p), StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> session logs.</summary>
    public static void Prune(string logs, int keep)
    {
        foreach (var old in Sessions(logs).Skip(keep))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // in use by another launcher: left for the next start
            }
        }
    }

    /// <summary>
    /// Masks what must never reach a log: <c>mkw1.</c> tickets, <c>nx2</c> game tokens, 64-hex tokens, and a 6-digit PIN
    /// on a line that mentions "pin".
    /// </summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var o = new StringBuilder(text.Length);
        var run = new StringBuilder();
        void Flush()
        {
            var word = run.ToString();
            if (word.StartsWith("mkw1.", StringComparison.Ordinal))
            {
                o.Append("mkw1.<redacted>");
            }
            else if (word.Length >= 16 && word.StartsWith("nx2", StringComparison.Ordinal))
            {
                o.Append("nx2<redacted>"); // a Nextendo game token (never logged on purpose; masked all the same)
            }
            else if (word.Length >= 64 && word.All(char.IsAsciiHexDigit))
            {
                o.Append("<redacted>");
            }
            else
            {
                o.Append(word);
            }
            run.Clear();
        }
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
            {
                run.Append(c);
            }
            else
            {
                Flush();
                o.Append(c);
            }
        }
        Flush();
        var masked = o.ToString();
        return masked.Contains("pin", StringComparison.OrdinalIgnoreCase) ? MaskPin(masked) : masked;
    }

    /// <summary>Replaces every standalone run of exactly 6 digits with <c>&lt;pin&gt;</c>.</summary>
    private static string MaskPin(string text)
    {
        var o = new StringBuilder(text.Length);
        var digits = new StringBuilder();
        var previousAlnum = false;
        void Flush(bool nextAlnum)
        {
            if (digits.Length == 6 && !previousAlnum && !nextAlnum)
            {
                o.Append("<pin>");
            }
            else
            {
                o.Append(digits);
            }
            digits.Clear();
        }
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
                continue;
            }
            if (digits.Length > 0)
            {
                Flush(char.IsLetterOrDigit(c));
            }
            previousAlnum = char.IsLetterOrDigit(c);
            o.Append(c);
        }
        Flush(false);
        return o.ToString();
    }

    /// <summary><c>2026-11-15T09:30:12.345Z</c></summary>
    public static string Timestamp(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary><c>20261115-093012</c>, for file and folder names.</summary>
    public static string FileStamp(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}

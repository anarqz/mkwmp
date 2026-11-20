// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.RegularExpressions;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>What <c>&lt;data dir&gt;/setup-state.json</c> says of the last setup (prepare.py <c>save_state</c>).</summary>
/// <param name="Port">The runtime version the last finished build was for; null until a setup got past its shaders step.</param>
/// <param name="Source">The player's copy of the game that setup read.</param>
/// <param name="Copy">Its data was copied into the install (<c>romfs_mode</c> "copy") rather than read in place.</param>
internal sealed record RecompSetupState(string? Port, string? Source, bool Copy);

/// <summary>What the driver needs from its target and the machine (the tests replace each).</summary>
internal sealed record RecompSetupHost
{
    public required Func<bool> IsRunning { get; init; }

    public Func<string, long?> FreeSpace { get; init; } = RuntimeInstaller.DriveFreeSpace;

    /// <summary>
    /// The process to start for these prepare.py arguments (the ones after the script), instead of
    /// the install's own Python: a test's stand-in script. The driver still sets the folder, the
    /// pipes and the environment.
    /// </summary>
    public Func<IReadOnlyList<string>, ProcessStartInfo>? Command { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// The launcher runs mkw-recomp's game setup itself, so the player never meets mkw-recomp's start
/// screen (launcher/docs/RECOMP_INTEGRATION.md, stage 1). The whole setup is one Python program
/// the runtime's zip carries, started exactly as that start screen starts it
/// (<c>runtime/ui/launcher/launcher_setup.cpp</c> <c>setup_start</c>):
/// <code>
/// &lt;runtime&gt;\setup\python\python.exe -I &lt;runtime&gt;\setup\src\tools\prepare\prepare.py
///     --game &lt;data folder&gt; --runtime &lt;folder of mkw.exe&gt; --port &lt;runtime version&gt;
///     [--source &lt;the player's copy&gt; --romfs link|copy]
/// </code>
/// Without <c>--source</c> it repeats the last setup and runs only the steps whose tools changed.
/// It saves <c>setup-state.json</c> after each step, so a stopped setup continues, and it ties its
/// own tools to itself (a job object), so ending it ends them.
/// <para>
/// The game is the player's: the launcher passes the path they picked and never copies, moves,
/// downloads or uploads it. What the setup builds from it stays in the install.
/// </para>
/// <para>
/// Under Wine / Proton the same Windows Python runs as a child of the launcher, in its prefix,
/// with Windows paths (<c>Z:\...</c> for files outside the prefix). The shaders step is the part
/// nobody has confirmed there (it injects a DLL into NVIDIA's disassembler and downloads it
/// first): a failure in it says so and points at the log.
/// </para>
/// </summary>
internal static partial class RecompSetup
{
    /// <summary>
    /// The runtimes whose setup this driver knows (arguments, events, error codes: checked at each
    /// bump; 0.1.16's prepare.py takes the same arguments and writes the same events as 0.1.15's,
    /// research/docs/RECOMP-0.1.16.md).
    /// </summary>
    public static readonly IReadOnlyList<string> Runtimes = [RecompRuntime.Pinned.Version];

    /// <summary>prepare.py <c>PREP_VERSION</c>: a state file with another one counts as empty there.</summary>
    public const int StateVersion = 1;

    /// <summary><c>CFG_GAME_DIR_WIN32_MAX</c> (runtime/core/base/cfg.h): the runtime refuses a longer data folder.</summary>
    public const int GameDirMax = 131;

    /// <summary>What a first setup needs free: 4.1 GB at its peak, with room to spare.</summary>
    public const long BuildSpace = 5_000_000_000;

    /// <summary>The game's data, when it is copied (prepare.py <c>ROMFS_BYTES</c>).</summary>
    public const long CopySpace = 22L * 1024 * 1024 * 1024;

    /// <summary>Setup logs kept in the launcher's <c>logs/</c>.</summary>
    public const int KeepLogs = 10;

    public const string LogPrefix = "setup-";

    private const string State = "setup-state.json";
    private static readonly byte[] NupMagic = "NUP\u0001"u8.ToArray();
    private static readonly byte[] NxMagic = "NXAR"u8.ToArray();
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(500);

    /// <summary>The data folders with a setup under way in this process.</summary>
    private static readonly ConcurrentDictionary<string, byte> Busy = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"pairs prewarm: (\d+) of (\d+) shader libraries", RegexOptions.CultureInvariant)]
    private static partial Regex WarmLine();

    /// <summary>Whether the launcher sets this install up itself (else the player does, in mkw-recomp).</summary>
    public static bool Drives(GameInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return install.TargetId == MkwRecompTarget.TargetId && install.Version is { } version && Runtimes.Contains(version);
    }

    public static string DataDir(GameInstall install) => install.DataRoot ?? install.Root;

    public static string Python(GameInstall install) => Path.Combine(install.Root, "setup", "python", "python.exe");

    public static string Script(GameInstall install) => Path.Combine(install.Root, "setup", "src", "tools", "prepare", "prepare.py");

    // ---- the state: is a setup needed? --------------------------------------------------------

    /// <summary>The last setup's record; null when there is none this runtime's setup would take.</summary>
    public static RecompSetupState? ReadState(string data)
    {
        string text;
        try
        {
            var path = Path.Combine(data, State);
            if (!File.Exists(path))
            {
                return null;
            }
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        return StateIn(text);
    }

    internal static RecompSetupState? StateIn(string json)
    {
        var root = Json.Parse(json);
        if (root.At("version").Long() != StateVersion)
        {
            return null;
        }
        // Before 0.1.5 the mode was only the first word of the romfs step's key.
        var mode = root.At("romfs_mode").Str() ?? root.At("done").At("romfs").Str()?.Split(' ')[0];
        return new RecompSetupState(root.At("port").Str(), root.At("source").Str() is { Length: > 0 } source ? source : null, mode == "copy");
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the game is set up for the runtime in this install; null when the launcher does not
    /// set this install up. <c>mkw.exe --play</c> skips mkw-recomp's own "another version set this
    /// folder up" check, so this is the only one: <c>setup-state.json</c>'s <c>port</c> must name
    /// the installed runtime, <c>mkwgame.dll</c> must be there and built for it
    /// (<c>mkwgame.id</c>), and a copy of the game that is read in place must still be there.
    /// </summary>
    public static GameSetupStatus? Status(GameInstall install)
    {
        if (!Drives(install))
        {
            return null;
        }
        var data = DataDir(install);
        var version = install.Version!;
        if (ReadState(data) is not { } state)
        {
            return new GameSetupStatus(new GameSetupPlan(GameSetupReason.NotSetUp, Strings.SetupNotSetUp, true, null), null);
        }
        var source = state.Source;
        // A copy keeps the whole game in the install: prepare.py then takes that for the source (its issue 501).
        var standsIn = state.Copy && File.Exists(Path.Combine(data, "exefs", "main")) && Directory.Exists(Path.Combine(data, "romfs"));
        var gone = source is null || (!Exists(source) && !standsIn);
        GameSetupPlan? plan;
        if (source is null)
        {
            plan = new GameSetupPlan(GameSetupReason.NotSetUp, Strings.SetupNotSetUp, true, null);
        }
        else if (gone)
        {
            plan = new GameSetupPlan(GameSetupReason.DumpMissing, Strings.SetupDumpGone(source), true, source);
        }
        else if (!File.Exists(Path.Combine(data, "mkwgame.dll")) || state.Port is null)
        {
            plan = new GameSetupPlan(GameSetupReason.Unfinished, Strings.SetupUnfinished, false, source);
        }
        else if (state.Port != version)
        {
            plan = new GameSetupPlan(GameSetupReason.RuntimeChanged, Strings.SetupRuntimeChanged(RecompRuntime.Pinned.Name, version), false, source);
        }
        else if (Recomp.RebuildProblemFor(new RecompId(RecompKind.Supported, version), Recomp.GameId(install.Root)) is not null)
        {
            plan = new GameSetupPlan(GameSetupReason.GameMismatch, Strings.SetupGameMismatch, false, source);
        }
        else
        {
            plan = null;
        }
        return new GameSetupStatus(plan, source);
    }

    // ---- the player's copy of the game --------------------------------------------------------

    private static bool StartsWith(string file, byte[] magic)
    {
        try
        {
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[4];
            return f.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && head.SequenceEqual(magic);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Why <paramref name="path"/> is not a copy of the game the setup takes; null when it is one,
    /// by the setup's own rule (<c>gamesource.py</c> <c>find_game</c>): a <c>.nup</c> package
    /// (it starts with <c>NUP\x01</c>), a <c>.nx</c> package (<c>NXAR</c>), or a folder with
    /// <c>exefs/main</c> and <c>romfs</c> in it or up to two levels below. Only the first four
    /// bytes of a file are read; the version and the completeness are the setup's own check.
    /// </summary>
    public static string? DumpProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Strings.SetupPickDump;
        }
        try
        {
            if (File.Exists(path))
            {
                return StartsWith(path, NupMagic) || StartsWith(path, NxMagic) ? null : Strings.SetupDumpNotAGame(path);
            }
            if (!Directory.Exists(path))
            {
                return Strings.SetupDumpNotFound(path);
            }
            IEnumerable<string> level = [path];
            for (var depth = 0; depth < 3; depth++)
            {
                var folders = level.ToList();
                if (folders.Any(d => File.Exists(Path.Combine(d, "exefs", "main"))
                    && (Directory.Exists(Path.Combine(d, "romfs")) || StartsWith(Path.Combine(d, "romfs"), NupMagic))))
                {
                    return null;
                }
                level = folders.SelectMany(Subfolders);
            }
            return Strings.SetupDumpNotAGame(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"{path}: {e.Message}";
        }
    }

    private static IEnumerable<string> Subfolders(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ---- the run ------------------------------------------------------------------------------

    /// <summary>The arguments after the script, as the start screen passes them.</summary>
    internal static IReadOnlyList<string> Arguments(GameInstall install, GameSetupRequest request)
    {
        var args = new List<string> { "--game", DataDir(install), "--runtime", install.Root, "--port", install.Version ?? "" };
        if (request.DumpPath is { } dump)
        {
            // Absolute, and as the player gave it otherwise: under Wine it stays a Windows path (Z:\...).
            args.AddRange(["--source", Path.GetFullPath(dump), "--romfs", request.Mode == GameDataMode.Copy ? "copy" : "link"]);
        }
        return args;
    }

    /// <summary>
    /// Runs the setup to its end. Never throws for a failure (the result says why, in words for
    /// the player); a cancel ends the setup's whole process tree and throws
    /// <see cref="OperationCanceledException"/>. Running it again continues from the last finished step.
    /// </summary>
    public static async Task<GameSetupResult> RunAsync(
        GameInstall install, GameSetupRequest request, RecompSetupHost host, IProgress<GameSetupProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        if (!Drives(install))
        {
            return GameSetupResult.Refused("none", Strings.SetupNone);
        }
        var data = DataDir(install);
        if (host.IsRunning())
        {
            return GameSetupResult.Refused("running", Strings.SetupCloseGame);
        }
        if (!Busy.TryAdd(data, 0))
        {
            return GameSetupResult.Refused("busy", Strings.SetupAlreadyRunning);
        }
        try
        {
            if (Precondition(install, request, host) is { } refused)
            {
                return refused;
            }
            cancel.ThrowIfCancellationRequested();
            return await DriveAsync(install, request, host, progress, cancel).ConfigureAwait(false);
        }
        finally
        {
            Busy.TryRemove(data, out _);
        }
    }

    /// <summary>What must hold before a process is started: nothing here writes anything.</summary>
    private static GameSetupResult? Precondition(GameInstall install, GameSetupRequest request, RecompSetupHost host)
    {
        var data = DataDir(install);
        var state = ReadState(data);
        if (request.DumpPath is { } dump)
        {
            if (DumpProblem(dump) is { } problem)
            {
                return GameSetupResult.Refused("nogame", problem, dump);
            }
        }
        else if (state?.Source is null)
        {
            return GameSetupResult.Refused("source", Strings.SetupPickDump);
        }
        if (host.Command is null && (!File.Exists(Python(install)) || !File.Exists(Script(install))))
        {
            return GameSetupResult.Refused("no_setup", Strings.SetupToolsMissing);
        }
        if (data.Length > GameDirMax)
        {
            return GameSetupResult.Refused("path", Strings.SetupPathTooLong(data, GameDirMax), data);
        }
        if (host.Command is null && !(Avx2.IsSupported && Fma.IsSupported && Bmi2.IsSupported))
        {
            return GameSetupResult.Refused("cpu", Strings.SetupCpu);
        }
        // prepare.py checks the room for a copy and 3 GiB for the build; the first build's real peak is higher.
        var first = !File.Exists(Path.Combine(data, "mkwgame.dll"));
        var copies = request.DumpPath is not null && request.Mode == GameDataMode.Copy && !Directory.Exists(Path.Combine(data, "romfs"));
        var need = (first ? BuildSpace : 0) + (copies ? CopySpace : 0);
        if (need > 0 && host.FreeSpace(data) is { } free && free < need)
        {
            return GameSetupResult.Refused("space", Strings.SetupNoSpace(Path.GetPathRoot(Path.GetFullPath(data)) ?? data, need, free));
        }
        return null;
    }

    private static ProcessStartInfo StartInfo(GameInstall install, GameSetupRequest request, RecompSetupHost host)
    {
        var args = Arguments(install, request);
        ProcessStartInfo start;
        if (host.Command is { } stand)
        {
            start = stand(args);
        }
        else
        {
            // -I: no user site packages, no PYTHON* variables, nothing imported from the working folder.
            start = new ProcessStartInfo(Python(install));
            start.ArgumentList.Add("-I");
            start.ArgumentList.Add(Script(install));
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
        }
        start.WorkingDirectory = install.Root;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        // Bytes as they come: Python writes a pipe in the system's code page, and a path may hold any character.
        start.StandardOutputEncoding = Encoding.Latin1;
        start.StandardErrorEncoding = Encoding.Latin1;
        // The launcher's environment (the tools need PATH, TEMP, the Wine prefix) without what could send this Python
        // or the ones it starts to another Python's modules. prepare.py removes them too; this covers its own start.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }
        return start;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>A line read as Latin-1 bytes: UTF-8 when it is valid UTF-8, else the system's code page.</summary>
    internal static string Decode(string raw)
    {
        if (raw.All(char.IsAscii))
        {
            return raw;
        }
        var bytes = Encoding.Latin1.GetBytes(raw);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            try
            {
                return CodePagesEncodingProvider.Instance.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)?.GetString(bytes) ?? raw;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                return raw;
            }
        }
    }

    private static async Task<GameSetupResult> DriveAsync(
        GameInstall install, GameSetupRequest request, RecompSetupHost host, IProgress<GameSetupProgress>? progress, CancellationToken cancel)
    {
        var data = DataDir(install);
        var clock = host.Clock;
        var copy = request.DumpPath is not null ? request.Mode == GameDataMode.Copy : ReadState(data)?.Copy == true;
        var tracker = new RecompSetupProgress(copy);
        var gate = new Lock();
        using var log = SetupLog.Open(request.LogFolder, clock);
        var start = StartInfo(install, request, host);
        log.Line($"mkw-recomp {install.Version} setup: game {data}, runtime {install.Root}");
        log.Line($"command: {start.FileName} {(start.ArgumentList.Count > 0 ? string.Join(' ', start.ArgumentList.Select(Quote)) : start.Arguments)}");
        log.Line($"source: {request.DumpPath ?? "(the last setup's)"}, data: {(request.DumpPath is null ? "(as the last setup)" : copy ? "copy" : "link")}");
        if (Platform.WineHost.How is { } wine)
        {
            log.Line($"wine: {wine}; the shaders step (NVIDIA's disassembler, a DLL injected into it) is not confirmed under Proton");
        }

        GameSetupResult Failed(string code, string? detail, int? exit)
        {
            var step = tracker.Step.Length > 0 ? tracker.Step : null;
            var message = Strings.SetupError(code, detail, step is null ? null : Strings.SetupStep(step, copy), exit);
            if (code == "failed" && step == "shaders")
            {
                // The one step with a download (NVIDIA's tools, by spec.py) and the one not confirmed under Wine.
                message += ". " + (Platform.WineHost.Detected ? Strings.SetupShadersProton : Strings.SetupShadersOnline);
            }
            log.Line($"result: {code} {detail} (exit {exit?.ToString(CultureInfo.InvariantCulture) ?? "none"}): {message}");
            return new GameSetupResult(false, code, message, detail, step, tracker.GameVersion, log.Path, exit);
        }

        Process process;
        var watch = clock.GetTimestamp();
        var began = clock.GetUtcNow();
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return Failed("no_setup", e.Message, null);
        }
        using (process)
        {
            process.StandardInput.Close();
            void Report()
            {
                GameSetupProgress now;
                lock (gate)
                {
                    now = tracker.At(clock.GetElapsedTime(watch));
                }
                progress?.Report(now);
            }

            async Task ReadAsync(StreamReader from, bool events)
            {
                try
                {
                    while (await from.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } raw)
                    {
                        var line = Decode(raw.TrimEnd('\r'));
                        var isEvent = false;
                        if (events)
                        {
                            lock (gate)
                            {
                                isEvent = tracker.Line(line, clock.GetElapsedTime(watch));
                            }
                        }
                        log.Line(events ? line : $"! {line}");
                        if (isEvent)
                        {
                            Report();
                        }
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // the pipe went with the process
                }
            }

            var killed = false;
            void Kill()
            {
                killed = true;
                KillTree(process, log);
            }

            Report();
            var output = ReadAsync(process.StandardOutput, events: true);
            var errors = ReadAsync(process.StandardError, events: false);
            using var beat = new CancellationTokenSource();
            var ticking = TickAsync(beat.Token);
            async Task TickAsync(CancellationToken stop)
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(Beat, clock, stop).ConfigureAwait(false);
                        string step;
                        lock (gate)
                        {
                            step = tracker.Step;
                        }
                        if (step == "warm" && WarmCount(data, began) is { } libraries)
                        {
                            lock (gate)
                            {
                                tracker.WarmCount(libraries.Done, libraries.Total);
                            }
                        }
                        Report();
                    }
                }
                catch (OperationCanceledException)
                {
                    // the setup ended
                }
            }

            try
            {
                using (cancel.Register(Kill))
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                // The lines still in the pipes; a tool that outlived the setup may hold them open.
                await Task.WhenAny(Task.WhenAll(output, errors), Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None)).ConfigureAwait(false);
            }
            finally
            {
                await beat.CancelAsync().ConfigureAwait(false);
                await ticking.ConfigureAwait(false);
            }
            var exit = process.ExitCode;
            log.Line($"exit code {exit.ToString(CultureInfo.InvariantCulture)} after {clock.GetElapsedTime(watch).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
            log.Append(Path.Combine(data, "logs", "setup.log"), began);
            if (killed || cancel.IsCancellationRequested)
            {
                log.Line("result: cancelled (the next setup continues from the last finished step)");
                throw new OperationCanceledException(Strings.SetupCancelled, cancel);
            }
            (string Code, string Detail)? error;
            bool finished;
            lock (gate)
            {
                (error, finished) = (tracker.Error, tracker.Finished);
            }
            if (error is { } told)
            {
                return Failed(told.Code, told.Detail, exit);
            }
            if (exit != 0 || !finished)
            {
                return Failed("stopped", null, exit);
            }
            Report();
            log.Line($"result: done, game {tracker.GameVersion ?? "?"}; ran {string.Join(' ', tracker.Ran)}; skipped {string.Join(' ', tracker.Skipped)}");
            return new GameSetupResult(true, null, null, null, null, tracker.GameVersion, log.Path, exit);
        }
    }

    private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;

    /// <summary>
    /// Ends the setup and everything it started. prepare.py holds its tools in a job object that
    /// goes with it; the tree is ended as well, for the moment before that job exists and for a
    /// Wine whose job objects do less.
    /// </summary>
    private static void KillTree(Process process, SetupLog log)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            log.Line("cancel: the setup and its tools were ended");
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            log.Line($"cancel: ending the process tree: {e.Message}");
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception again) when (again is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                log.Line($"cancel: ending the setup: {again.Message}");
            }
        }
    }

    /// <summary>The newest "N of M shader libraries" of this run's <c>logs/warm.log</c> (written by <c>mkw.exe --prewarm</c> every 2 s).</summary>
    internal static (long Done, long Total)? WarmCount(string data, DateTimeOffset since)
    {
        try
        {
            var path = Path.Combine(data, "logs", "warm.log");
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < since.UtcDateTime.AddSeconds(-2))
            {
                return null; // an earlier setup's
            }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Max(0, file.Length - 8192), SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.Latin1);
            var matches = WarmLine().Matches(reader.ReadToEnd());
            return matches.Count > 0
                && long.TryParse(matches[^1].Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var done)
                && long.TryParse(matches[^1].Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                    ? (done, total)
                    : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The whole log of one run, in the launcher's <c>logs/setup-&lt;stamp&gt;.log</c>: the command, every
    /// line the setup wrote with the time since its start, and at the end the setup's own
    /// <c>logs/setup.log</c> (its tools' output and timings). Paths are kept as they are: they
    /// are what a bug report needs. Failures to write are silent.
    /// </summary>
    private sealed class SetupLog : IDisposable
    {
        private const int TailBytes = 2 * 1024 * 1024;
        private readonly Lock _gate = new();
        private readonly TimeProvider _clock;
        private readonly long _start;
        private StreamWriter? _file;

        private SetupLog(string? path, StreamWriter? file, TimeProvider clock)
        {
            (Path, _file, _clock, _start) = (path, file, clock, clock.GetTimestamp());
        }

        public string? Path { get; }

        public static SetupLog Open(string? folder, TimeProvider clock)
        {
            if (folder is null)
            {
                return new SetupLog(null, null, clock);
            }
            try
            {
                Directory.CreateDirectory(folder);
                var stamp = SessionLog.FileStamp(clock.GetUtcNow());
                var path = System.IO.Path.Combine(folder, $"{LogPrefix}{stamp}.log");
                for (var n = 2; File.Exists(path); n++)
                {
                    path = System.IO.Path.Combine(folder, $"{LogPrefix}{stamp}-{n.ToString(CultureInfo.InvariantCulture)}.log");
                }
                var file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                foreach (var old in Logs(folder).Skip(KeepLogs))
                {
                    try
                    {
                        File.Delete(old);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // in use: left for the next setup
                    }
                }
                var log = new SetupLog(path, file, clock);
                log.Raw($"{SessionLog.Timestamp(clock.GetUtcNow())} setup log");
                return log;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new SetupLog(null, null, clock);
            }
        }

        private void Raw(string text)
        {
            lock (_gate)
            {
                try
                {
                    _file?.WriteLine(text);
                    _file?.Flush();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // a full disk never stops the setup
                }
            }
        }

        public void Line(string text) =>
            Raw($"{_clock.GetElapsedTime(_start).TotalSeconds.ToString("0000.0", CultureInfo.InvariantCulture)} {text}");

        /// <summary>The setup's own log of this run (its last part when it is very long).</summary>
        public void Append(string setupLog, DateTimeOffset since)
        {
            try
            {
                if (!File.Exists(setupLog) || File.GetLastWriteTimeUtc(setupLog) < since.UtcDateTime.AddSeconds(-2))
                {
                    return;
                }
                using var file = new FileStream(setupLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var cut = file.Length > TailBytes;
                file.Seek(cut ? file.Length - TailBytes : 0, SeekOrigin.Begin);
                using var reader = new StreamReader(file, new UTF8Encoding(false));
                var text = reader.ReadToEnd();
                Raw($"---- {setupLog}{(cut ? " (its end)" : "")} ----");
                Raw(text.TrimEnd());
                Raw("---- end ----");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Raw($"---- {setupLog}: {e.Message} ----");
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _file?.Dispose();
                _file = null;
            }
        }
    }

    /// <summary>The setup logs in the launcher's <c>logs/</c>, newest first (names sort by time).</summary>
    public static IReadOnlyList<string> Logs(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFiles(folder, LogPrefix + "*.log").OrderByDescending(Path.GetFileName, StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.Core.Diagnostics;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// The game's crashes (Diagnostics/): looked for when the game ends and when the launcher starts,
/// shown once (<see cref="PendingCrash"/>) until the player reports or ignores them, and the
/// report itself, for a crash or for "Found a bug?". Nothing is sent by the launcher: the report
/// is a zip on the Desktop and a GitHub issue the player's browser opens.
/// </summary>
public sealed partial class Launcher
{
    private readonly Lock _crashGate = new();
    private CrashIncidentStore? _crashStore;
    private CrashView? _pendingCrash;
    private IReadOnlyList<string> _pendingCrashIds = [];
    private DateTimeOffset _gameStarted;

    private CrashIncidentStore CrashStore
    {
        get
        {
            lock (_crashGate)
            {
                return _crashStore ??= new CrashIncidentStore(LauncherFolder);
            }
        }
    }

    public CrashView? PendingCrash
    {
        get
        {
            lock (_crashGate)
            {
                return _pendingCrash;
            }
        }
    }

    /// <summary>The folder the game process writes its crash records in: mkw.exe's.</summary>
    private string? CrashFolder()
    {
        var s = Settings;
        return GameFinder.Detect(_targets.Get(s.Target), s.Game)?.Root;
    }

    /// <summary>The game is being started (or waited for): what it leaves from now on is this session's.</summary>
    internal void GameStarting() => _gameStarted = _clock.GetUtcNow();

    /// <summary>
    /// The game ended (<paramref name="exitCode"/> null: it was not ours to wait for). A crash
    /// record written since it started, or an exit code no game ends well with, becomes the
    /// pending crash and a line in the launcher's log.
    /// </summary>
    internal void GameEnded(GameInstall install, int? exitCode)
    {
        try
        {
            var started = _gameStarted == default ? _clock.GetUtcNow().AddHours(-12) : _gameStarted;
            if (CrashStore.Detect(install.Root, started, exitCode, _clock.GetUtcNow()) is { } crash)
            {
                Note(string.Create(CultureInfo.InvariantCulture,
                    $"the game crashed: {crash.What} (crash id {crash.Id}{(crash.Dump is null ? "" : ", dump " + crash.Dump)})"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Note($"crash check: {e.Message}");
        }
        RefreshCrashes();
    }

    /// <summary>Reads the crashes not answered yet (the game's folder and the launcher's own list) into <see cref="PendingCrash"/>.</summary>
    internal void RefreshCrashes()
    {
        IReadOnlyList<CrashIncident> pending;
        try
        {
            pending = CrashStore.Pending(CrashFolder());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            pending = [];
        }
        CrashView? view = pending.Count == 0 ? null : View(pending[0], pending.Count - 1);
        bool changed;
        lock (_crashGate)
        {
            changed = _pendingCrash != view;
            (_pendingCrash, _pendingCrashIds) = (view, [.. pending.Select(i => i.Id)]);
        }
        if (changed)
        {
            RaiseChanged();
        }
    }

    private static CrashView View(CrashIncident crash, int earlier) =>
        new(crash.Id, crash.What, crash.Time, crash.Record?.Players, crash.Record?.Host, crash.Record is not null, crash.Dump is not null, earlier);

    public Task DismissCrashAsync(string id) => Task.Run(() =>
    {
        // the screen showed the newest with "and N earlier": they are answered together
        IReadOnlyList<string> ids;
        lock (_crashGate)
        {
            ids = _pendingCrashIds.Contains(id) ? _pendingCrashIds : [id];
        }
        try
        {
            CrashStore.MarkHandled(ids);
            Note($"crash {string.Join(", ", ids)}: answered, not offered again");
        }
        catch (IOException e)
        {
            // never a silent dismissal that comes back: the log says why it will
            Note($"crash: the answered list could not be saved ({e.Message}); it will be offered again");
        }
        RefreshCrashes();
    });

    public Task<BugReport> PrepareReportAsync(string? crashId) => Task.Run(() =>
    {
        var s = Settings;
        var target = _targets.Get(s.Target);
        var install = GameFinder.Detect(target, s.Game);
        var crashes = new List<CrashIncident>();
        if (crashId is not null)
        {
            var all = CrashStore.Incidents(install?.Root);
            if (all.FirstOrDefault(i => i.Id == crashId) is { } asked)
            {
                crashes.Add(asked);
                IReadOnlyList<string> together;
                lock (_crashGate)
                {
                    together = _pendingCrashIds.Contains(crashId) ? _pendingCrashIds : [];
                }
                crashes.AddRange(all.Where(i => i.Id != crashId && together.Contains(i.Id)));
            }
        }
        var crash = crashes.Count > 0 ? crashes[0] : null;
        var zipName = crash is not null ? $"mkwmp-crash-{crash.Id}.zip" : null;
        string? zip = null, problem = null, system = null;
        var onDesktop = false;
        // a first text for the zip's own copy; the final one names the zip that was written
        var draft = crash is not null ? BugReportBuilder.CrashBody(crash, Version, null, zipName, install?.DevSha256) : BugReportBuilder.BugBody(Version, null, null);
        try
        {
            // the session the report is about: the one that just ended when the crash is its own, else unknown (SaveZip takes the half day before)
            DateTimeOffset? since = _gameStarted != default && (crash is null || crash.Time >= _gameStarted.AddMinutes(-1)) ? _gameStarted : null;
            (zip, onDesktop, system) = BugReportBuilder.SaveZip(LauncherFolder, Version, target, install, crashes, draft, DesktopFolder(), Secrets(install), _clock, since);
            Note($"{(crash is null ? "bug" : "crash")} report saved: {zip}");
        }
        catch (IOException e)
        {
            problem = e.Message;
            Note($"report: the zip could not be saved: {e.Message}");
        }
        zipName = zip is null ? null : Path.GetFileName(zip);
        if (crash is not null)
        {
            var title = BugReportBuilder.CrashTitle(crash);
            var text = BugReportBuilder.CrashBody(crash, Version, system, zipName, install?.DevSha256);
            return new BugReport(true, title, BugReportBuilder.Url("crash.yml", "crash", title, "report", text), text, zip, onDesktop, problem);
        }
        var body = BugReportBuilder.BugBody(Version, system, zipName);
        var logs = zipName is null ? "" : $"Drag {zipName} here (the launcher saved it and shows where).";
        return new BugReport(false, "", BugReportBuilder.Url("bug_report.yml", "bug", "", "what", "", [("version", Version), ("logs", logs)]), body, zip, onDesktop, problem);
    });

    /// <summary>The player's Desktop (under Wine: the prefix's, which Proton and Wine link to the real one); null when there is none.</summary>
    private string? DesktopFolder()
    {
        if (_options.DesktopFolder is { } forced)
        {
            return forced;
        }
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return desktop.Length > 0 && Directory.Exists(desktop) ? desktop : null;
        }
        catch (Exception e) when (e is ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>What must not be in a minidump that leaves this PC: the PIN, this launcher's local token, and the session file's ticket and token.</summary>
    private List<string> Secrets(GameInstall? install)
    {
        var secrets = new List<string> { Settings.Pin, Token };
        secrets.AddRange(Config.Servers.Select(p => p.Pin));
        // The game holds its Nextendo token and account in memory: never in a dump that leaves this PC.
        secrets.AddRange(_nextendo.Secrets);
        var link = Config.Nextendo;
        if (link.Linked)
        {
            secrets.AddRange([link.Username, link.FriendCode, link.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        // Nextendo friends are other people: their names, friend codes and ids are in no dump either.
        secrets.AddRange(NextendoPeopleSecrets());
        if (install is null)
        {
            return secrets;
        }
        try
        {
            foreach (var folder in new[] { install.Root, install.DataRoot }.Where(f => f is { Length: > 0 }))
            {
                var path = Path.Combine(folder!, "mkwmp.toml");
                if (!File.Exists(path))
                {
                    continue;
                }
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Split('=', 2) is [var key, var value] && key.Trim() is "launcher_token" or "ticket" or "pin")
                    {
                        secrets.Add(value.Trim().Trim('"'));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // what could be read is scrubbed
        }
        return secrets;
    }
}

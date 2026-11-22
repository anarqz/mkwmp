// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Overlay;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>The console-style overlay's two objects, as the shell gets them (launcher/docs/OVERLAY.md).</summary>
public sealed partial class Launcher
{
    /// <summary>The game asks for the launcher over it; the shell answers with <see cref="OverlayBridge.ReturnToGame"/>.</summary>
    public OverlayBridge OverlayBridge => _local.Overlay;

    /// <summary>Standing by for a game the launcher did not start, and a second launcher's "show yourself".</summary>
    public OverlayHost OverlayHost => _local.Instance;

    /// <summary>
    /// Records where this launcher is and how to reach it (<see cref="LauncherRecord"/>), for the
    /// mod and for a second launcher. Call once the local API is up; false when it is not, or the
    /// file cannot be written.
    /// </summary>
    public bool PublishRecord(string? path = null)
    {
        if (LocalApiPort <= 0 || Environment.ProcessPath is not { } exe)
        {
            return false;
        }
        var record = new LauncherRecord(exe.Replace('\\', '/'), Environment.ProcessId, LocalApiPort, Token);
        var written = record.Write(path);
        if (path is null)
        {
            PublishModRecord(record);
        }
        return written;
    }

    private string? _modRecordAt;

    /// <summary>
    /// The copy the mod reads, in the game's mod folder (<c>&lt;data&gt;\mods\mkwmp\launcher.json</c>),
    /// so a game started without the launcher finds it: portable, no file in the user's profile.
    /// Written again when the game folder changes; nothing when the mod is not installed there.
    /// </summary>
    private void PublishModRecord(LauncherRecord? record = null)
    {
        if (LocalApiPort <= 0 || Environment.ProcessPath is not { } exe || Game.Install is not { } install)
        {
            return;
        }
        var mod = Path.Combine(install.DataRoot ?? install.Root, "mods", "mkwmp");
        var at = Path.Combine(mod, "launcher.json");
        if (!Directory.Exists(mod) || (record is null && at == _modRecordAt))
        {
            return;
        }
        record ??= new LauncherRecord(exe.Replace('\\', '/'), Environment.ProcessId, LocalApiPort, Token);
        if (record.Write(at))
        {
            _modRecordAt = at;
        }
    }

    /// <summary>Stands by for the game of <paramref name="args"/> (the overlay host's start); false when the local API is not up or the game is gone.</summary>
    public bool AttachOverlayHost(OverlayHostArgs args) => LocalApiPort > 0 && _local.Instance.Attach(args, LocalApiPort, Token);

    /// <summary>
    /// The process id of the running game of the selected target (the first, when several run), for
    /// the window to find the game's window; 0 when none is seen. Reads the process list: not for a tight loop.
    /// </summary>
    public int FindGamePid() => GamePids() is [var first, ..] ? first : 0;

    /// <summary>
    /// That process id is the selected target's game as this process sees it (under Wine: in this
    /// prefix; a game of another prefix can reach the local API but no window of ours can be put over it).
    /// </summary>
    public bool IsGameProcess(int pid) => pid > 0 && GamePids().Contains(pid);

    private IReadOnlyList<int> GamePids()
    {
        var s = Settings;
        var target = _targets.Get(s.Target);
        return GameFinder.Detect(target, s.Game) is { } install ? target.ProcessIds(install) : [];
    }

    /// <summary>
    /// What the mod reads as <c>launcher_exe</c> in mkwmp.toml: this launcher's executable (never
    /// the dotnet host or a test runner), so that a later start of the game without the launcher
    /// can bring it up as the overlay host.
    /// </summary>
    private string? LauncherExe => (_options.Executable ?? LauncherOptions.SelfExecutable())?.Replace('\\', '/');

    /// <summary>
    /// PLAY is refused while the launcher stands by for a game that runs by itself (the mod signed
    /// in and holds the seat): a second session would start a second game and fight for the name.
    /// </summary>
    private string? HostedGameProblem => _local.Instance.ParentPid != 0 ? Strings.GameRunsAlready : null;

    /// <summary>The views follow the game the launcher stands by for (<see cref="PlayProblem"/>).</summary>
    private void WatchOverlayHost()
    {
        _local.Instance.ParentAttached += (_, _) => Mutate(() => { });
        _local.Instance.ParentExited += (_, _) =>
        {
            // the game it stood by for is closed now: an account signed in over it reaches the mod's next start
            Volatile.Write(ref _modSynced, null);
            SyncModSettings();
            Mutate(() => { });
        };
    }
}

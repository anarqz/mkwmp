// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Overlay;

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
    public bool PublishRecord(string? path = null) =>
        LocalApiPort > 0 && Environment.ProcessPath is { } exe
        && new LauncherRecord(exe.Replace('\\', '/'), Environment.ProcessId, LocalApiPort, Token).Write(path);

    /// <summary>Stands by for the game of <paramref name="args"/> (the overlay host's start); false when the local API is not up or the game is gone.</summary>
    public bool AttachOverlayHost(OverlayHostArgs args) => LocalApiPort > 0 && _local.Instance.Attach(args, LocalApiPort, Token);
}
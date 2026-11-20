// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core;
using Mkwmp.Core.Overlay;

namespace Mkwmp.App.Overlay;

/// <summary>
/// What App.xaml.cs needs for the overlay host and the single instance (launcher/docs/OVERLAY.md,
/// "Without the launcher"), in two calls:
/// <code>
/// // 1. first thing in OnStartup, on the UI thread, before the Launcher is made:
/// var startup = OverlayStartup.Begin(e.Args);
/// if (startup is null) { Shutdown(0); return; }   // a running launcher took the request over
///
/// // 2. after launcher.StartAsync(...) and the window exist:
/// startup.Attach(launcher, show: () => { window.Show(); window.Activate(); }, exit: Shutdown);
/// if (!startup.Hosted) window.Show();              // hosted: no window until the player asks
/// </code>
/// While <see cref="Hosted"/> the launcher stands by for the game that started it: no window, no
/// tray icon, no seat taken (the mod holds the seat), no update prompt, and it leaves when that
/// game ends. A normal start by the player while it stands by lands in <c>show</c>, and from then
/// on it is the ordinary launcher (<see cref="Hosted"/> false: it stays when the game ends).
/// </summary>
public sealed class OverlayStartup : IDisposable
{
    private readonly SingleInstance? _instance;
    private volatile bool _hosted;

    private OverlayStartup(SingleInstance? instance, OverlayHostArgs? host)
    {
        _instance = instance;
        HostArgs = host;
        _hosted = host is not null;
    }

    /// <summary>The command line asked for the overlay host (<c>--overlay-host --parent-pid N --session FILE</c>).</summary>
    public OverlayHostArgs? HostArgs { get; }

    /// <summary>Standing by without a window. False after the player opened the launcher.</summary>
    public bool Hosted => _hosted;

    /// <summary>
    /// Null when this process should exit now: another launcher of this user runs and took the
    /// request (show its window; or stand by for this game), or the overlay host was asked for
    /// and the running launcher did not answer (the mod asks again at the next trigger).
    /// Call it on the UI thread: the instance's mutex belongs to the thread that took it.
    /// </summary>
    public static OverlayStartup? Begin(string[] args)
    {
        var host = OverlayHostArgs.Parse(args);
        if (args.Contains("--overlay-host") && host is null)
        {
            return null; // asked for wrongly: nothing to stand by for
        }
        var instance = SingleInstance.TryAcquire();
        if (instance is not null)
        {
            return new OverlayStartup(instance, host);
        }
        // The running launcher may still be starting: a few tries.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (Task.Run(() => SingleInstance.HandOffAsync(host)).GetAwaiter().GetResult())
            {
                return null;
            }
            Thread.Sleep(500);
        }
        // It holds the name but does not answer (no local API: another launcher's port problem). The
        // player still gets a window; a host is not worth a second, deaf launcher.
        return host is null ? new OverlayStartup(null, null) : null;
    }

    /// <summary>
    /// After the launcher started (its local API is up): records where the launcher is, stands by
    /// for the game when hosted, and wires the two events. <paramref name="show"/> and
    /// <paramref name="exit"/> are called on the UI thread.
    /// </summary>
    public void Attach(Launcher launcher, Action show, Action exit)
    {
        var ui = System.Windows.Application.Current.Dispatcher;
        _ = launcher.PublishRecord();
        launcher.OverlayHost.ShowRequested += (_, _) =>
        {
            _hosted = false;
            _ = ui.BeginInvoke(show);
        };
        launcher.OverlayHost.ParentExited += (_, _) =>
        {
            launcher.OverlayBridge.GameExited();
            if (_hosted)
            {
                _ = ui.BeginInvoke(exit);
            }
        };
        if (HostArgs is { } host && !launcher.AttachOverlayHost(host))
        {
            // the game is already gone, or the local API is not up: nothing to stand by for
            _ = ui.BeginInvoke(exit);
        }
    }

    public void Dispose() => _instance?.Dispose();
}

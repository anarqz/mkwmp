// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Mkwmp.App.Platform;

/// <summary>
/// How WPF draws under Wine / Proton.
/// <para>
/// The heartbeat: seen on a SteamOS device (Desktop Mode, Proton 11), after a click the window kept showing the frame
/// before it (Play instead of Stop, the clock a minute old) until the pointer moved; the next change of anything showed
/// it all at once. The likeliest reading (not proven): a frame drawn and not yet shown, the screen one frame behind,
/// and an idle launcher draws no next frame to push it out. Under Wine only, while the window is visible, one pixel in its corner changes four times a second, by
/// one step of a colour no one can see: there is always a next frame, so a change is on the screen within a quarter of
/// a second. It costs a 1x1 redraw. It could not be made to happen again by a test on that device (pointer and touch
/// taps, the game started and closed, the overlay), so it is a precaution: <c>MKWMP_WINE_HEARTBEAT=0</c> turns it off
/// to compare.
/// </para>
/// <para>
/// <c>MKWMP_RENDER=software</c> or <c>hardware</c> picks WPF's render mode for a run (on Windows too). Software is not
/// the default under Wine: on that device it took about three times the processor (33% of a core idle, 62% while the
/// focus moved, against 9% and 25%).
/// </para>
/// </summary>
internal static class WineRendering
{
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(250);

    /// <summary>Call once, before the first window is made.</summary>
    public static void Apply()
    {
        var asked = Environment.GetEnvironmentVariable("MKWMP_RENDER")?.Trim().ToLowerInvariant();
        if (asked == "software")
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
        else if (asked == "hardware")
        {
            RenderOptions.ProcessRenderMode = RenderMode.Default;
        }
        if (SystemStatus.IsWine || asked is not null)
        {
            App.Log($"render: {RenderOptions.ProcessRenderMode} (tier {RenderCapability.Tier >> 16}){(asked is null ? "" : $", MKWMP_RENDER={asked}")}"
                + (SystemStatus.IsWine ? $", heartbeat {(HeartbeatWanted ? "on" : "off")}" : ""));
        }
    }

    private static bool HeartbeatWanted => SystemStatus.IsWine && Environment.GetEnvironmentVariable("MKWMP_WINE_HEARTBEAT") != "0";

    /// <summary>The heartbeat for <paramref name="window"/> (its content must be a panel); nothing on Windows.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!HeartbeatWanted || window.Content is not Panel root)
        {
            return;
        }
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        root.Children.Add(new Rectangle
        {
            Width = 1, Height = 1, Fill = brush, IsHitTestVisible = false, Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        });
        var odd = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = Beat };
        timer.Tick += (_, _) => brush.Color = Color.FromArgb(1, 0, 0, (odd = !odd) ? (byte)1 : (byte)0);
        void Follow()
        {
            timer.IsEnabled = window.IsVisible && window.WindowState != WindowState.Minimized;
        }
        window.IsVisibleChanged += (_, _) => Follow();
        window.StateChanged += (_, _) => Follow();
        window.Closed += (_, _) => timer.Stop();
        Follow();
    }
}

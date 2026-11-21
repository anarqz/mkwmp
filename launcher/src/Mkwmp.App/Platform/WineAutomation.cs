// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Interop;

namespace Mkwmp.App.Platform;

/// <summary>
/// Under Wine / Proton the window does not answer UI Automation. Proton starts Xalia (its gamepad
/// helper) beside every program, and Xalia asks the window for its automation tree
/// (<c>WM_GETOBJECT</c>); WPF then builds providers for its elements and serves them from inside
/// the launcher's process through Wine's own <c>uiautomationcore</c>, native code that WPF was
/// never run against, on every focus change and every progress step. The launcher steers itself
/// with a pad and no assistive program can read a WPF window through Wine, so nothing is lost by
/// not answering: Xalia sees a plain window (and draws no second focus frame), and none of that
/// native code runs in this process. On Windows nothing changes: screen readers get the full tree.
/// </summary>
internal static class WineAutomation
{
    private const int WmGetObject = 0x003D;

    /// <summary>Call before the window is shown.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        // MKWMP_WINE_AUTOMATION=1 answers as on Windows: to compare when a fault is looked for.
        if (!SystemStatus.IsWine || Environment.GetEnvironmentVariable("MKWMP_WINE_AUTOMATION") == "1")
        {
            return;
        }
        if (PresentationSource.FromVisual(window) is HwndSource now)
        {
            now.AddHook(Hook);
            return;
        }
        window.SourceInitialized += (_, _) => (PresentationSource.FromVisual(window) as HwndSource)?.AddHook(Hook);
    }

    /// <summary>A hook added with <see cref="HwndSource.AddHook"/> sees a message before WPF's own handling of it.</summary>
    private static IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetObject)
        {
            handled = true; // 0: "no object", as a window without automation answers
        }
        return IntPtr.Zero;
    }
}

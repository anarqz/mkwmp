// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mkwmp.UpdateHelper;

/// <summary>
/// The helper's window: small, centred, without a frame, not closable while files are being
/// swapped. One per process (plain statics: the window procedure is a static function). The
/// install runs on another thread and reports through <see cref="Show"/>, <see cref="Fail"/> and
/// <see cref="Close"/>; this thread only paints.
/// </summary>
internal static unsafe class UpdaterWindow
{
    private const uint WmRefresh = Native.WmApp + 1;
    private const uint WmFinish = Native.WmApp + 2;
    private static readonly Lock Gate = new();
    private static readonly ManualResetEventSlim Dismissed = new(false);
    private static readonly long Began = Stopwatch.GetTimestamp();
    private static Scene _scene;
    private static nint _hwnd;
    private static double _scale = 1;

    /// <summary>Creates and shows the window; false when there is no desktop to show it on (the install goes on without it).</summary>
    public static bool Create(Scene first)
    {
        try
        {
            if (!Painter.Start())
            {
                return false;
            }
            _scene = first;
            try
            {
                _scale = Math.Clamp(Native.GetDpiForSystem() / 96.0, 1, 4);
            }
            catch (EntryPointNotFoundException)
            {
                _scale = 1;
            }
            var instance = Native.GetModuleHandle(null);
            var (w, h) = ((int)Math.Round(Painter.Width * _scale), (int)Math.Round(Painter.Height * _scale));
            fixed (char* name = "MkwmpUpdater", title = "MKWMP")
            {
                var wc = new Native.WndClassEx
                {
                    Size = (uint)sizeof(Native.WndClassEx),
                    WndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
                    Instance = instance,
                    Icon = Native.LoadIcon(instance, 32512), // the application icon (the launcher's)
                    Cursor = Native.LoadCursor(0, 32512), // the arrow
                    ClassName = name,
                };
                if (Native.RegisterClassEx(&wc) == 0)
                {
                    return false;
                }
                var (screenW, screenH) = (Native.GetSystemMetrics(0), Native.GetSystemMetrics(1));
                _hwnd = Native.CreateWindowEx(Native.WsExAppWindow, name, title, Native.WsPopup, Math.Max(0, (screenW - w) / 2), Math.Max(0, (screenH - h) / 2), w, h, 0, 0, instance, 0);
            }
            if (_hwnd == 0)
            {
                return false;
            }
            RoundCorners();
            _ = Native.SetTimer(_hwnd, 1, 33, 0);
            _ = Native.ShowWindow(_hwnd, 5);
            _ = Native.SetForegroundWindow(_hwnd);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Windows 11 rounds the corners and draws the border in our colour; elsewhere (Windows 10, Wine) the window stays a bordered rectangle.</summary>
    private static void RoundCorners()
    {
        try
        {
            var round = 2; // DWMWCP_ROUND
            if (Native.DwmSetWindowAttribute(_hwnd, 33, &round, sizeof(int)) != 0)
            {
                return;
            }
            var border = _scene.Dark ? 0x005C5656u : 0x00C6C6C6u; // COLORREF (BGR) of Brush.Rule
            if (Native.DwmSetWindowAttribute(_hwnd, 34, &border, sizeof(uint)) == 0)
            {
                _scene = _scene with { Border = false }; // DWM draws it, following the rounded corners
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no DWM: a rectangle
        }
    }

    /// <summary>The message loop; returns when the window is gone.</summary>
    public static void Run()
    {
        Native.Msg message;
        while (Native.GetMessage(&message, 0, 0, 0) > 0)
        {
            _ = Native.TranslateMessage(&message);
            _ = Native.DispatchMessage(&message);
        }
    }

    /// <summary>Any thread: the step's text and the bar.</summary>
    public static void Show(string text, double progress)
    {
        lock (Gate)
        {
            _scene = _scene with { Text = text, Progress = progress, Failed = false };
        }
        _ = Native.PostMessage(_hwnd, WmRefresh, 0, 0);
    }

    /// <summary>Any thread: the failure, until the player clicks or presses a key, or <paramref name="patience"/> passes.</summary>
    public static void Fail(string title, string body, TimeSpan patience)
    {
        lock (Gate)
        {
            _scene = _scene with { Title = title, Text = body, Failed = true };
        }
        Dismissed.Reset();
        _ = Native.PostMessage(_hwnd, WmRefresh, 0, 0);
        _ = Dismissed.Wait(patience);
    }

    /// <summary>Any thread: closes the window and ends <see cref="Run"/>.</summary>
    public static void Close() => _ = Native.PostMessage(_hwnd, WmFinish, 0, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case Native.WmEraseBkgnd:
                return 1; // every frame covers the whole window
            case Native.WmPaint:
                Paint(hwnd);
                return 0;
            case Native.WmTimer:
            case WmRefresh:
                _ = Native.InvalidateRect(hwnd, null, 0);
                return 0;
            case Native.WmNcHitTest:
                // No frame: the whole window drags, except when a click is what closes it.
                return _scene.Failed ? 1 : Native.HtCaption;
            case Native.WmLButtonUp:
            case Native.WmKeyDown:
                if (_scene.Failed)
                {
                    Dismissed.Set();
                }
                return 0;
            case Native.WmClose:
                // Alt+F4 never stops a swap half way; after a failure it is the same as a click.
                Dismissed.Set();
                return 0;
            case WmFinish:
                _ = Native.DestroyWindow(hwnd);
                return 0;
            case Native.WmDestroy:
                Native.PostQuitMessage(0);
                return 0;
            default:
                return Native.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private static void Paint(nint hwnd)
    {
        Native.PaintStruct paint;
        var hdc = Native.BeginPaint(hwnd, &paint);
        Scene scene;
        lock (Gate)
        {
            scene = _scene with { Phase = Stopwatch.GetElapsedTime(Began).TotalSeconds / 1.6 % 1.0, Rounded = false };
        }
        var bitmap = Painter.Render(scene, _scale);
        if (bitmap != 0)
        {
            if (Native.GdipCreateFromHDC(hdc, out var g) == 0)
            {
                _ = Native.GdipDrawImageI(g, bitmap, 0, 0);
                _ = Native.GdipDeleteGraphics(g);
            }
            _ = Native.GdipDisposeImage(bitmap);
        }
        _ = Native.EndPaint(hwnd, &paint);
    }
}

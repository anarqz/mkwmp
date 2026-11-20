// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mkwmp.Core.Overlay;
using Mkwmp.Core.Platform;

namespace Mkwmp.App.Overlay;

/// <summary>
/// The window work of the console-style overlay (launcher/docs/OVERLAY.md §4): finding the game's
/// window, putting the launcher over it and giving the game back.
/// <para>
/// One rule serves Windows and gamescope alike: while the game is in front the launcher's window
/// is hidden, not merely behind. On Windows the game then has the foreground with nothing of ours
/// over a full-screen picture; under gamescope (Steam Deck Game Mode) the window shown is, among
/// an app's windows, the one mapped last, so showing the launcher puts it in front and hiding it
/// leaves the game as the only one.
/// </para>
/// UI thread only, except <see cref="Find"/> and <see cref="ToBitmap"/>.
/// </summary>
public static class GameWindow
{
    /// <summary>The largest visible top-level window of that process; zero when it has none (yet).</summary>
    public static IntPtr Find(int pid)
    {
        if (pid <= 0)
        {
            return IntPtr.Zero;
        }
        var best = IntPtr.Zero;
        long area = -1;
        EnumWindows(
            (hwnd, _) =>
            {
                if (GetWindowThreadProcessId(hwnd, out var owner) != 0 && owner == (uint)pid && IsWindowVisible(hwnd) && GetWindow(hwnd, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowRect(hwnd, out var r))
                {
                    var size = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (size > area)
                    {
                        (best, area) = (hwnd, size);
                    }
                }
                return true;
            },
            IntPtr.Zero);
        return best;
    }

    /// <summary>The game's window is the foreground one: a trigger of the launcher's own (GuideWatcher) counts only then.</summary>
    public static bool IsForeground(int pid)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || pid <= 0)
        {
            return false;
        }
        _ = GetWindowThreadProcessId(foreground, out var owner);
        return owner == (uint)pid;
    }

    /// <summary>
    /// Shows the launcher's window over the game and gives it the keyboard and the controller.
    /// The mod called AllowSetForegroundWindow before it asked, so Windows lets this process take
    /// the foreground; without the mod (the launcher's own Guide watch) the input queues are
    /// joined for the moment of the switch instead.
    /// </summary>
    public static void ShowOver(Window launcher)
    {
        if (launcher.WindowState == WindowState.Minimized)
        {
            launcher.WindowState = WindowState.Normal;
        }
        launcher.Show(); // gamescope: mapped last, so shown
        var hwnd = new WindowInteropHelper(launcher).EnsureHandle();
        if (!SetForegroundWindow(hwnd))
        {
            var from = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var me = GetCurrentThreadId();
            if (from != 0 && from != me && AttachThreadInput(me, from, true))
            {
                _ = BringWindowToTop(hwnd);
                _ = SetForegroundWindow(hwnd);
                _ = AttachThreadInput(me, from, false);
            }
        }
        // over an exclusive full-screen game the foreground alone may not be enough: on top for the switch, then not
        launcher.Topmost = true;
        launcher.Topmost = false;
        _ = launcher.Activate();
        _ = launcher.Focus();
    }

    /// <summary>
    /// Gives the game back: the launcher's window goes away (<paramref name="hideLauncher"/>; always
    /// under Wine, where gamescope then shows the game) and the game's window takes the foreground.
    /// Call it when the grow-back animation has ended, then <c>OverlayBridge.ReturnToGame()</c>.
    /// </summary>
    public static void GiveBack(Window launcher, IntPtr game, bool hideLauncher = true)
    {
        if (hideLauncher || WineHost.Detected)
        {
            launcher.Hide();
        }
        if (game == IntPtr.Zero)
        {
            return;
        }
        if (IsIconic(game))
        {
            _ = ShowWindow(game, 9 /* SW_RESTORE */);
        }
        _ = SetForegroundWindow(game); // this process has the foreground: allowed
    }

    /// <summary>
    /// Where the game draws, in screen pixels: the launcher covers exactly that for the overlay, so
    /// the picture shrinks from where the game was (a window as much as a full screen). Null for
    /// no window, a minimised one, or one too small to be a game's.
    /// </summary>
    public static Int32Rect? ClientPixels(IntPtr game)
    {
        if (game == IntPtr.Zero || IsIconic(game) || !GetClientRect(game, out var client) || client.Right < 320 || client.Bottom < 200)
        {
            return null;
        }
        var origin = default(Point32);
        return ClientToScreen(game, ref origin) ? new Int32Rect(origin.X, origin.Y, client.Right, client.Bottom) : null;
    }

    /// <summary>Moves a (borderless, not maximised) window onto that rectangle of the screen, without activating it.</summary>
    public static void Place(Window launcher, Int32Rect pixels)
    {
        var hwnd = new WindowInteropHelper(launcher).EnsureHandle();
        _ = SetWindowPos(hwnd, IntPtr.Zero, pixels.X, pixels.Y, pixels.Width, pixels.Height, 0x0014 /* SWP_NOZORDER | SWP_NOACTIVATE */);
    }

    /// <summary>The frame as an image for the Play tile and the shrink animation (frozen: any thread may make it).</summary>
    public static BitmapSource ToBitmap(OverlayFrame frame)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Bgra, frame.Stride);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// The fallback picture when the game sent none (plugin API 3, no mod): the screen where the
    /// game's window is, copied before the launcher shows itself. Windows only: under Wine a copy
    /// of the screen does not hold a Vulkan window's picture, so there is no thumbnail then.
    /// Null when there is nothing to copy or the copy is black (an exclusive full-screen game).
    /// </summary>
    public static OverlayFrame? Capture(IntPtr game)
    {
        if (WineHost.Detected || game == IntPtr.Zero || IsIconic(game) || !GetClientRect(game, out var client))
        {
            return null;
        }
        var origin = default(Point32);
        if (!ClientToScreen(game, ref origin))
        {
            return null;
        }
        int w = client.Right, h = client.Bottom;
        if (w < 16 || h < 16)
        {
            return null;
        }
        // scaled down by GDI to at most 1280 x 720, as the mod's picture is
        var scale = Math.Min(1.0, Math.Min(1280.0 / w, 720.0 / h));
        int ow = Math.Max(16, (int)(w * scale)), oh = Math.Max(16, (int)(h * scale));
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, ow, oh);
        try
        {
            var old = SelectObject(memory, bitmap);
            _ = SetStretchBltMode(memory, 4 /* HALFTONE */);
            var ok = StretchBlt(memory, 0, 0, ow, oh, screen, origin.X, origin.Y, w, h, 0x00CC0020 /* SRCCOPY */);
            _ = SelectObject(memory, old);
            if (!ok)
            {
                return null;
            }
            var info = new BitmapInfoHeader { Size = 40, Width = ow, Height = -oh, Planes = 1, BitCount = 32 };
            var pixels = new byte[ow * oh * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)oh, pixels, ref info, 0) != oh)
            {
                return null;
            }
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
            var frame = new OverlayFrame(ow, oh, pixels);
            return frame.IsBlank() ? null : frame;
        }
        finally
        {
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ── ending the game ("Quit Game", launcher/docs/OVERLAY.md §5) ──

    /// <summary>That process still runs (under Wine: a process of this prefix).</summary>
    public static bool Alive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the game to close as its window's close button does (WM_CLOSE, posted: never waits for
    /// the game): mkw-recomp then finishes its saves, tells its mods and ends. False when the
    /// process has no window to ask.
    /// </summary>
    public static bool RequestClose(int pid)
    {
        var hwnd = Find(pid);
        return hwnd != IntPtr.Zero && PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Ends the game's process and the processes it started, at once: nothing is saved. Under
    /// Wine <paramref name="pid"/> is the Wine-side id (the mod's, or the prefix's process list),
    /// and the kill is Wine's. False when the process could not be ended (another user's, or gone).
    /// </summary>
    public static bool Kill(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId)
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is NotSupportedException or AggregateException or Win32Exception or InvalidOperationException)
            {
                process.Kill(); // the tree could not be walked (Wine) or one child refused: the game itself
            }
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return !Alive(pid);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

#pragma warning disable SYSLIB1054 // plain DllImport, as GamepadInput: blittable, no unsafe code in this project
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point32 point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint to, [MarshalAs(UnmanagedType.Bool)] bool on);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);


    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(IntPtr dest, int x, int y, int cx, int cy, IntPtr src, int sx, int sy, int scx, int scy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
#pragma warning restore SYSLIB1054
}

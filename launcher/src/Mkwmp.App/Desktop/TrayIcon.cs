// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Mkwmp.App.Desktop;

/// <summary>
/// The system tray icon (Shell_NotifyIcon, no WinForms): tooltip, left click opens, right click
/// shows a native menu (Open / Quit), and <see cref="ShowBalloon"/> is the Windows notification
/// for news that arrives while the window is hidden. Port of rust/client's tray.rs. Created only
/// on real Windows (see <see cref="DesktopIntegration"/>); <see cref="TryCreate"/> gives null when
/// the shell has no tray.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x4D; // WM_APP + 'M'
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int NinBalloonUserClick = 0x0405;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10;
    private const uint NiifUser = 4;
    private const uint MfString = 0, MfSeparator = 0x800;
    private const uint TpmRightButton = 2, TpmReturnCmd = 0x100, TpmNoNotify = 0x80;
    private const uint IconId = 1;

    private readonly HwndSource _source;
    private readonly string _tooltip;
    private readonly string _openText;
    private readonly string _quitText;
    private readonly uint _taskbarCreated;
    private nint _icon;
    private bool _ownsIcon;
    private bool _added;
    private bool _disposed;

    private TrayIcon(string tooltip, string openText, string quitText)
    {
        _tooltip = tooltip;
        _openText = openText;
        _quitText = quitText;
        // a window nobody sees: it only receives the icon's messages
        _source = new HwndSource(new HwndSourceParameters("MKWMP tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW: not in the task bar, not in Alt+Tab
        });
        _source.AddHook(OnMessage);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated"); // Explorer restarted: the icon has to be added again
        LoadIcon();
    }

    /// <summary>The icon was clicked (or its notification, or "Open" in its menu).</summary>
    public event EventHandler? OpenRequested;

    /// <summary>"Quit" in its menu.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>The icon in the tray, or null when there is no tray to put it in (never throws).</summary>
    public static TrayIcon? TryCreate(string tooltip, string openText, string quitText)
    {
        TrayIcon? tray = null;
        try
        {
            tray = new TrayIcon(tooltip, openText, quitText);
            if (tray.Add())
            {
                return tray;
            }
            App.Log("tray: the shell refused the icon");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.Log("tray: " + e.Message);
        }
        tray?.Dispose();
        return null;
    }

    /// <summary>A Windows notification from the icon; false when the shell did not take it.</summary>
    public bool ShowBalloon(string title, string body)
    {
        if (_disposed || !_added)
        {
            return false;
        }
        var data = Data(NifInfo);
        data.szInfoTitle = Fit(title, 63);
        data.szInfo = Fit(body.Length > 0 ? body : title, 255);
        data.dwInfoFlags = NiifUser;
        return Notify(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_added)
        {
            var data = Data(0);
            Notify(NimDelete, ref data);
            _added = false;
        }
        _source.RemoveHook(OnMessage);
        _source.Dispose();
        if (_ownsIcon && _icon != 0)
        {
            DestroyIcon(_icon);
        }
        _icon = 0;
    }

    private static string Fit(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private void LoadIcon()
    {
        // the exe's own icon (the launcher's mark); the system's application icon when run through the dotnet host
        if (Environment.ProcessPath is { } exe)
        {
            var small = new nint[1];
            if (ExtractIconExW(exe, 0, null, small, 1) > 0 && small[0] != 0)
            {
                _icon = small[0];
                _ownsIcon = true;
                return;
            }
        }
        _icon = LoadIconW(0, 32512); // IDI_APPLICATION
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _source.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private bool Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip);
        _added = Notify(NimAdd, ref data);
        return _added;
    }

    private static bool Notify(uint message, ref NotifyIconData data)
    {
        try
        {
            return Shell_NotifyIconW(message, ref data);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            handled = true;
            switch ((int)(lParam & 0xFFFF))
            {
                case WmLButtonUp or NinBalloonUserClick:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WmRButtonUp or WmContextMenu:
                    ShowMenu(hwnd);
                    break;
            }
        }
        else if (_taskbarCreated != 0 && message == (int)_taskbarCreated && !_disposed)
        {
            Add();
        }
        return 0;
    }

    private void ShowMenu(nint hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }
        try
        {
            AppendMenuW(menu, MfString, 1, _openText);
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, 2, _quitText);
            GetCursorPos(out var at);
            SetForegroundWindow(hwnd); // or the menu would not close on a click elsewhere
            var chosen = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify, at.X, at.Y, hwnd, 0);
            PostMessageW(hwnd, 0, 0, 0);
            switch (chosen)
            {
                case 1:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case 2:
                    QuitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>Brings a window of this process to the front (the tray click gives us the right to).</summary>
    public static void BringToFront(nint window)
    {
        if (window != 0)
        {
            SetForegroundWindow(window);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

#pragma warning disable SYSLIB1054 // plain DllImport: the struct has inline strings, and no unsafe code in this project
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ExtractIconExW(string file, int index, nint[]? large, nint[]? small, uint count);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LoadIconW(nint instance, nint name);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
#pragma warning restore SYSLIB1054
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;

namespace Mkwmp.UpdateHelper;

/// <summary>The few Win32 and GDI+ (flat API) entry points of the window. All of them exist in Wine.</summary>
internal static unsafe partial class Native
{
    public const uint WsPopup = 0x80000000;
    public const uint WsExAppWindow = 0x00040000;
    public const uint WmDestroy = 0x0002;
    public const uint WmPaint = 0x000F;
    public const uint WmClose = 0x0010;
    public const uint WmEraseBkgnd = 0x0014;
    public const uint WmNcHitTest = 0x0084;
    public const uint WmKeyDown = 0x0100;
    public const uint WmTimer = 0x0113;
    public const uint WmLButtonUp = 0x0202;
    public const uint WmApp = 0x8000;
    public const int HtCaption = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public nint Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GdiplusStartupInput
    {
        public uint Version;
        public nint DebugCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    // ---- kernel32 / user32 / dwmapi / shlwapi ------------------------------------------------

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW")]
    public static partial nint GetModuleHandle(char* name);

    [LibraryImport("user32", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(WndClassEx* wc);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW")]
    public static partial nint CreateWindowEx(uint exStyle, char* className, char* title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32")]
    public static partial int ShowWindow(nint hwnd, int show);

    [LibraryImport("user32")]
    public static partial int SetForegroundWindow(nint hwnd);

    [LibraryImport("user32", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(Msg* message, nint hwnd, uint min, uint max);

    [LibraryImport("user32")]
    public static partial int TranslateMessage(Msg* message);

    [LibraryImport("user32", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(Msg* message);

    [LibraryImport("user32", EntryPoint = "PostMessageW")]
    public static partial int PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32")]
    public static partial void PostQuitMessage(int code);

    [LibraryImport("user32")]
    public static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32")]
    public static partial int InvalidateRect(nint hwnd, Rect* rect, int erase);

    [LibraryImport("user32")]
    public static partial nint BeginPaint(nint hwnd, PaintStruct* paint);

    [LibraryImport("user32")]
    public static partial int EndPaint(nint hwnd, PaintStruct* paint);

    [LibraryImport("user32")]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);

    [LibraryImport("user32")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("user32", EntryPoint = "LoadIconW")]
    public static partial nint LoadIcon(nint instance, nint name);

    [LibraryImport("user32")]
    public static partial uint GetDpiForSystem();

    [LibraryImport("dwmapi")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    [LibraryImport("shlwapi")]
    public static partial nint SHCreateMemStream(byte* data, uint length);

    // ---- gdiplus -----------------------------------------------------------------------------

    [LibraryImport("gdiplus")]
    public static partial int GdiplusStartup(out nuint token, GdiplusStartupInput* input, nint output);

    [LibraryImport("gdiplus")]
    public static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan0, out nint bitmap);

    [LibraryImport("gdiplus")]
    public static partial int GdipCreateBitmapFromStream(nint stream, out nint bitmap);

    [LibraryImport("gdiplus")]
    public static partial int GdipDisposeImage(nint image);

    [LibraryImport("gdiplus")]
    public static partial int GdipGetImageGraphicsContext(nint image, out nint graphics);

    [LibraryImport("gdiplus")]
    public static partial int GdipCreateFromHDC(nint hdc, out nint graphics);

    [LibraryImport("gdiplus")]
    public static partial int GdipDeleteGraphics(nint graphics);

    [LibraryImport("gdiplus")]
    public static partial int GdipSetSmoothingMode(nint graphics, int mode);

    [LibraryImport("gdiplus")]
    public static partial int GdipSetTextRenderingHint(nint graphics, int hint);

    [LibraryImport("gdiplus")]
    public static partial int GdipSetInterpolationMode(nint graphics, int mode);

    [LibraryImport("gdiplus")]
    public static partial int GdipSetPixelOffsetMode(nint graphics, int mode);

    [LibraryImport("gdiplus")]
    public static partial int GdipGraphicsClear(nint graphics, uint argb);

    [LibraryImport("gdiplus")]
    public static partial int GdipCreateSolidFill(uint argb, out nint brush);

    [LibraryImport("gdiplus")]
    public static partial int GdipDeleteBrush(nint brush);

    [LibraryImport("gdiplus")]
    public static partial int GdipCreatePen1(uint argb, float width, int unit, out nint pen);

    [LibraryImport("gdiplus")]
    public static partial int GdipDeletePen(nint pen);

    [LibraryImport("gdiplus")]
    public static partial int GdipCreatePath(int fillMode, out nint path);

    [LibraryImport("gdiplus")]
    public static partial int GdipDeletePath(nint path);

    [LibraryImport("gdiplus")]
    public static partial int GdipAddPathArc(nint path, float x, float y, float width, float height, float start, float sweep);

    [LibraryImport("gdiplus")]
    public static partial int GdipClosePathFigure(nint path);

    [LibraryImport("gdiplus")]
    public static partial int GdipFillPath(nint graphics, nint brush, nint path);

    [LibraryImport("gdiplus")]
    public static partial int GdipDrawPath(nint graphics, nint pen, nint path);

    [LibraryImport("gdiplus")]
    public static partial int GdipFillRectangle(nint graphics, nint brush, float x, float y, float width, float height);

    [LibraryImport("gdiplus")]
    public static partial int GdipDrawRectangle(nint graphics, nint pen, float x, float y, float width, float height);

    [LibraryImport("gdiplus")]
    public static partial int GdipDrawImageRect(nint graphics, nint image, float x, float y, float width, float height);

    [LibraryImport("gdiplus")]
    public static partial int GdipDrawImageI(nint graphics, nint image, int x, int y);

    [LibraryImport("gdiplus")]
    public static partial int GdipNewPrivateFontCollection(out nint collection);

    [LibraryImport("gdiplus")]
    public static partial int GdipPrivateAddMemoryFont(nint collection, void* memory, int length);

    [LibraryImport("gdiplus", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GdipCreateFontFamilyFromName(string name, nint collection, out nint family);

    [LibraryImport("gdiplus")]
    public static partial int GdipGetGenericFontFamilySansSerif(out nint family);

    [LibraryImport("gdiplus")]
    public static partial int GdipCreateFont(nint family, float size, int style, int unit, out nint font);

    [LibraryImport("gdiplus")]
    public static partial int GdipDeleteFont(nint font);

    [LibraryImport("gdiplus")]
    public static partial int GdipCreateStringFormat(int attributes, ushort language, out nint format);

    [LibraryImport("gdiplus")]
    public static partial int GdipDeleteStringFormat(nint format);

    [LibraryImport("gdiplus")]
    public static partial int GdipSetStringFormatFlags(nint format, int flags);

    [LibraryImport("gdiplus")]
    public static partial int GdipSetStringFormatTrimming(nint format, int trimming);

    [LibraryImport("gdiplus", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GdipDrawString(nint graphics, string text, int length, nint font, RectF* layout, nint format, nint brush);

    [LibraryImport("gdiplus", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GdipSaveImageToFile(nint image, string path, Guid* encoder, nint parameters);
}

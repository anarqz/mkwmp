// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using System.Runtime.InteropServices;
using Mkwmp.Core;

namespace Mkwmp.App.Input;

/// <summary>
/// One reading of the SDL controller, in XInput's terms so <see cref="GamepadInput"/> treats both
/// alike: <see cref="Buttons"/> is an XINPUT_GAMEPAD mask by position (south = A), sticks with Y up positive.
/// </summary>
internal sealed record SdlReading(ushort Buttons, short LX, short LY, short RX, short RY, PadIdentity Identity, string Name);

/// <summary>
/// Controllers XInput does not see (a Switch Pro Controller, Joy-Con, DualSense... over USB or Bluetooth), read
/// the way the game reads them: through mkw-recomp's own <c>SDL3.dll</c>. The launcher loads a copy of it (in
/// <c>cache/sdl3/</c>), never the game folder's file, so a runtime update can still replace it.
/// <para>
/// SDL runs on its own thread (opening a Switch controller is a handshake that can take a second), and only while
/// <see cref="Wanted"/>: the launcher's window is in front, the launcher itself or its overlay over a running game.
/// With the overlay up the game is suspended behind it and reads an idle pad (the mod's filter), so only one of the
/// two SDLs acts on the controller's input. Out of front, SDL is shut down and the game's SDL has the controller alone.
/// SDL's XInput, RawInput and Windows.Gaming.Input drivers are off: XInput pads stay <see cref="GamepadInput"/>'s.
/// </para>
/// </summary>
internal sealed class SdlPads : IDisposable
{
    private const uint InitGamepad = 0x00002000; // SDL_INIT_GAMEPAD (implies SDL_INIT_JOYSTICK)

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _stop;
    private volatile bool _wanted;
    private volatile string? _runtime;
    private SdlReading? _reading;

    public SdlPads()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SDL pads" };
        _thread.Start();
    }

    /// <summary>The newest reading of the open controller; null without one (or while SDL is off).</summary>
    public SdlReading? Reading => Volatile.Read(ref _reading);

    /// <summary>The newest reading's buttons (Guide, the Home button, included), 0 without a controller: the overlay's Guide watch.</summary>
    public static ushort CurrentButtons => (ushort)Volatile.Read(ref s_buttons);

    private static int s_buttons;

    /// <summary>SDL may run: the launcher's window (or its overlay) is in front.</summary>
    public bool Wanted
    {
        get => _wanted;
        set
        {
            _wanted = value;
            _wake.Set();
        }
    }

    /// <summary>The folder with mkw.exe and its SDL3.dll (null: none installed, SDL stays off).</summary>
    public string? RuntimeFolder
    {
        get => _runtime;
        set
        {
            _runtime = value;
            _wake.Set();
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }

    private void Run()
    {
        Sdl? sdl = null;
        string? loadedFrom = null;
        var failed = false;
        var on = false;
        var pad = IntPtr.Zero;
        var nextScan = DateTime.MinValue;
        try
        {
            while (!_stop)
            {
                var runtime = _runtime;
                if (!_wanted || runtime is null)
                {
                    if (on)
                    {
                        Close(sdl!, ref pad);
                        sdl!.QuitSubSystem(InitGamepad);
                        on = false;
                        App.Log("input: SDL controllers off (the game has the controller, or the window is not in front)");
                    }
                    _wake.Wait(TimeSpan.FromSeconds(1));
                    _wake.Reset();
                    continue;
                }
                // one SDL per run (a library cannot be unloaded safely): a later runtime folder's is used from the next start
                if (sdl is null)
                {
                    if (failed && string.Equals(loadedFrom, runtime, StringComparison.OrdinalIgnoreCase))
                    {
                        _wake.Wait(TimeSpan.FromSeconds(1));
                        _wake.Reset();
                        continue;
                    }
                    loadedFrom = runtime;
                    sdl = Sdl.Load(runtime);
                    failed = sdl is null;
                    if (sdl is null)
                    {
                        continue;
                    }
                }
                if (!on)
                {
                    sdl.SetHint("SDL_JOYSTICK_XINPUT", "0");
                    sdl.SetHint("SDL_JOYSTICK_RAWINPUT", "0");
                    sdl.SetHint("SDL_JOYSTICK_WGI", "0");
                    sdl.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
                    if (!sdl.Init(InitGamepad))
                    {
                        App.Log($"input: SDL_Init(gamepad) failed: {sdl.Error()}; controllers through XInput only");
                        sdl = null; // not retried this run
                        failed = true;
                        continue;
                    }
                    // read by polling: no event queue to fill
                    sdl.SetJoystickEventsEnabled(false);
                    sdl.SetGamepadEventsEnabled(false);
                    on = true;
                    nextScan = DateTime.MinValue;
                    App.Log("input: SDL controllers on");
                }
                sdl.UpdateGamepads();
                if (pad != IntPtr.Zero && !sdl.GamepadConnected(pad))
                {
                    App.Log("input: SDL controller gone");
                    Close(sdl, ref pad);
                }
                if (pad == IntPtr.Zero && DateTime.UtcNow >= nextScan)
                {
                    nextScan = DateTime.UtcNow.AddSeconds(1);
                    pad = OpenFirst(sdl);
                }
                Publish(pad == IntPtr.Zero ? null : Read(sdl, pad));
                Sleep();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            App.Log($"input: SDL controllers stopped: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            Publish(null);
            if (on && sdl is not null)
            {
                Close(sdl, ref pad);
                sdl.QuitSubSystem(InitGamepad);
            }
        }

        void Sleep()
        {
            _wake.Wait(TimeSpan.FromMilliseconds(1000.0 / 60.0));
            _wake.Reset();
        }
    }

    private void Publish(SdlReading? reading)
    {
        Volatile.Write(ref _reading, reading);
        Volatile.Write(ref s_buttons, reading?.Buttons ?? 0);
    }

    private void Close(Sdl sdl, ref IntPtr pad)
    {
        Publish(null);
        if (pad != IntPtr.Zero)
        {
            sdl.CloseGamepad(pad);
            pad = IntPtr.Zero;
        }
    }

    private static IntPtr OpenFirst(Sdl sdl)
    {
        foreach (var id in sdl.GetGamepads())
        {
            var pad = sdl.OpenGamepad(id);
            if (pad == IntPtr.Zero)
            {
                continue;
            }
            var r = Read(sdl, pad);
            App.Log($"input: SDL controller \"{r.Name}\" {r.Identity}");
            return pad;
        }
        return IntPtr.Zero;
    }

    // SDL_GamepadButton: positional (SOUTH is where an Xbox pad has A), as XINPUT_GAMEPAD's bits are
    private static readonly (int Button, ushort Mask)[] Buttons =
    [
        (0, 0x1000), (1, 0x2000), (2, 0x4000), (3, 0x8000), // south, east, west, north
        (4, 0x0020), (5, 0x0400), (6, 0x0010), (7, 0x0040), (8, 0x0080), // back, guide (Home), start, left stick, right stick
        (9, 0x0100), (10, 0x0200), // left and right shoulder
        (11, 0x0001), (12, 0x0002), (13, 0x0004), (14, 0x0008), // D-pad up, down, left, right
    ];

    private static SdlReading Read(Sdl sdl, IntPtr pad)
    {
        ushort mask = 0;
        foreach (var (button, bit) in Buttons)
        {
            if (sdl.GetGamepadButton(pad, button))
            {
                mask |= bit;
            }
        }
        // SDL's Y axes are down positive, XInput's up positive
        static short Up(short y) => (short)Math.Clamp(-(int)y, short.MinValue, short.MaxValue);
        return new SdlReading(mask,
            sdl.GetGamepadAxis(pad, 0), Up(sdl.GetGamepadAxis(pad, 1)),
            sdl.GetGamepadAxis(pad, 2), Up(sdl.GetGamepadAxis(pad, 3)),
            new PadIdentity(sdl.GetGamepadVendor(pad), sdl.GetGamepadProduct(pad)),
            sdl.GetGamepadName(pad));
    }

    /// <summary>The SDL3 calls used, from a private copy of the runtime's SDL3.dll.</summary>
    private sealed class Sdl
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool SetHintFn([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool InitFn(uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FlagsFn(uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void VoidFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EnableFn([MarshalAs(UnmanagedType.I1)] bool enabled);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ListFn(out int count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr OpenFn(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PtrFn(IntPtr p);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool PtrBoolFn(IntPtr p);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool ButtonFn(IntPtr p, int button);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate short AxisFn(IntPtr p, int axis);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ushort IdFn(IntPtr p);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr NameFn(IntPtr p);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ErrorFn();

        private SetHintFn _setHint = null!;
        private InitFn _init = null!;
        private FlagsFn _quit = null!;
        private EnableFn _joystickEvents = null!;
        private EnableFn _gamepadEvents = null!;
        private VoidFn _update = null!;
        private ListFn _list = null!;
        private PtrFn _free = null!;
        private OpenFn _open = null!;
        private PtrFn _close = null!;
        private PtrBoolFn _connected = null!;
        private ButtonFn _button = null!;
        private AxisFn _axis = null!;
        private IdFn _vendor = null!;
        private IdFn _product = null!;
        private NameFn _name = null!;
        private ErrorFn _error = null!;

        /// <summary>The runtime's SDL3.dll, copied first (null, logged, when there is none or it cannot be loaded).</summary>
        public static Sdl? Load(string runtime)
        {
            var source = Path.Combine(runtime, "SDL3.dll");
            try
            {
                var info = new FileInfo(source);
                if (!info.Exists)
                {
                    App.Log($"input: no SDL3.dll in {runtime}: controllers through XInput only");
                    return null;
                }
                // one copy per file (size and time), so a newer runtime gets its own beside the one in use
                var folder = Path.Combine(LauncherOptions.LauncherFolder, Mkwmp.Core.Launcher.CacheFolderName, "sdl3", $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}");
                var copy = Path.Combine(folder, "SDL3.dll");
                if (!File.Exists(copy))
                {
                    Directory.CreateDirectory(folder);
                    var part = copy + ".part";
                    File.Copy(source, part, overwrite: true);
                    File.Move(part, copy, overwrite: true);
                }
                var lib = NativeLibrary.Load(copy);
                var sdl = new Sdl();
                T F<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
                sdl._setHint = F<SetHintFn>("SDL_SetHint");
                sdl._init = F<InitFn>("SDL_InitSubSystem");
                sdl._quit = F<FlagsFn>("SDL_QuitSubSystem");
                sdl._joystickEvents = F<EnableFn>("SDL_SetJoystickEventsEnabled");
                sdl._gamepadEvents = F<EnableFn>("SDL_SetGamepadEventsEnabled");
                sdl._update = F<VoidFn>("SDL_UpdateGamepads");
                sdl._list = F<ListFn>("SDL_GetGamepads");
                sdl._free = F<PtrFn>("SDL_free");
                sdl._open = F<OpenFn>("SDL_OpenGamepad");
                sdl._close = F<PtrFn>("SDL_CloseGamepad");
                sdl._connected = F<PtrBoolFn>("SDL_GamepadConnected");
                sdl._button = F<ButtonFn>("SDL_GetGamepadButton");
                sdl._axis = F<AxisFn>("SDL_GetGamepadAxis");
                sdl._vendor = F<IdFn>("SDL_GetGamepadVendor");
                sdl._product = F<IdFn>("SDL_GetGamepadProduct");
                sdl._name = F<NameFn>("SDL_GetGamepadName");
                sdl._error = F<ErrorFn>("SDL_GetError");
                App.Log($"input: SDL3 from {source} (copy in {folder})");
                return sdl;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                App.Log($"input: SDL3 not usable ({e.GetType().Name}: {e.Message}): controllers through XInput only");
                return null;
            }
        }

        public void SetHint(string name, string value) => _setHint(name, value);
        public bool Init(uint flags) => _init(flags);
        public void QuitSubSystem(uint flags) => _quit(flags);
        public void SetJoystickEventsEnabled(bool on) => _joystickEvents(on);
        public void SetGamepadEventsEnabled(bool on) => _gamepadEvents(on);
        public void UpdateGamepads() => _update();
        public IntPtr OpenGamepad(uint id) => _open(id);
        public void CloseGamepad(IntPtr pad) => _close(pad);
        public bool GamepadConnected(IntPtr pad) => _connected(pad);
        public bool GetGamepadButton(IntPtr pad, int button) => _button(pad, button);
        public short GetGamepadAxis(IntPtr pad, int axis) => _axis(pad, axis);
        public ushort GetGamepadVendor(IntPtr pad) => _vendor(pad);
        public ushort GetGamepadProduct(IntPtr pad) => _product(pad);
        public string GetGamepadName(IntPtr pad) => Marshal.PtrToStringUTF8(_name(pad)) ?? "";
        public string Error() => Marshal.PtrToStringUTF8(_error()) ?? "";

        public uint[] GetGamepads()
        {
            var list = _list(out var count);
            if (list == IntPtr.Zero)
            {
                return [];
            }
            try
            {
                var ids = new uint[Math.Max(count, 0)];
                for (var i = 0; i < ids.Length; i++)
                {
                    ids[i] = (uint)Marshal.ReadInt32(list, i * 4);
                }
                return ids;
            }
            finally
            {
                _free(list);
            }
        }
    }
}

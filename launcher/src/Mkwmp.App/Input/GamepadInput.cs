// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Mkwmp.App.Input;

/// <summary>What the player touched last (Mouse is also touch): decides the glyphs of the hint bar.</summary>
public enum InputKind { Gamepad, Keyboard, Mouse }

public enum NavDirection { Up, Down, Left, Right }

/// <summary>How far the right stick scrolls in one poll, in units (positive: right, down).</summary>
public readonly record struct ScrollStep(double X, double Y);

/// <summary>
/// XInput, polled 60 times a second on the UI thread (xinput1_4.dll, else xinput9_1_0.dll; Wine
/// has both). Raises <see cref="Pressed"/> once per button press; the D-pad and the left stick
/// repeat while held (after a delay). Silent while <see cref="Enabled"/> is false (the window is
/// not the active one: the game has the controller then).
/// </summary>
public sealed class GamepadInput : IDisposable
{
    private const double StickThreshold = 0.55;
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherTimer _timer;
    private readonly DateTime[] _nextProbe = new DateTime[4];
    private int _api; // 0: xinput1_4, 1: xinput9_1_0, 2: none
    private int _slot = -1;
    private ushort _held;
    private bool _swallow = true;
    private PadButton? _repeating;
    private DateTime _repeatAt;
    private DateTime _repeatSince;
    private DateTime _lastPoll;
    private DateTime _stickSince;
    private bool _enabled = true;
    private string? _setting;
    private bool _noCapabilitiesEx;
    private PadIdentity? _pretend;

    public GamepadInput()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>A role (<see cref="PadLayout"/>): with the Switch layout the east button is A.</summary>
    public event EventHandler<PadButton>? Pressed;

    /// <summary>The right stick, every poll while it is tilted: how fast to scroll (units a second, positive down / right); zeros once when it is let go.</summary>
    public event EventHandler<ScrollStep>? Scrolled;

    /// <summary>The game's <c>pad_layout</c> ("auto", "nintendo", "xbox"): with <see cref="Identity"/>, which physical button gives which role.</summary>
    public string? Setting
    {
        get => _setting;
        set
        {
            _setting = value;
            Remap();
        }
    }

    /// <summary>The controller in use, as the game's SDL identifies it (<see cref="PadIdentity.Unknown"/> without one).</summary>
    public PadIdentity Identity { get; private set; }

    /// <summary>What the game does with this controller and <see cref="Setting"/> (<see cref="PadLayout.Resolve"/>).</summary>
    public PadMapping Mapping { get; private set; } = PadLayout.Resolve(null, PadIdentity.Unknown);

    /// <summary>The mapping changed: a new setting or another controller.</summary>
    public event EventHandler? MappingChanged;

    /// <summary>A controller came or went.</summary>
    public event EventHandler? ConnectedChanged;

    public bool Connected => _slot >= 0;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                _swallow = true; // what is held when the window comes back is not a press
                _repeating = null;
            }
        }
    }

    public void Start() => _timer.Start();

    public void Dispose() => _timer.Stop();

    private void Poll()
    {
        if (_api == 2)
        {
            _timer.Stop();
            return;
        }
        var now = DateTime.UtcNow;
        var state = default(XInputState);
        var found = -1;
        if (_slot >= 0 && Read(_slot, out state))
        {
            found = _slot;
        }
        else
        {
            for (var i = 0; i < 4 && found < 0; i++)
            {
                if (now < _nextProbe[i])
                {
                    continue; // asking an empty slot is slow: once in a while is enough
                }
                if (Read(i, out state))
                {
                    found = i;
                }
                else
                {
                    _nextProbe[i] = now + ProbeEvery;
                }
            }
        }
        if (found != _slot)
        {
            _slot = found;
            _swallow = true;
            Identity = _pretend ?? (found >= 0 ? Identify(found) : PadIdentity.Unknown);
            Remap();
            ConnectedChanged?.Invoke(this, EventArgs.Empty);
        }
        if (found < 0)
        {
            return;
        }

        var buttons = state.Buttons;
        var x = state.ThumbLX / 32767.0;
        var y = state.ThumbLY / 32767.0;
        if (Math.Abs(x) > Math.Abs(y))
        {
            if (x > StickThreshold) buttons |= 0x0008;
            else if (x < -StickThreshold) buttons |= 0x0004;
        }
        else
        {
            if (y > StickThreshold) buttons |= 0x0001;
            else if (y < -StickThreshold) buttons |= 0x0002;
        }

        var dt = Math.Clamp((now - _lastPoll).TotalSeconds, 0, 0.1);
        _lastPoll = now;
        if (!_enabled || _swallow)
        {
            _held = buttons;
            _swallow = !_enabled;
            StickReleased();
            return;
        }
        var pressed = (ushort)(buttons & ~_held);
        _held = buttons;
        foreach (var (mask, button) in Map)
        {
            if ((pressed & mask) == 0)
            {
                continue;
            }
            if (button is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right)
            {
                _repeating = button;
                _repeatAt = now + ScrollMotion.FirstRepeat;
                _repeatSince = _repeatAt;
            }
            Pressed?.Invoke(this, PadLayout.ToRole(button, Mapping));
        }
        if (_repeating is { } held)
        {
            if ((buttons & MaskOf(held)) == 0)
            {
                _repeating = null;
            }
            else if (now >= _repeatAt)
            {
                _repeatAt = now + ScrollMotion.RepeatInterval(now - _repeatSince); // held longer: faster
                Pressed?.Invoke(this, held);
            }
        }
        Scroll(state, now, dt);
    }

    /// <summary>The right stick scrolls; faster the longer it is held (<see cref="ScrollMotion.StickVelocity"/>).</summary>
    private void Scroll(XInputState state, DateTime now, double dt)
    {
        var x = state.ThumbRX / 32767.0;
        var y = state.ThumbRY / 32767.0;
        if (Math.Max(Math.Abs(x), Math.Abs(y)) <= ScrollMotion.StickDeadZone)
        {
            StickReleased();
            return;
        }
        if (_stickSince == default)
        {
            _stickSince = now;
        }
        var held = (now - _stickSince).TotalSeconds;
        // up on the stick scrolls up (back); right scrolls right (forward): XInput's Y is up positive
        // a speed, not a distance: the list moves on the render clock, however unevenly this timer ticks
        var step = new ScrollStep(-ScrollMotion.StickVelocity(x, held), ScrollMotion.StickVelocity(y, held));
        if (step.X != 0 || step.Y != 0)
        {
            Scrolled?.Invoke(this, step);
        }
    }

    private void StickReleased()
    {
        if (_stickSince != default)
        {
            _stickSince = default;
            Scrolled?.Invoke(this, default); // let go: the list settles
        }
    }

    private static readonly (ushort Mask, PadButton Button)[] Map =
    [
        (0x0001, PadButton.Up), (0x0002, PadButton.Down), (0x0004, PadButton.Left), (0x0008, PadButton.Right),
        (0x0010, PadButton.Start), (0x0020, PadButton.Back), (0x0100, PadButton.LB), (0x0200, PadButton.RB),
        (0x1000, PadButton.A), (0x2000, PadButton.B), (0x4000, PadButton.X), (0x8000, PadButton.Y),
    ];

    private static ushort MaskOf(PadButton button)
    {
        foreach (var (mask, b) in Map)
        {
            if (b == button) return mask;
        }
        return 0;
    }

    /// <summary>The harness' <c>--pad-device</c>: take the controller for <paramref name="pad"/>, whatever XInput says.</summary>
    public void Pretend(PadIdentity pad)
    {
        _pretend = pad;
        Identity = pad;
        Remap();
    }

    private void Remap()
    {
        var mapping = PadLayout.Resolve(_setting, Identity);
        if (mapping != Mapping)
        {
            Mapping = mapping;
            MappingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The controller in <paramref name="slot"/> as SDL identifies it (SDL_xinputjoystick.c: XInputGetCapabilitiesEx,
    /// xinput1_4's ordinal 108, for the ids; its last field is Steam's virtual pad slot), with Steam's type for its
    /// virtual pad from the file <c>SteamVirtualGamepadInfo</c> names (SDL_steam_virtual_gamepad.c).
    /// </summary>
    private PadIdentity Identify(int slot)
    {
        if (_api != 0 || _noCapabilitiesEx)
        {
            return PadIdentity.Unknown;
        }
        XInputCapabilitiesEx caps;
        try
        {
            if (XInputGetCapabilitiesEx(1, (uint)slot, 0, out caps) != 0)
            {
                return PadIdentity.Unknown;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _noCapabilitiesEx = true;
            return PadIdentity.Unknown;
        }
        var pad = new PadIdentity(caps.VendorId, caps.ProductId);
        if (!pad.IsSteamVirtual)
        {
            return pad;
        }
        string? info = null;
        try
        {
            if (Environment.GetEnvironmentVariable(PadLayout.SteamVirtualGamepadInfoVariable) is { Length: > 0 } file)
            {
                info = File.ReadAllText(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // as SDL: without the file, Steam's virtual pad is an Xbox controller
        }
        return pad with { SteamType = PadLayout.SteamTypeOf(info, (int)caps.SteamSlot) };
    }

    private bool Read(int slot, out XInputState state)
    {
        state = default;
        while (_api < 2)
        {
            try
            {
                return (_api == 0 ? XInputGetState14((uint)slot, out state) : XInputGetState910((uint)slot, out state)) == 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _api++;
            }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    /// <summary>XINPUT_CAPABILITIES and what XInputGetCapabilitiesEx adds (SDL_xinput.h SDL_XINPUT_CAPABILITIES_EX).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilitiesEx
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
        public ushort LeftMotor;
        public ushort RightMotor;
        public ushort VendorId;
        public ushort ProductId;
        public ushort ProductVersion;
        public ushort Unknown;
        public uint SteamSlot;
    }

#pragma warning disable SYSLIB1054 // plain DllImport: blittable, and no unsafe code in this project
    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetCapabilitiesEx(uint one, uint index, uint flags, out XInputCapabilitiesEx capabilities);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState14(uint index, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState910(uint index, out XInputState state);
#pragma warning restore SYSLIB1054
}

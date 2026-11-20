// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Mkwmp.App.Input;

/// <summary>What a controller (or its keyboard equivalent) can ask the shell for.</summary>
public enum PadButton { A, B, X, Y, LB, RB, Start, Back, Up, Down, Left, Right }

/// <summary>What the player touched last (Mouse is also touch): decides the glyphs of the hint bar.</summary>
public enum InputKind { Gamepad, Keyboard, Mouse }

public enum NavDirection { Up, Down, Left, Right }

/// <summary>
/// XInput, polled 60 times a second on the UI thread (xinput1_4.dll, else xinput9_1_0.dll; Wine
/// has both). Raises <see cref="Pressed"/> once per button press; the D-pad and the left stick
/// repeat while held (after a delay). Silent while <see cref="Enabled"/> is false (the window is
/// not the active one: the game has the controller then).
/// </summary>
public sealed class GamepadInput : IDisposable
{
    private const double StickThreshold = 0.55;
    private static readonly TimeSpan FirstRepeat = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan NextRepeat = TimeSpan.FromMilliseconds(105);
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherTimer _timer;
    private readonly DateTime[] _nextProbe = new DateTime[4];
    private int _api; // 0: xinput1_4, 1: xinput9_1_0, 2: none
    private int _slot = -1;
    private ushort _held;
    private bool _swallow = true;
    private PadButton? _repeating;
    private DateTime _repeatAt;
    private bool _enabled = true;

    public GamepadInput()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0) };
        _timer.Tick += (_, _) => Poll();
    }

    public event EventHandler<PadButton>? Pressed;

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

        if (!_enabled || _swallow)
        {
            _held = buttons;
            _swallow = !_enabled;
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
                _repeatAt = now + FirstRepeat;
            }
            Pressed?.Invoke(this, button);
        }
        if (_repeating is { } held)
        {
            if ((buttons & MaskOf(held)) == 0)
            {
                _repeating = null;
            }
            else if (now >= _repeatAt)
            {
                _repeatAt = now + NextRepeat;
                Pressed?.Invoke(this, held);
            }
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

#pragma warning disable SYSLIB1054 // plain DllImport: blittable, and no unsafe code in this project
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState14(uint index, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState910(uint index, out XInputState state);
#pragma warning restore SYSLIB1054
}

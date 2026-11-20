// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Mkwmp.App.Overlay;

/// <summary>
/// The Guide (Xbox) button, which XInputGetState never reports: XInputGetStateEx (xinput1_4
/// ordinal 100) does, focused or not. Two uses (launcher/docs/OVERLAY.md §2):
/// <list type="bullet">
/// <item>with the overlay up, Guide goes back to the game as it opened it (GamepadInput has no Guide);</item>
/// <item>the fallback trigger for a game without the mod's overlay (the winmm probe, an older mod):
/// Guide while the game's window is the foreground one.</item>
/// </list>
/// Steam and the Xbox Game Bar take the button first where they are set to, and Wine's xinput1_4
/// has no ordinal 100 in every version: <see cref="Available"/> says whether it can be read at all.
/// Polled 30 times a second on the UI thread; <see cref="Pressed"/> once per press.
/// </summary>
public sealed class GuideWatcher : IDisposable
{
    private const ushort Guide = 0x0400;
    private readonly DispatcherTimer _timer;
    private bool _held = true; // what is held at the start is not a press
    private bool _missing;

    public GuideWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>The Guide button went down (on any of the four pads).</summary>
    public event EventHandler? Pressed;

    /// <summary>False once the system turned out to have no XInputGetStateEx.</summary>
    public bool Available => !_missing;

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        _held = true;
    }

    public void Dispose() => _timer.Stop();

    private void Poll()
    {
        var down = false;
        try
        {
            for (uint pad = 0; pad < 4 && !down; pad++)
            {
                down = XInputGetStateEx(pad, out var state) == 0 && (state.Buttons & Guide) != 0;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _missing = true;
            _timer.Stop();
            return;
        }
        var pressed = down && !_held;
        _held = down;
        if (pressed)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>XINPUT_STATE plus the DWORD XInputGetStateEx also writes: 20 bytes, not 16.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputStateEx
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
        public uint Reserved;
    }

#pragma warning disable SYSLIB1054 // plain DllImport, as GamepadInput
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetStateEx(uint index, out XInputStateEx state);
#pragma warning restore SYSLIB1054
}

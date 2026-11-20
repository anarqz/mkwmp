// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;

namespace Mkwmp.Core.Devices;

/// <summary>
/// winmm's waveform API: the oldest audio API of Windows, and for that reason the one that
/// behaves the same on Windows and under Wine/Proton (where the launcher runs on the Steam Deck).
/// Devices are polled (WHDR_DONE), not called back: no native-to-managed callback to keep alive.
/// </summary>
internal static unsafe class WinMm
{
    public const uint WaveMapper = 0xFFFF_FFFF;
    public const uint WaveMapped = 0x0004;
    public const uint HeaderDone = 0x0000_0001;
    private const uint QueryInstanceId = 0x0811; // DRV_QUERYFUNCTIONINSTANCEID
    private const uint QueryInstanceIdSize = 0x0812; // DRV_QUERYFUNCTIONINSTANCEIDSIZE
    private const int CapsSize = 96; // enough for WAVEINCAPSW (80) and WAVEOUTCAPSW (84)
    private const int NameOffset = 8; // after wMid, wPid, vDriverVersion
    private const int NameChars = 32;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;

        /// <summary>16-bit mono PCM.</summary>
        public static WaveFormat Pcm(uint rate) => new()
        {
            FormatTag = 1,
            Channels = 1,
            SamplesPerSec = rate,
            AvgBytesPerSec = rate * 2,
            BlockAlign = 2,
            BitsPerSample = 16,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nuint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nuint Reserved;
    }

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint waveInGetDevCapsW(nuint id, byte* caps, uint size);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint waveInMessage(nint device, uint message, nint p1, nint p2);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveInOpen(nint* handle, uint id, WaveFormat* format, nint callback, nint instance, uint flags);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveInPrepareHeader(nint handle, WaveHeader* header, uint size);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveInUnprepareHeader(nint handle, WaveHeader* header, uint size);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveInAddBuffer(nint handle, WaveHeader* header, uint size);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveInStart(nint handle);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveInReset(nint handle);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveInClose(nint handle);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint waveOutGetDevCapsW(nuint id, byte* caps, uint size);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint waveOutMessage(nint device, uint message, nint p1, nint p2);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveOutOpen(nint* handle, uint id, WaveFormat* format, nint callback, nint instance, uint flags);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveOutPrepareHeader(nint handle, WaveHeader* header, uint size);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveOutUnprepareHeader(nint handle, WaveHeader* header, uint size);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveOutWrite(nint handle, WaveHeader* header, uint size);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveOutReset(nint handle);

    [DllImport("winmm.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint waveOutClose(nint handle);

    /// <summary>
    /// The devices in winmm's order (the index is what waveInOpen takes). The id is the audio
    /// endpoint's id (<c>{0.0.1.00000000}.{guid}</c>), the same string the game's voice engine
    /// (WASAPI) reports and client.toml stores; the name is winmm's, cut at 31 characters.
    /// </summary>
    public static List<AudioDevice> Devices(bool input)
    {
        var list = new List<AudioDevice>();
        uint count;
        try
        {
            count = input ? waveInGetNumDevs() : waveOutGetNumDevs();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return list;
        }
        var caps = stackalloc byte[CapsSize];
        for (uint i = 0; i < count && i < ClientConfig.MaxDevices; i++)
        {
            new Span<byte>(caps, CapsSize).Clear();
            if ((input ? waveInGetDevCapsW(i, caps, 80) : waveOutGetDevCapsW(i, caps, 84)) != 0)
            {
                continue;
            }
            var name = new string((char*)(caps + NameOffset), 0, NameChars);
            var end = name.IndexOf('\0', StringComparison.Ordinal);
            name = end < 0 ? name : name[..end];
            // An index would change when a device is plugged in; without an endpoint id the name is the best we have.
            var id = EndpointId(input, i) ?? name;
            if (id.Length > 0 && list.All(d => d.Id != id))
            {
                list.Add(new AudioDevice(id, name.Length > 0 ? name : id));
            }
        }
        return list;
    }

    private static string? EndpointId(bool input, uint index)
    {
        nint size = 0;
        var device = (nint)index;
        var result = input ? waveInMessage(device, QueryInstanceIdSize, (nint)(&size), 0) : waveOutMessage(device, QueryInstanceIdSize, (nint)(&size), 0);
        if (result != 0 || size <= 0 || size > 4096)
        {
            return null;
        }
        var buffer = stackalloc byte[(int)size];
        result = input ? waveInMessage(device, QueryInstanceId, (nint)buffer, size) : waveOutMessage(device, QueryInstanceId, (nint)buffer, size);
        if (result != 0)
        {
            return null;
        }
        var text = new string((char*)buffer, 0, (int)size / 2);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    /// <summary>The winmm index of the device with that id; the wave mapper (the system default) for "" or a device that is gone.</summary>
    public static uint IndexOf(bool input, string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return WaveMapper;
        }
        var devices = Devices(input);
        var index = devices.FindIndex(d => d.Id == id);
        if (index < 0)
        {
            return WaveMapper;
        }
        // Devices() skips nothing in practice, but look the index up again by id to be exact.
        uint count = input ? waveInGetNumDevs() : waveOutGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            if (EndpointId(input, i) == id)
            {
                return i;
            }
        }
        return (uint)index;
    }
}

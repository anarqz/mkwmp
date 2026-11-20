// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Devices;

/// <summary>
/// The real <see cref="IDeviceLab"/>: the webcam through mkwmp-camera.dll (found next to the
/// launcher, else in the game's MKWMP mod), microphones and outputs through winmm. The camera
/// library is loaded on first use and looked for again while it is missing: the player may pick
/// the game folder, or install the mod, with the settings screen open.
/// </summary>
public sealed unsafe class DeviceLab : IDeviceLab
{
    private const int CameraAbi = 1;
    private const string CameraDll = "mkwmp-camera.dll";
    private const uint SampleRate = 48_000;
    private const int FrameSamples = 1600; // 33 ms: one level about 30 times a second
    private const int CaptureBuffers = 4;

    private static Action<string>? s_cameraLog;

    private readonly Func<IEnumerable<string>> _folders;
    private readonly Action<string> _note;
    private readonly Lock _gate = new();
    private Camera? _camera;
    private string? _cameraProblem;
    private (int Width, int Height) _frame;
    private Thread? _mic;
    private CancellationTokenSource? _micStop;
    private bool _disposed;

    /// <param name="libraryFolders">Where mkwmp-camera.dll may be, in order (the launcher folder first).</param>
    public DeviceLab(Func<IEnumerable<string>> libraryFolders, Action<string>? note = null)
    {
        ArgumentNullException.ThrowIfNull(libraryFolders);
        _folders = libraryFolders;
        _note = note ?? (_ => { });
    }

    public event EventHandler<float>? MicLevel;

    // ---- camera ---------------------------------------------------------------------------

    private sealed class Camera(nint module)
    {
        public nint Module { get; } = module;
        public delegate* unmanaged[Cdecl]<byte*, int, int> DevicesJson;
        public delegate* unmanaged[Cdecl]<byte*, int, int, int, int, void> Start;
        public delegate* unmanaged[Cdecl]<int, void> SetMirror;
        public delegate* unmanaged[Cdecl]<void> Stop;
        public delegate* unmanaged[Cdecl]<byte*, int, uint*, int> Frame;
        public delegate* unmanaged[Cdecl]<int> Status;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCameraLog(byte* line)
    {
        try
        {
            if (line is not null && s_cameraLog is { } log)
            {
                log("camera: " + (Marshal.PtrToStringUTF8((nint)line) ?? ""));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or ArgumentException)
        {
            // never let a log line take the native caller down
        }
    }

    private Camera? EnsureCamera()
    {
        lock (_gate)
        {
            if (_camera is not null || _disposed)
            {
                return _camera;
            }
            _cameraProblem = Strings.CameraDllMissing;
            IEnumerable<string> folders;
            try
            {
                folders = [.. _folders()];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                folders = [];
            }
            foreach (var folder in folders)
            {
                var path = Path.Combine(folder, CameraDll);
                if (!File.Exists(path))
                {
                    continue;
                }
                if (!NativeLibrary.TryLoad(path, out var module))
                {
                    _cameraProblem = Strings.CameraDllBroken(path);
                    continue;
                }
                try
                {
                    var abi = ((delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(module, "mkwmp_camera_abi"))();
                    if (abi != CameraAbi)
                    {
                        _cameraProblem = Strings.CameraDllOtherAbi(abi);
                        NativeLibrary.Free(module);
                        continue;
                    }
                    var camera = new Camera(module)
                    {
                        DevicesJson = (delegate* unmanaged[Cdecl]<byte*, int, int>)NativeLibrary.GetExport(module, "mkwmp_camera_devices_json"),
                        Start = (delegate* unmanaged[Cdecl]<byte*, int, int, int, int, void>)NativeLibrary.GetExport(module, "mkwmp_camera_start"),
                        SetMirror = (delegate* unmanaged[Cdecl]<int, void>)NativeLibrary.GetExport(module, "mkwmp_camera_set_mirror"),
                        Stop = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(module, "mkwmp_camera_stop"),
                        Frame = (delegate* unmanaged[Cdecl]<byte*, int, uint*, int>)NativeLibrary.GetExport(module, "mkwmp_camera_frame"),
                        Status = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(module, "mkwmp_camera_status"),
                    };
                    s_cameraLog = _note;
                    ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<byte*, void>, void>)NativeLibrary.GetExport(module, "mkwmp_camera_init"))(&OnCameraLog);
                    _note($"camera: loaded {path}");
                    _camera = camera;
                    _cameraProblem = null;
                    return camera;
                }
                catch (EntryPointNotFoundException e)
                {
                    _cameraProblem = Strings.CameraDllBroken(e.Message);
                    NativeLibrary.Free(module);
                }
            }
            return null;
        }
    }

    public bool CameraAvailable => EnsureCamera() is not null;

    public string? CameraProblem
    {
        get
        {
            EnsureCamera();
            lock (_gate)
            {
                return _cameraProblem;
            }
        }
    }

    public Task<IReadOnlyList<CameraDevice>> CameraDevicesAsync() => Task.Run<IReadOnlyList<CameraDevice>>(() =>
    {
        if (EnsureCamera() is not { } camera)
        {
            return [];
        }
        for (var cap = 16 * 1024; cap <= 1024 * 1024; cap *= 4)
        {
            var buffer = new byte[cap];
            int length;
            fixed (byte* p = buffer)
            {
                length = camera.DevicesJson(p, cap);
            }
            if (length < 0)
            {
                continue; // too small: ask again with more room
            }
            var list = new List<CameraDevice>();
            if (Json.Parse(buffer.AsSpan(0, Math.Min(length, cap))).At("devices") is { ValueKind: JsonValueKind.Array } devices)
            {
                foreach (var d in devices.EnumerateArray())
                {
                    if (((JsonElement?)d).At("id").Str() is { Length: > 0 } id)
                    {
                        list.Add(new CameraDevice(id, ((JsonElement?)d).At("name").Str() is { Length: > 0 } name ? name : id));
                    }
                }
            }
            return list;
        }
        return [];
    });

    public void StartCamera(string deviceId, int width, int height, int fps, bool mirror)
    {
        if (EnsureCamera() is not { } camera || width <= 0 || height <= 0)
        {
            return;
        }
        var id = Encoding.UTF8.GetBytes((deviceId ?? "") + "\0");
        lock (_gate)
        {
            _frame = (width, height);
            fixed (byte* p = id)
            {
                camera.Start(p, width, height, Math.Clamp(fps, 1, 60), mirror ? 1 : 0);
            }
        }
    }

    public void SetCameraMirror(bool mirror)
    {
        if (_camera is { } camera)
        {
            camera.SetMirror(mirror ? 1 : 0);
        }
    }

    public void StopCamera()
    {
        if (_camera is { } camera)
        {
            lock (_gate)
            {
                _frame = default;
                camera.Stop();
            }
        }
    }

    public CameraStatus CameraStatus => _camera is { } camera && camera.Status() is var s and >= 0 and <= 3 ? (CameraStatus)s : CameraStatus.Idle;

    public bool TryCopyFrame(Span<byte> rgba, ref uint serial)
    {
        if (_camera is not { } camera)
        {
            return false;
        }
        var (width, height) = _frame;
        // mkwmp_camera.h: the buffer must hold at least width * height * 4 bytes; a smaller span is refused here.
        if (width <= 0 || rgba.Length < (long)width * height * 4)
        {
            return false;
        }
        fixed (byte* p = rgba)
        fixed (uint* s = &serial)
        {
            return camera.Frame(p, rgba.Length, s) == 1;
        }
    }

    // ---- audio ----------------------------------------------------------------------------

    public Task<IReadOnlyList<AudioDevice>> InputDevicesAsync() => Task.Run<IReadOnlyList<AudioDevice>>(() => WinMm.Devices(input: true));

    public Task<IReadOnlyList<AudioDevice>> OutputDevicesAsync() => Task.Run<IReadOnlyList<AudioDevice>>(() => WinMm.Devices(input: false));

    public void StartMicTest(string deviceId, int gainPercent, bool echoToOutput, string outputId)
    {
        StopMicTest();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            var stop = new CancellationTokenSource();
            var gain = Math.Clamp(gainPercent, 0, 100) / 100f;
            _micStop = stop;
            _mic = new Thread(() => MicLoop(deviceId ?? "", gain, echoToOutput, outputId ?? "", stop.Token)) { IsBackground = true, Name = "mkwmp mic test" };
            _mic.Start();
        }
    }

    public void StopMicTest()
    {
        Thread? thread;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            (thread, stop, _mic, _micStop) = (_mic, _micStop, null, null);
        }
        stop?.Cancel();
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        stop?.Dispose();
    }

    private static uint Open(bool input, string id, nint* handle)
    {
        var format = WinMm.WaveFormat.Pcm(SampleRate);
        var index = WinMm.IndexOf(input, id);
        // WAVE_MAPPED: this device, but let the mapper convert when it does not take 48 kHz mono itself.
        var flags = index == WinMm.WaveMapper ? 0 : WinMm.WaveMapped;
        var result = input ? WinMm.waveInOpen(handle, index, &format, 0, 0, flags) : WinMm.waveOutOpen(handle, index, &format, 0, 0, flags);
        if (result != 0 && flags != 0)
        {
            result = input ? WinMm.waveInOpen(handle, index, &format, 0, 0, 0) : WinMm.waveOutOpen(handle, index, &format, 0, 0, 0);
        }
        return result;
    }

    private static WinMm.WaveHeader* NewHeader(int bytes)
    {
        var header = (WinMm.WaveHeader*)NativeMemory.AllocZeroed((nuint)(sizeof(WinMm.WaveHeader) + bytes));
        header->Data = (nint)(header + 1);
        header->BufferLength = (uint)bytes;
        return header;
    }

    private void MicLoop(string deviceId, float gain, bool echo, string outputId, CancellationToken stop)
    {
        nint input = 0, output = 0;
        var size = (uint)sizeof(WinMm.WaveHeader);
        var capture = new WinMm.WaveHeader*[CaptureBuffers];
        var playing = new Queue<nint>();
        try
        {
            var result = Open(input: true, deviceId, &input);
            if (result != 0)
            {
                _note($"mic test: cannot open the microphone (winmm error {result})");
                MicLevel?.Invoke(this, 0f);
                return;
            }
            if (echo && Open(input: false, outputId, &output) != 0)
            {
                _note("mic test: cannot open the output; metering only");
                output = 0;
            }
            for (var i = 0; i < CaptureBuffers; i++)
            {
                capture[i] = NewHeader(FrameSamples * 2);
                _ = WinMm.waveInPrepareHeader(input, capture[i], size);
                _ = WinMm.waveInAddBuffer(input, capture[i], size);
            }
            _ = WinMm.waveInStart(input);
            while (!stop.IsCancellationRequested)
            {
                foreach (var header in capture)
                {
                    if ((header->Flags & WinMm.HeaderDone) == 0)
                    {
                        continue;
                    }
                    var samples = new Span<short>((void*)header->Data, (int)Math.Min(header->BytesRecorded, header->BufferLength) / 2);
                    var peak = 0;
                    for (var i = 0; i < samples.Length; i++)
                    {
                        var v = Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
                        samples[i] = (short)v;
                        peak = Math.Max(peak, Math.Abs(v));
                    }
                    MicLevel?.Invoke(this, Math.Min(1f, peak / 32767f));
                    // At most half a second waits to be played: a stalled output must not grow the queue.
                    if (output != 0 && samples.Length > 0 && playing.Count < 16)
                    {
                        var copy = NewHeader(samples.Length * 2);
                        samples.CopyTo(new Span<short>((void*)copy->Data, samples.Length));
                        _ = WinMm.waveOutPrepareHeader(output, copy, size);
                        if (WinMm.waveOutWrite(output, copy, size) == 0)
                        {
                            playing.Enqueue((nint)copy);
                        }
                        else
                        {
                            _ = WinMm.waveOutUnprepareHeader(output, copy, size);
                            NativeMemory.Free(copy);
                        }
                    }
                    header->BytesRecorded = 0;
                    _ = WinMm.waveInAddBuffer(input, header, size);
                }
                while (playing.Count > 0 && (((WinMm.WaveHeader*)playing.Peek())->Flags & WinMm.HeaderDone) != 0)
                {
                    var done = (WinMm.WaveHeader*)playing.Dequeue();
                    _ = WinMm.waveOutUnprepareHeader(output, done, size);
                    NativeMemory.Free(done);
                }
                Thread.Sleep(5);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _note($"mic test: {e.Message}");
        }
        finally
        {
            if (input != 0)
            {
                _ = WinMm.waveInReset(input);
                foreach (var header in capture)
                {
                    if (header is not null)
                    {
                        _ = WinMm.waveInUnprepareHeader(input, header, size);
                    }
                }
                _ = WinMm.waveInClose(input);
            }
            foreach (var header in capture)
            {
                NativeMemory.Free(header);
            }
            if (output != 0)
            {
                _ = WinMm.waveOutReset(output);
                while (playing.Count > 0)
                {
                    var left = (WinMm.WaveHeader*)playing.Dequeue();
                    _ = WinMm.waveOutUnprepareHeader(output, left, size);
                    NativeMemory.Free(left);
                }
                _ = WinMm.waveOutClose(output);
            }
            MicLevel?.Invoke(this, 0f);
        }
    }

    /// <summary>Two soft rising notes (A5, then E6), about 0.6 s: short, and unlike any game sound.</summary>
    internal static short[] Chime(float gain)
    {
        var samples = new short[(int)(SampleRate * 0.6)];
        var half = samples.Length / 2;
        for (var i = 0; i < samples.Length; i++)
        {
            var second = i >= half;
            var t = (second ? i - half : i) / (double)SampleRate;
            var frequency = second ? 1318.5 : 880.0;
            var envelope = Math.Min(1.0, t / 0.01) * Math.Exp(-t * 9.0);
            samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * t) * envelope * 0.5 * gain * short.MaxValue);
        }
        return samples;
    }

    public Task PlayTestSoundAsync(string outputId, int gainPercent) => Task.Run(() =>
    {
        nint output = 0;
        try
        {
            var result = Open(input: false, outputId ?? "", &output);
            if (result != 0)
            {
                _note($"test sound: cannot open the output (winmm error {result})");
                return;
            }
            var samples = Chime(Math.Clamp(gainPercent, 0, 100) / 100f);
            var size = (uint)sizeof(WinMm.WaveHeader);
            var header = NewHeader(samples.Length * 2);
            try
            {
                samples.CopyTo(new Span<short>((void*)header->Data, samples.Length));
                _ = WinMm.waveOutPrepareHeader(output, header, size);
                if (WinMm.waveOutWrite(output, header, size) == 0)
                {
                    for (var waited = 0; waited < 3000 && (header->Flags & WinMm.HeaderDone) == 0; waited += 10)
                    {
                        Thread.Sleep(10);
                    }
                }
                _ = WinMm.waveOutReset(output);
                _ = WinMm.waveOutUnprepareHeader(output, header, size);
            }
            finally
            {
                NativeMemory.Free(header);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _note($"test sound: {e.Message}");
        }
        finally
        {
            if (output != 0)
            {
                _ = WinMm.waveOutClose(output);
            }
        }
    });

    public void Dispose()
    {
        StopMicTest();
        lock (_gate)
        {
            _disposed = true;
        }
        // The library stays loaded: its capture thread may still be winding down.
        StopCamera();
    }
}

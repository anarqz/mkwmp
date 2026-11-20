// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Devices;

/// <summary>
/// The launcher's own device tests (Settings → Party Chat): list and preview the webcam through
/// mkwmp-camera.dll (the same DLL and device ids the mod uses, docs/contracts/VIDEO_API.md), and
/// list microphones/outputs, meter the mic and play a test sound. Nothing here sends anything to
/// the network; the in-game voice engine (mkwmp-voice.dll) stays the game's.
/// </summary>
public interface IDeviceLab : IDisposable
{
    /// <summary>False when mkwmp-camera.dll is not found or has another ABI (<see cref="CameraProblem"/> says why).</summary>
    bool CameraAvailable { get; }
    string? CameraProblem { get; }

    /// <summary>Enumerates through COM: call off the UI thread.</summary>
    Task<IReadOnlyList<CameraDevice>> CameraDevicesAsync();

    void StartCamera(string deviceId, int width, int height, int fps, bool mirror);
    void SetCameraMirror(bool mirror);
    void StopCamera();
    CameraStatus CameraStatus { get; }

    /// <summary>Copies the newest frame (width*height*4 RGBA) when newer than <paramref name="serial"/>.</summary>
    bool TryCopyFrame(Span<byte> rgba, ref uint serial);

    Task<IReadOnlyList<Mkwmp.Core.AudioDevice>> InputDevicesAsync();
    Task<IReadOnlyList<Mkwmp.Core.AudioDevice>> OutputDevicesAsync();

    /// <summary>Meters the microphone ("" = default); <see cref="MicLevel"/> fires about 30 times a second with 0..1 peak.</summary>
    void StartMicTest(string deviceId, int gainPercent, bool echoToOutput, string outputId);
    void StopMicTest();
    event EventHandler<float>? MicLevel;

    /// <summary>Plays a short chime on that output ("" = default) at the gain.</summary>
    Task PlayTestSoundAsync(string outputId, int gainPercent);
}

public sealed record CameraDevice(string Id, string Name);

public enum CameraStatus { Idle = 0, Starting = 1, Running = 2, Failed = 3 }

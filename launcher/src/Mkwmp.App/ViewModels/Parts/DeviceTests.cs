// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mkwmp.Core;
using Mkwmp.Core.Devices;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The microphone test: a live level (0..1, with a slow-falling peak) from
/// <see cref="IDeviceLab.StartMicTest"/>, optionally played back ("hear myself"). Local only.
/// </summary>
public sealed class MicTester : ObservableObject, IDisposable
{
    private readonly ILauncher _launcher;
    private readonly DispatcherTimer _timer;
    private float _latest;
    private double _level;
    private double _peak;
    private bool _running;
    private bool _echo;

    public MicTester(ILauncher launcher)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
    }

    public bool Running
    {
        get => _running;
        set
        {
            if (value == _running)
            {
                return;
            }
            if (value)
            {
                Start();
            }
            else
            {
                Stop();
            }
        }
    }

    /// <summary>Plays the microphone on the chosen output while testing.</summary>
    public bool Echo
    {
        get => _echo;
        set
        {
            if (Set(ref _echo, value) && _running)
            {
                Start();
            }
        }
    }

    /// <summary>0..1, smoothed.</summary>
    public double Level
    {
        get => _level;
        private set => Set(ref _level, value);
    }

    /// <summary>0..1: the loudest of the last second or so.</summary>
    public double Peak
    {
        get => _peak;
        private set => Set(ref _peak, value);
    }

    /// <summary>(Re)starts with the settings as they are now (device, gain, output).</summary>
    public void Start()
    {
        var voice = _launcher.Settings.Voice;
        _launcher.Devices.MicLevel -= OnLevel;
        _launcher.Devices.MicLevel += OnLevel;
        _launcher.Devices.StartMicTest(voice.Input, Math.Clamp(voice.InputGain, 0, 100), _echo, voice.Output);
        _timer.Start();
        if (!_running)
        {
            _running = true;
            Raise(nameof(Running));
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }
        _running = false;
        _timer.Stop();
        _launcher.Devices.MicLevel -= OnLevel;
        _launcher.Devices.StopMicTest();
        Volatile.Write(ref _latest, 0f);
        Level = 0;
        Peak = 0;
        Raise(nameof(Running));
    }

    /// <summary>The settings changed while testing: follow them.</summary>
    public void Restart()
    {
        if (_running)
        {
            Start();
        }
    }

    // any thread, about 30 times a second
    private void OnLevel(object? sender, float level) => Volatile.Write(ref _latest, level);

    private void Tick()
    {
        var now = Math.Clamp(Volatile.Read(ref _latest), 0f, 1f);
        Level = now > _level ? now : _level * 0.78 + now * 0.22;
        Peak = Math.Max(now, _peak - 0.012);
    }

    public void Dispose() => Stop();
}

/// <summary>
/// The camera test's picture: the newest frame of <see cref="IDeviceLab"/> in a bitmap, about 30
/// times a second. Local only: nothing is sent anywhere.
/// </summary>
public sealed class CameraPreview : ObservableObject, IDisposable
{
    public const int FrameWidth = 640;
    public const int FrameHeight = 360;

    private readonly IDeviceLab _lab;
    private readonly DispatcherTimer _timer;
    private readonly byte[] _pixels = new byte[FrameWidth * FrameHeight * 4];
    private WriteableBitmap? _bitmap;
    private uint _serial;
    private bool _running;
    private bool _hasFrame;
    private CameraStatus _status;

    public CameraPreview(IDeviceLab lab)
    {
        _lab = lab ?? throw new ArgumentNullException(nameof(lab));
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
    }

    public bool Running
    {
        get => _running;
        private set => Set(ref _running, value);
    }

    /// <summary>The picture; null until the first frame.</summary>
    public ImageSource? Frame => _hasFrame ? _bitmap : null;

    public bool HasFrame
    {
        get => _hasFrame;
        private set
        {
            if (Set(ref _hasFrame, value))
            {
                Raise(nameof(Frame));
            }
        }
    }

    public CameraStatus Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Why there is no camera test at all (mkwmp-camera.dll missing), or null.</summary>
    public string? Problem => _lab.CameraAvailable ? null : _lab.CameraProblem ?? CoreStrings.CameraDllMissing;

    public void Start(string deviceId, bool mirror)
    {
        if (!_lab.CameraAvailable)
        {
            Raise(nameof(Problem));
            return;
        }
        _serial = 0;
        HasFrame = false;
        _lab.StartCamera(deviceId, FrameWidth, FrameHeight, 30, mirror);
        Status = CameraStatus.Starting;
        Running = true;
        _timer.Start();
    }

    public void SetMirror(bool mirror)
    {
        if (_running)
        {
            _lab.SetCameraMirror(mirror);
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }
        _timer.Stop();
        _lab.StopCamera();
        Running = false;
        HasFrame = false;
        Status = CameraStatus.Idle;
    }

    private void Tick()
    {
        Status = _lab.CameraStatus;
        if (!_lab.TryCopyFrame(_pixels, ref _serial))
        {
            return;
        }
        // RGBA from the camera, BGRA for the bitmap
        var pixels = _pixels.AsSpan();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }
        _bitmap ??= new WriteableBitmap(FrameWidth, FrameHeight, 96, 96, PixelFormats.Bgr32, null);
        _bitmap.WritePixels(new Int32Rect(0, 0, FrameWidth, FrameHeight), _pixels, FrameWidth * 4, 0);
        HasFrame = true;
    }

    public void Dispose() => Stop();
}

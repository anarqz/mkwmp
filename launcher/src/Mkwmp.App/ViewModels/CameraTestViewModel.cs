// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.App.Input;
using Mkwmp.Core.Devices;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Settings › Voice &amp; Camera › Test Camera: the chosen camera's live picture, full page. Only this
/// screen sees it; the camera stops when the page is left. (View: Views/Settings/CameraTestPage.xaml.)
/// </summary>
public sealed class CameraTestViewModel : PageViewModel
{
    private string _deviceName = "";

    public CameraTestViewModel(ShellViewModel shell)
        : base(shell)
    {
        Preview = new CameraPreview(Launcher.Devices);
        Preview.PropertyChanged += (_, _) =>
        {
            Raise(nameof(StatusText));
            Raise(nameof(ShowSpinner));
        };
    }

    public CameraPreview Preview { get; }

    public bool Mirror => Launcher.Settings.Voice.CameraMirror;

    /// <summary>Not offered under Wine / Proton (no capture there yet), or without mkwmp-camera.dll.</summary>
    private string? Unavailable => Links.IsWine ? S["settings.chat.camera_linux"] : Preview.Problem;

    /// <summary>What is shown instead of the picture; "" while it is live.</summary>
    public string StatusText
    {
        get
        {
            if (Unavailable is { } why)
            {
                return why;
            }
            if (Preview.HasFrame && Preview.Status != CameraStatus.Failed)
            {
                return "";
            }
            return Preview.Status == CameraStatus.Failed ? S["settings.camera_test.failed"] : S["settings.camera_test.starting"];
        }
    }

    public bool ShowSpinner => Unavailable is null && !Preview.HasFrame && Preview.Status != CameraStatus.Failed;

    public string DeviceLine => S.Format("settings.camera_test.device", ("name", _deviceName.Length > 0 ? _deviceName : S["settings.chat.camera_first"]))
        + "  ·  " + (Mirror ? S["settings.camera_test.mirrored"] : S["settings.camera_test.not_mirrored"]);

    public string PrivacyLine => S["settings.camera_test.private"];

    public override IReadOnlyList<Hint> Hints =>
    [
        new Hint(PadButton.X, S["settings.camera_test.mirror"]),
        new Hint(PadButton.B, S["hint.back"]),
    ];

    public override void OnEnter()
    {
        if (Unavailable is not null)
        {
            return;
        }
        var id = Launcher.Settings.Voice.Camera;
        Preview.Start(id, Mirror);
        RelayCommand.Async(async () =>
        {
            var devices = await Launcher.Devices.CameraDevicesAsync().ConfigureAwait(true);
            _deviceName = (id.Length == 0 ? (devices.Count > 0 ? devices[0] : null) : devices.FirstOrDefault(d => d.Id == id))?.Name ?? id;
            Raise(nameof(DeviceLine));
        }).Execute(null);
    }

    public override void OnLeave() => Preview.Stop();

    public override bool OnButton(PadButton button)
    {
        if (button != PadButton.X)
        {
            return false;
        }
        var mirror = !Mirror;
        Preview.SetMirror(mirror);
        RelayCommand.Async(() => Shell.UpdateSettingsAsync(s => s with { Voice = s.Voice with { CameraMirror = mirror } })).Execute(null);
        return true;
    }
}

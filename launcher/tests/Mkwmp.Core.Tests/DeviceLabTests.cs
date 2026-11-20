// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Devices;

namespace Mkwmp.Core.Tests;

/// <summary>The device tests' plumbing. Nothing here opens a camera or a microphone: only lists and the library's presence.</summary>
public class DeviceLabTests
{
    /// <summary>native/camera/build of this checkout, when the camera library was built here.</summary>
    private static string? BuiltCameraFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var build = Path.Combine(dir.FullName, "native", "camera", "build");
            if (File.Exists(Path.Combine(build, "mkwmp-camera.dll")))
            {
                return build;
            }
        }
        return null;
    }

    [Fact]
    public async Task Without_the_camera_library_the_camera_is_unavailable_with_a_reason()
    {
        using var lab = new DeviceLab(() => [Rig.NewFolder("no-camera")]);
        Assert.False(lab.CameraAvailable);
        Assert.Equal(Strings.CameraDllMissing, lab.CameraProblem);
        Assert.Empty(await lab.CameraDevicesAsync());
        Assert.Equal(CameraStatus.Idle, lab.CameraStatus);
        uint serial = 0;
        Assert.False(lab.TryCopyFrame(new byte[16], ref serial));
        lab.StartCamera("", 320, 240, 15, true); // nothing to start: no throw
        lab.StopCamera();
    }

    [Fact]
    public async Task The_camera_library_is_found_in_a_later_folder_and_lists_devices()
    {
        if (BuiltCameraFolder() is not { } built)
        {
            return; // not built in this checkout
        }
        var notes = new List<string>();
        using var lab = new DeviceLab(() => [Rig.NewFolder("empty"), built], notes.Add);
        Assert.True(lab.CameraAvailable, lab.CameraProblem);
        Assert.Null(lab.CameraProblem);
        var devices = await lab.CameraDevicesAsync();
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Name)));
        Assert.Equal(devices.Count, devices.Select(d => d.Id).Distinct().Count());
        Assert.Equal(CameraStatus.Idle, lab.CameraStatus);
        uint serial = 0;
        Assert.False(lab.TryCopyFrame(new byte[320 * 240 * 4], ref serial)); // not started
        Assert.Contains(notes, n => n.StartsWith("camera: loaded ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audio_devices_list_with_stable_ids_and_the_chime_is_short_and_soft()
    {
        using var lab = new DeviceLab(() => []);
        foreach (var list in new[] { await lab.InputDevicesAsync(), await lab.OutputDevicesAsync() })
        {
            Assert.All(list, d => Assert.False(string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Name)));
            Assert.Equal(list.Count, list.Select(d => d.Id).Distinct().Count());
        }
        Assert.Equal(await lab.OutputDevicesAsync(), await lab.OutputDevicesAsync());
        Assert.Equal(WinMm.WaveMapper, WinMm.IndexOf(input: true, ""));
        Assert.Equal(WinMm.WaveMapper, WinMm.IndexOf(input: false, "a device that was unplugged"));
        var chime = DeviceLab.Chime(1f);
        Assert.InRange(chime.Length, 48_000 / 4, 48_000);
        Assert.InRange(chime.Max(s => Math.Abs((int)s)), 4000, short.MaxValue / 2);
        Assert.All(DeviceLab.Chime(0f), s => Assert.Equal(0, s));
        lab.StopMicTest(); // none running: no throw
    }
}

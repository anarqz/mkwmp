// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;

namespace Mkwmp.App.ViewModels;

/// <summary>The battery of the top bar (GetSystemPowerStatus; Wine answers it on the Steam Deck).</summary>
public sealed partial class ShellViewModel
{
    private DateTime _nextBatteryRead;
    private bool _hasBattery;
    private int _batteryPercent;
    private bool _batteryCharging;
    private int? _batteryOverride;

    /// <summary>False on a desktop: the top bar hides the battery.</summary>
    public bool HasBattery
    {
        get => _hasBattery;
        private set => Set(ref _hasBattery, value);
    }

    public int BatteryPercent
    {
        get => _batteryPercent;
        private set => Set(ref _batteryPercent, value);
    }

    public bool BatteryCharging
    {
        get => _batteryCharging;
        private set => Set(ref _batteryCharging, value);
    }

    /// <summary>For screenshots (<c>--battery 93</c>): shows this instead of the real one.</summary>
    public void OverrideBattery(int percent)
    {
        _batteryOverride = percent;
        ReadBattery();
    }

    private void ReadBattery()
    {
        _nextBatteryRead = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        if (_batteryOverride is { } forced)
        {
            HasBattery = true;
            BatteryPercent = forced;
            BatteryCharging = false;
            return;
        }
        try
        {
            // BatteryFlag 128: no system battery; 255 / percent 255: unknown
            if (GetSystemPowerStatus(out var status) && (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255 && status.BatteryLifePercent <= 100)
            {
                HasBattery = true;
                BatteryPercent = status.BatteryLifePercent;
                BatteryCharging = status.ACLineStatus == 1;
                return;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no battery to show
        }
        HasBattery = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

#pragma warning disable SYSLIB1054 // plain DllImport: blittable, and no unsafe code in this project
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
#pragma warning restore SYSLIB1054
}

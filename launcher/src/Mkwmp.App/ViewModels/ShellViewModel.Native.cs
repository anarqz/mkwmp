// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.App.Platform;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The battery and network of the top bar (<see cref="SystemStatus"/>: Windows APIs, or the Linux
/// kernel's files under Wine/Proton). Read off the UI thread every 5 s, and at once when Windows
/// reports a power change, so plugging the charger in or out shows up right away.
/// </summary>
public sealed partial class ShellViewModel
{
    private DateTime _nextBatteryRead;
    private bool _hasBattery;
    private int _batteryPercent;
    private PowerSource _power;
    private NetworkKind _network = NetworkKind.Wifi;
    private int _wifiBars = 3;
    private BatteryStatus? _batteryOverride;
    private NetworkStatus? _networkOverride;
    private bool _powerEventsHooked;
    private int _reading;

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

    public PowerSource Power
    {
        get => _power;
        private set
        {
            if (Set(ref _power, value))
            {
                Raise(nameof(BatteryCharging));
                Raise(nameof(BatteryPluggedIn));
            }
        }
    }

    /// <summary>The battery is filling (bolt, green fill).</summary>
    public bool BatteryCharging => _power == PowerSource.Charging;

    /// <summary>On the charger without filling (plug).</summary>
    public bool BatteryPluggedIn => _power == PowerSource.PluggedIn;

    /// <summary>Wi-Fi, wired or offline (the machine's link; the server's state is on the Play tile).</summary>
    public NetworkKind Network
    {
        get => _network;
        private set
        {
            if (Set(ref _network, value))
            {
                Raise(nameof(NetworkOffline));
            }
        }
    }

    /// <summary>Wi-Fi strength 0..3.</summary>
    public int WifiBars
    {
        get => _wifiBars;
        private set => Set(ref _wifiBars, value);
    }

    public bool NetworkOffline => _network == NetworkKind.Offline;

    /// <summary>For screenshots (<c>--battery 93</c>, <c>40c</c>, <c>100p</c>): shows this instead of the real one.</summary>
    public void OverrideBattery(int percent, PowerSource power = PowerSource.OnBattery)
    {
        _batteryOverride = new BatteryStatus(percent, power);
        Apply(_batteryOverride, null);
    }

    /// <summary>For screenshots (<c>--network wifi2</c>, <c>wired</c>, <c>offline</c>).</summary>
    public void OverrideNetwork(NetworkStatus network)
    {
        ArgumentNullException.ThrowIfNull(network);
        _networkOverride = network;
        Network = network.Kind;
        WifiBars = network.Bars;
    }

    private void ReadBattery()
    {
        _nextBatteryRead = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        HookPowerEvents();
        if (Interlocked.Exchange(ref _reading, 1) == 1)
        {
            return; // the last read is still running (a slow network or /sys answer)
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        _ = Task.Run(() =>
        {
            try
            {
                var battery = _batteryOverride ?? SystemStatus.ReadBattery();
                var network = _networkOverride ?? SystemStatus.ReadNetwork();
                // an override set while this read ran still wins (screenshots)
                dispatcher?.BeginInvoke(() => Apply(_batteryOverride ?? battery, _networkOverride ?? network));
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
            }
        });
    }

    private void Apply(BatteryStatus? battery, NetworkStatus? network)
    {
        HasBattery = battery is not null;
        if (battery is not null)
        {
            BatteryPercent = battery.Percent;
            Power = battery.Source;
        }
        if (network is not null)
        {
            Network = network.Kind;
            WifiBars = network.Bars;
        }
    }

    private void HookPowerEvents()
    {
        if (_powerEventsHooked)
        {
            return;
        }
        _powerEventsHooked = true;
        try
        {
            // raised on a system thread: read again on the UI thread
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.StatusChange)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(ReadBattery);
                }
            };
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(ReadBattery);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.ExternalException or System.Net.NetworkInformation.NetworkInformationException)
        {
            // no system events (some Wine setups): the 5 s read still runs
        }
    }
}

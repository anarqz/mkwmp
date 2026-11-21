// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Mkwmp.App.Platform;

/// <summary>Where the power comes from, for the battery icon.</summary>
public enum PowerSource
{
    OnBattery,

    /// <summary>The charger is in and the battery is filling.</summary>
    Charging,

    /// <summary>The charger is in but the battery is not filling (full, or held by the system).</summary>
    PluggedIn,
}

public sealed record BatteryStatus(int Percent, PowerSource Source);

public enum NetworkKind { Offline, Wifi, Wired }

/// <param name="Bars">Wi-Fi strength 0..3 (Wired/Offline: 0).</param>
public sealed record NetworkStatus(NetworkKind Kind, int Bars);

/// <summary>
/// The battery and the network of the top bar, on Windows and under Wine/Proton (Steam Deck).
/// Windows answers through its APIs. Under Wine those are thin (no battery on some builds, no
/// Wi-Fi signal at all), so the Linux files the kernel publishes are read instead, through Wine's
/// Z: drive (the Linux root): /sys/class/power_supply for the battery and charger,
/// /proc/net/wireless and /sys/class/net for the network. Every read is cheap and never throws.
/// </summary>
public static class SystemStatus
{
    /// <summary>Running under Wine/Proton (<see cref="Core.Platform.WineHost"/>).</summary>
    public static bool IsWine => Core.Platform.WineHost.Detected;

    /// <summary>The Linux root as Wine maps it (null on Windows, or when Z: is not mapped).</summary>
    internal static string? LinuxRoot => Core.Platform.WineHost.LinuxRoot;

    /// <summary>The battery, or null when the machine has none (a desktop).</summary>
    public static BatteryStatus? ReadBattery()
    {
        if (LinuxRoot is { } root && ReadLinuxBattery(Path.Combine(root, "sys", "class", "power_supply")) is { } linux)
        {
            return linux;
        }
        return ReadWindowsBattery();
    }

    public static NetworkStatus ReadNetwork()
    {
        if (LinuxRoot is { } root)
        {
            return ReadLinuxNetwork(Path.Combine(root, "proc", "net", "wireless"), Path.Combine(root, "sys", "class", "net"));
        }
        return ReadWindowsNetwork();
    }

    /// <summary>
    /// /sys/class/power_supply: a supply of <c>type</c> Battery gives <c>capacity</c> (0-100) and
    /// <c>status</c> (Charging, Discharging, Full, Not charging); a Mains/USB supply with
    /// <c>online</c> 1 means the charger is in. The Steam Deck has BAT1 and ACAD.
    /// </summary>
    internal static BatteryStatus? ReadLinuxBattery(string powerSupply)
    {
        try
        {
            if (!Directory.Exists(powerSupply))
            {
                return null;
            }
            int? percent = null;
            string? status = null;
            var mains = false;
            foreach (var dir in Directory.EnumerateDirectories(powerSupply))
            {
                var type = ReadLine(Path.Combine(dir, "type"));
                if (string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase))
                {
                    // a peripheral's battery (a controller, a mouse) has scope Device: not ours
                    if (string.Equals(ReadLine(Path.Combine(dir, "scope")), "Device", StringComparison.OrdinalIgnoreCase) || percent is not null)
                    {
                        continue;
                    }
                    if (int.TryParse(ReadLine(Path.Combine(dir, "capacity")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity))
                    {
                        percent = Math.Clamp(capacity, 0, 100);
                        status = ReadLine(Path.Combine(dir, "status"));
                    }
                }
                else if (type is not null && ReadLine(Path.Combine(dir, "online")) == "1")
                {
                    mains = true;
                }
            }
            if (percent is not { } p)
            {
                return null;
            }
            var source = status switch
            {
                "Charging" => PowerSource.Charging,
                "Full" or "Not charging" => PowerSource.PluggedIn,
                _ when mains => PowerSource.PluggedIn,
                _ => PowerSource.OnBattery,
            };
            return new BatteryStatus(p, source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// /proc/net/wireless lists the connected Wi-Fi interfaces with a link quality (0..70 on most
    /// drivers); otherwise a non-loopback interface whose operstate is up is a wired link.
    /// </summary>
    internal static NetworkStatus ReadLinuxNetwork(string procWireless, string sysNet)
    {
        try
        {
            if (File.Exists(procWireless))
            {
                // Inter-| sta-|   Quality        |   ...
                //  face | tus | link level noise |
                // wlan0: 0000   54.  -56.  -256 ...
                foreach (var line in File.ReadLines(procWireless).Skip(2))
                {
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon < 0)
                    {
                        continue;
                    }
                    var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length > 1 && double.TryParse(fields[1].TrimEnd('.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var link) && link > 0)
                    {
                        return new NetworkStatus(NetworkKind.Wifi, Bars(link / 70.0 * 100));
                    }
                }
            }
            if (Directory.Exists(sysNet))
            {
                foreach (var dir in Directory.EnumerateDirectories(sysNet))
                {
                    var name = Path.GetFileName(dir);
                    if (name == "lo" || Directory.Exists(Path.Combine(dir, "wireless")) || !Directory.Exists(Path.Combine(dir, "device")))
                    {
                        continue; // loopback, Wi-Fi without a link, virtual (docker, bridges, VPNs)
                    }
                    if (ReadLine(Path.Combine(dir, "operstate")) == "up")
                    {
                        return new NetworkStatus(NetworkKind.Wired, 0);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // unknown: offline
        }
        return new NetworkStatus(NetworkKind.Offline, 0);
    }

    /// <summary>Signal quality 0..100 to the icon's 0..3 bars.</summary>
    internal static int Bars(double quality) => quality switch
    {
        >= 60 => 3,
        >= 35 => 2,
        > 0 => 1,
        _ => 0,
    };

    private static BatteryStatus? ReadWindowsBattery()
    {
        try
        {
            // BatteryFlag 128: no system battery; 255 / percent 255: unknown; 8: charging. ACLineStatus 1: on the charger.
            if (GetSystemPowerStatus(out var s) && (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255 && s.BatteryLifePercent <= 100)
            {
                var source = (s.BatteryFlag & 8) != 0 ? PowerSource.Charging
                    : s.ACLineStatus == 1 ? PowerSource.PluggedIn
                    : PowerSource.OnBattery;
                return new BatteryStatus(s.BatteryLifePercent, source);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no battery to show
        }
        return null;
    }

    private static NetworkStatus ReadWindowsNetwork()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && n.GetIPProperties().GatewayAddresses.Count > 0)
                .ToList();
            if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
            {
                return new NetworkStatus(NetworkKind.Wired, 0);
            }
            if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            {
                return new NetworkStatus(NetworkKind.Wifi, WifiBars() is { } bars ? SignalBars(bars) : 3);
            }
            // a link of another kind (USB tethering, a VM adapter) still reaches the internet
            return up.Count > 0 ? new NetworkStatus(NetworkKind.Wired, 0) : new NetworkStatus(NetworkKind.Offline, 0);
        }
        catch (NetworkInformationException)
        {
            return new NetworkStatus(NetworkKind.Offline, 0);
        }
    }

    /// <summary>Windows' 0..5 signal bars (the taskbar's) to the icon's 0..3.</summary>
    internal static int SignalBars(int windowsBars) => windowsBars switch
    {
        >= 4 => 3,
        >= 2 => 2,
        > 0 => 1,
        _ => 0,
    };

    // The Wi-Fi strength comes from NetworkInformation.GetInternetConnectionProfile(): IsWlanConnectionProfile and
    // GetSignalBars(). Not from the WLAN API: since Windows 11 24H2, WlanQueryInterface(current_connection) and the
    // other calls that return the network's name (SSID) or its access point (BSSID) count as location access, light
    // the "location in use" icon and are logged under Settings > Privacy > Location. Signal bars are neither, so this
    // never reads the SSID, the BSSID, the profile's name or the network names. The WinRT class is called through
    // its ABI (combase), without the Windows SDK projection, which would add 25 MB to the launcher.
    private static readonly Guid NetworkInformationStatics = new("5074F851-950D-4165-9C15-365619481EEA");
    private static readonly Guid ConnectionProfile2 = new("E2045145-4C9F-400C-9150-7EC7D6E2888A");
    private static IntPtr _networkInformation;
    private static bool _winRtFailed;

    /// <summary>The internet connection's Wi-Fi signal bars (0..5), or null (not Wi-Fi, unknown, no WinRT).</summary>
    private static int? WifiBars()
    {
        if (_winRtFailed)
        {
            return null;
        }
        IntPtr profile = IntPtr.Zero, profile2 = IntPtr.Zero, bars = IntPtr.Zero;
        try
        {
            if (_networkInformation == IntPtr.Zero && !ActivateNetworkInformation())
            {
                _winRtFailed = true;
                return null;
            }
            // INetworkInformationStatics: IInspectable (slots 0-5), GetConnectionProfiles (6), GetInternetConnectionProfile (7)
            if (Slot<GetPointer>(_networkInformation, 7)(_networkInformation, out profile) < 0 || profile == IntPtr.Zero)
            {
                return null; // no internet profile
            }
            var iid = ConnectionProfile2;
            if (Marshal.QueryInterface(profile, in iid, out profile2) < 0)
            {
                return null;
            }
            // IConnectionProfile2: IsWwanConnectionProfile (6), IsWlanConnectionProfile (7), ..., GetSignalBars (11)
            if (Slot<GetByte>(profile2, 7)(profile2, out var wlan) < 0 || wlan == 0)
            {
                return null;
            }
            if (Slot<GetPointer>(profile2, 11)(profile2, out bars) < 0 || bars == IntPtr.Zero)
            {
                return null; // IReference<byte> null: no signal reading
            }
            // IReference<byte>: IInspectable (0-5), get_Value (6)
            return Slot<GetByte>(bars, 6)(bars, out var value) < 0 ? null : Math.Clamp((int)value, 0, 5);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidCastException)
        {
            _winRtFailed = true;
            return null;
        }
        finally
        {
            foreach (var p in (ReadOnlySpan<IntPtr>)[bars, profile2, profile])
            {
                if (p != IntPtr.Zero)
                {
                    _ = Marshal.Release(p);
                }
            }
        }
    }

    /// <summary>The NetworkInformation statics, kept for the process's life; false when WinRT has none.</summary>
    private static bool ActivateNetworkInformation()
    {
        // the read runs on a thread-pool thread: keep the process's MTA alive for the WinRT objects it uses
        if (CoIncrementMTAUsage(out _) < 0)
        {
            return false;
        }
        const string Class = "Windows.Networking.Connectivity.NetworkInformation";
        if (WindowsCreateString(Class, (uint)Class.Length, out var name) < 0)
        {
            return false;
        }
        try
        {
            var iid = NetworkInformationStatics;
            return RoGetActivationFactory(name, ref iid, out _networkInformation) >= 0 && _networkInformation != IntPtr.Zero;
        }
        finally
        {
            _ = WindowsDeleteString(name);
        }
    }

    /// <summary>The method in a COM object's vtable slot.</summary>
    private static T Slot<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPointer(IntPtr self, out IntPtr value);

    /// <summary>A boolean or a byte out (WinRT's boolean is one byte).</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetByte(IntPtr self, out byte value);

    private static string? ReadLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    [DllImport("combase.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoIncrementMTAUsage(out IntPtr cookie);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
#pragma warning restore SYSLIB1054
}

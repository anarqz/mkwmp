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
                return new NetworkStatus(NetworkKind.Wifi, Bars(WifiQuality() ?? 100));
            }
            // a link of another kind (USB tethering, a VM adapter) still reaches the internet
            return up.Count > 0 ? new NetworkStatus(NetworkKind.Wired, 0) : new NetworkStatus(NetworkKind.Offline, 0);
        }
        catch (NetworkInformationException)
        {
            return new NetworkStatus(NetworkKind.Offline, 0);
        }
    }

    /// <summary>The connected Wi-Fi's signal quality (0..100) through the WLAN API, or null.</summary>
    private static uint? WifiQuality()
    {
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0 || WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0)
            {
                return null;
            }
            // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, then items of
            // { GUID InterfaceGuid; WCHAR strInterfaceDescription[256]; WLAN_INTERFACE_STATE isState; } (532 bytes)
            var count = Marshal.ReadInt32(list);
            for (var i = 0; i < count; i++)
            {
                var item = list + 8 + (i * 532);
                if (Marshal.ReadInt32(item + 528) != 1)
                {
                    continue; // wlan_interface_state_connected
                }
                var guid = Marshal.PtrToStructure<Guid>(item);
                if (WlanQueryInterface(handle, ref guid, 7, IntPtr.Zero, out _, out var data, out _) != 0)
                {
                    continue; // wlan_intf_opcode_current_connection
                }
                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES: isState, mode, strProfileName[256] (520 bytes), then
                    // WLAN_ASSOCIATION_ATTRIBUTES: DOT11_SSID (36), bssType (4), bssid (6 + 2 padding),
                    // phyType (4), phyIndex (4), wlanSignalQuality at 520 + 56 = 576.
                    return (uint)Marshal.ReadInt32(data + 576);
                }
                finally
                {
                    WlanFreeMemory(data);
                }
            }
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (list != IntPtr.Zero)
            {
                WlanFreeMemory(list);
            }
            if (handle != IntPtr.Zero)
            {
                _ = WlanCloseHandle(handle, IntPtr.Zero);
            }
        }
    }

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

    [DllImport("wlanapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out uint dataSize, out IntPtr data, out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WlanFreeMemory(IntPtr memory);
#pragma warning restore SYSLIB1054
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
// Ported from Infinity Launcher (Infinity.Core/Process/WineHost.cs, https://github.com/anarqz/InfinityHorizon).
using System.Runtime.InteropServices;

namespace Mkwmp.Core.Platform;

/// <summary>
/// Running under Wine / Proton (SteamOS, Linux). ntdll exports <c>wine_get_version</c>, but
/// Proton can hide it (HideWineExports), so Wine's environment and registry count too. Under Wine
/// a DLL next to the game's exe is not loaded over the built-in one unless the per-application
/// DLL override says so: the probe (<c>winmm.dll</c> beside mkw.exe) needs <c>winmm=native,builtin</c>.
/// </summary>
public static class WineHost
{
    public static bool Detected => How is not null;

    /// <summary>How Wine was recognised (for the log), or null on Windows.</summary>
    public static string? How { get; } = Detect();

    /// <summary>The Linux root as Wine maps it (<c>Z:\</c>), or null on Windows / when unmapped.</summary>
    public static string? LinuxRoot { get; } = Detected && Directory.Exists(@"Z:\proc") ? @"Z:\" : null;

    /// <summary>The value written for a native-first DLL: the game folder's DLL, Wine's own if it is gone.</summary>
    public const string NativeFirst = "native,builtin";

    /// <summary>The per-application override key Wine reads when <paramref name="exe"/> loads (HKCU, the prefix's registry).</summary>
    public static string AppDefaultsKey(string exe) => $@"Software\Wine\AppDefaults\{exe}\DllOverrides";

    /// <summary>
    /// A CreateProcess environment block does not reach Wine's load-order code in the child, so
    /// <c>WINEDLLOVERRIDES</c> alone never loads a game-folder DLL (Infinity, 2026-10-19). The
    /// per-app registry override does, and only for <paramref name="exe"/>: set when
    /// <paramref name="want"/>, removed when not, if it is the launcher's own value. Returns what
    /// happened, for the log. Nothing to do on Windows.
    /// </summary>
    public static string EnsureDllOverride(string exe, string dll, bool want)
    {
        if (!Detected)
        {
            return "not wine";
        }
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AppDefaultsKey(exe), writable: true);
            var now = key.GetValue(dll) as string;
            if (want)
            {
                if (string.Equals(now, NativeFirst, StringComparison.OrdinalIgnoreCase))
                {
                    return "already";
                }
                key.SetValue(dll, NativeFirst, Microsoft.Win32.RegistryValueKind.String);
                return now is null ? "set" : $"set (was {now})";
            }
            if (string.Equals(now, NativeFirst, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(dll, throwOnMissingValue: false);
                return "removed";
            }
            return now is null ? "none" : $"left ({now}, not ours)";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return "failed: " + e.Message;
        }
    }

    /// <summary>
    /// Whether the per-application override loads <paramref name="dll"/> beside <paramref name="exe"/> native first
    /// (what <see cref="EnsureDllOverride"/> writes); null on Windows, where nothing needs it. Reads one registry value.
    /// </summary>
    public static bool? HasDllOverride(string exe, string dll)
    {
        if (!Detected)
        {
            return null;
        }
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppDefaultsKey(exe));
            return key?.GetValue(dll) is string now && now.StartsWith("native", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null; // not readable: not said to be missing
        }
    }

    private static string? Detect()
    {
        var ntdll = GetModuleHandleW("ntdll.dll");
        if (ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero)
        {
            return "ntdll wine_get_version";
        }
        foreach (var name in new[] { "WINEPREFIX", "WINELOADER", "WINEDLLOVERRIDES", "STEAM_COMPAT_DATA_PATH" })
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                return "environment " + name;
            }
        }
        using var wine = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Wine");
        return wine is not null ? @"registry HKCU\Software\Wine" : null;
    }

#pragma warning disable SYSLIB1054 // plain DllImport, as Infinity's
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
#pragma warning restore SYSLIB1054
}

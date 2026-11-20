// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Microsoft.Win32;

namespace Mkwmp.Core;

/// <summary>"Start with the system": the launcher starts at login, straight into the tray.</summary>
public interface IAutostart
{
    /// <summary>Makes the system's start-up entry match <paramref name="on"/> (also refreshes the path after the launcher moved).</summary>
    void Apply(bool on, string executable);
}

/// <summary>
/// The per-user Run key (<c>HKCU\…\Run</c>, value <c>MKWMP</c> = <c>"&lt;exe&gt;" --tray</c>): the same
/// entry the Rust launcher wrote, so switching launchers never leaves two.
/// </summary>
public sealed class RegistryAutostart : IAutostart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "MKWMP";

    public void Apply(bool on, string executable)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                run.SetValue(ValueName, $"\"{executable}\" --tray");
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new LauncherException($"start with the system: {e.Message}", e);
        }
    }
}

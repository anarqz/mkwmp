// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Mkwmp.App.ViewModels;

/// <summary>The few places outside the launcher that its pages open: https pages in the browser, a saved file in the file manager.</summary>
internal static class Links
{
    public const string Repo = "https://github.com/anarqz/mkwmp";
    public const string Issues = Repo + "/issues";
    public const string BasedOn = "https://github.com/anarqz/InfinityHorizon";

    /// <summary>
    /// The launcher runs under Wine / Proton (Linux, the Steam Deck): <see cref="Mkwmp.Core.Platform.WineHost"/>
    /// decides. MKWMP_FAKE_WINE=1 only pretends, to look at those states of the pages on Windows.
    /// </summary>
    public static bool IsWine => Mkwmp.Core.Platform.WineHost.Detected || Environment.GetEnvironmentVariable("MKWMP_FAKE_WINE") == "1";

    /// <summary>Steam Deck Game Mode (gamescope): no system tray there, so closing the window really closes.</summary>
    public static bool GameMode =>
        Environment.GetEnvironmentVariable("SteamGamepadUI") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY") is { Length: > 0 }
        || string.Equals(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "gamescope", StringComparison.OrdinalIgnoreCase);

    /// <summary>The server's name rule (as Mkwmp.Core's ClientConfig.ValidName): 3-16 of letters, digits, - and _.</summary>
    public static bool ValidName(string? name) =>
        name is { Length: >= 3 and <= 16 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Opens an https page in the browser; anything else is ignored. False when nothing could open it.</summary>
    public static bool Open(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        return Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
    }

    public static bool Open(Uri? page) => Open(page?.AbsoluteUri);

    /// <summary>
    /// Opens the page and tells the player where it went: under Wine / Proton the browser may open
    /// behind the launcher (Desktop Mode) or not at all (Game Mode), so the address is always shown there.
    /// </summary>
    public static void Open(ShellViewModel shell, string? url)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (string.IsNullOrEmpty(url))
        {
            return;
        }
        var opened = Open(url);
        if (!opened)
        {
            shell.ShowToast(shell.S["settings.link_failed"], url);
        }
        else if (IsWine)
        {
            shell.ShowToast(shell.S["settings.link_opened"], url);
        }
    }

    /// <summary>The Linux home as Wine shows it (<c>Z:\home\deck</c>), or null on Windows.</summary>
    public static string? WineHome()
    {
        if (!IsWine || Environment.GetEnvironmentVariable("HOME") is not { Length: > 1 } home || home[0] != '/')
        {
            return null;
        }
        var path = "Z:" + home.Replace('/', '\\');
        return Directory.Exists(path) ? path : Directory.Exists("Z:\\") ? "Z:\\" : null;
    }

    /// <summary>Shows a file in its folder.</summary>
    public static bool ShowInFolder(string path)
    {
        if (File.Exists(path))
        {
            return Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return folder is { Length: > 0 } && Directory.Exists(folder) && Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            App.Log("open: " + e.Message);
            return false;
        }
    }
}

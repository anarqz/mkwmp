// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// The MKWMP mod's per-player settings in mkw-recomp: the host keeps them in
/// <c>&lt;data&gt;/profile/mod-settings/mkwmp.ini</c>, flat <c>key=value</c> lines (LF, no sections;
/// native/probe/src/mkw_plugin.h, setting_get / setting_set). The game rewrites the file on every
/// change it makes itself, so the launcher writes only while the game is closed, and keeps every
/// line it does not change. Only the first player's folder (<c>profile/</c>) is handled: with
/// <c>MKW_PROFILE_DIR</c> or <c>MKW_PLAYER</c> set the file is somewhere else and nothing is read or written.
/// </summary>
public static class ModSettings
{
    /// <summary>"1": my camera is shown to mutual friends in my friend room (off unless exactly "1").</summary>
    public const string Camera = "camera";

    /// <summary>"1": I see my friends' cameras.</summary>
    public const string CameraView = "camera_view";

    public static string GameIsRunning => Strings.ModSettingsRunning;
    public static string NoProfileYet => Strings.ModSettingsNoProfileYet;

    /// <summary>The settings file of this install's first player; null when this is not mkw-recomp or the folder is redirected.</summary>
    public static string? PathFor(GameInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (install.TargetId != MkwRecompTarget.TargetId
            || Environment.GetEnvironmentVariable("MKW_PROFILE_DIR") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("MKW_PLAYER") is { Length: > 0 })
        {
            return null;
        }
        return Path.Combine(install.DataRoot ?? install.Root, "profile", "mod-settings", Recomp.Mod + ".ini");
    }

    /// <summary>Letters, digits, '.', '_', '-' (the host's rule).</summary>
    public static bool ValidKey(string key) =>
        key is { Length: > 0 and <= 64 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>The value of <paramref name="key"/>; null when it is unset, the file is missing or this target has no such settings.</summary>
    public static string? Read(GameInstall install, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (PathFor(install) is not { } path || !ValidKey(key))
        {
            return null;
        }
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            string? value = null;
            foreach (var line in File.ReadAllLines(path))
            {
                if (Split(line) is { } pair && pair.Key == key)
                {
                    value = pair.Value; // the last one wins, as a reader that fills a table would have it
                }
            }
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets <paramref name="key"/> (null removes it), keeping every other line. Throws
    /// <see cref="LauncherException"/> with what the player must do when the game runs or has never
    /// run, or when the file cannot be written. False when this target has no such settings.
    /// </summary>
    public static bool Write(IGameTarget target, GameInstall install, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(key);
        if (PathFor(install) is not { } path)
        {
            return false;
        }
        if (!ValidKey(key) || value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new LauncherException(Strings.ModSettingsNotASetting(key));
        }
        if (target.IsRunning(install))
        {
            throw new LauncherException(GameIsRunning);
        }
        var profile = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
        if (!Directory.Exists(profile))
        {
            throw new LauncherException(NoProfileYet);
        }
        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            var done = false;
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (Split(lines[i]) is not { } pair || pair.Key != key)
                {
                    continue;
                }
                if (value is null || done)
                {
                    lines.RemoveAt(i); // removed, or an older duplicate
                }
                else
                {
                    lines[i] = $"{key}={value}";
                    done = true;
                }
            }
            if (value is not null && !done)
            {
                lines.Add($"{key}={value}");
            }
            var text = new StringBuilder();
            foreach (var line in lines)
            {
                text.Append(line).Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".mkwmp-tmp";
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(Strings.GameSettingsSaveFailed(e.Message), e);
        }
    }

    private static (string Key, string Value)? Split(string line)
    {
        var at = line.IndexOf('=', StringComparison.Ordinal);
        return at <= 0 ? null : (line[..at].Trim(), line[(at + 1)..].TrimEnd('\r'));
    }
}

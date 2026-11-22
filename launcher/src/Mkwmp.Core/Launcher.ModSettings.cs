// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core;

/// <summary>
/// The MKWMP mod has no pages or tabs of its own in mkw-recomp's menus (1.4.3): everything is set
/// here, and the launcher hands the mod what it needs through the mod's settings file
/// (<see cref="ModSettings"/>, <c>profile/mod-settings/mkwmp.ini</c>). The mod reads them at the
/// game's start: the camera's switches and device in every case, and, when the game is started
/// without the launcher (mkw-recomp's own start screen, or Steam), the account it signs in with.
/// Written only while the game is closed (the game owns the file while it runs), before every start,
/// after a change of the settings, and when a game the launcher stood by for ends.
/// </summary>
public sealed partial class Launcher
{
    private string? _modSynced; // what was written last, for this install (null: not yet, or the game was running)

    /// <summary>
    /// What the mod's settings should hold now. The account (consent18, server, pin, pin_name), the
    /// seat mode and the voice settings only when this launcher has a complete account: a launcher that
    /// never signed in leaves what is there alone. The camera's device and mirror always.
    /// </summary>
    internal static IReadOnlyList<(string Key, string Value)> ModSettingsFor(LauncherSettings s)
    {
        var v = s.Voice;
        var keys = new List<(string, string)>
        {
            ("camera_device", v.Camera),
            ("camera_mirror", v.CameraMirror ? "1" : "0"),
        };
        if (HasServer(s))
        {
            keys.Add(("consent18", s.AdultConsent ? "1" : "0"));
        }
        if (HasServer(s) && s.Name.Length > 0 && ClientConfig.ValidPin(s.Pin))
        {
            keys.Add(("server", s.Server));
            keys.Add(("pin_name", s.Name));
            keys.Add(("pin", s.Pin));
            keys.Add(("seat_mode", s.Seat == SeatMode.Online ? "online" : "start"));
            keys.Add(("voice", v.Enabled ? "1" : "0"));
            keys.Add(("voice_input", v.Input));
            keys.Add(("voice_output", v.Output));
            keys.Add(("voice_in_gain", Math.Clamp(v.InputGain, 1, 100).ToString(CultureInfo.InvariantCulture)));
            keys.Add(("voice_out_gain", Math.Clamp(v.OutputGain, 1, 100).ToString(CultureInfo.InvariantCulture)));
        }
        return keys;
    }

    /// <summary>The game of the settings, when it is closed: the mod's settings get <see cref="ModSettingsFor"/>.</summary>
    private void SyncModSettings()
    {
        var target = _targets.Get(Settings.Target);
        if (GameFinder.Detect(target, Settings.Game) is { } install)
        {
            SyncModSettings(target, install, force: false);
        }
    }

    /// <summary>Writes what differs (nothing while the game runs, or for a target without mod settings). True when it wrote something.</summary>
    private bool SyncModSettings(IGameTarget target, GameInstall install, bool force)
    {
        if (ModSettings.PathFor(install) is not { } path)
        {
            return false;
        }
        var wanted = ModSettingsFor(Settings);
        var key = path + "\n" + string.Join("\n", wanted.Select(w => w.Key + "=" + w.Value));
        if (!force && key == Volatile.Read(ref _modSynced))
        {
            return false;
        }
        try
        {
            if (target.IsRunning(install))
            {
                return false; // the next look, once it is closed
            }
            var now = ModSettings.ReadAll(install, Recomp.Mod);
            var written = new List<string>();
            foreach (var (name, value) in wanted)
            {
                if (now.TryGetValue(name, out var have) && have == value)
                {
                    continue;
                }
                if (!target.WriteModSetting(install, name, value))
                {
                    return false;
                }
                written.Add(name);
            }
            Volatile.Write(ref _modSynced, key);
            if (written.Count > 0)
            {
                Note($"mod settings: {string.Join(", ", written)} written for the MKWMP mod (it has no settings page of its own)");
            }
            return written.Count > 0;
        }
        catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
        {
            // said once per state (the game never started yet has no profile folder); tried again before every start
            if (Interlocked.Exchange(ref _modSynced, key) != key)
            {
                Note($"mod settings: not written: {e.Message}");
            }
            return false;
        }
    }
}

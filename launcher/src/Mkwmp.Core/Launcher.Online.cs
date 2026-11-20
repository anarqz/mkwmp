// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// Offline first. A fresh install has no server (<see cref="LauncherSettings.Server"/> is ""): the
/// launcher is then a shell for the game. No server is contacted (no status poll, presence,
/// friends or reports), PLAY starts the game with no seat (Launcher.Session.cs), and the mod gets
/// only what its overlay needs. Choosing a server (<see cref="ProbeServerAsync"/>, then
/// <see cref="SaveSettingsAsync"/> with it) turns online on; clearing it (Disconnect) turns it off
/// again and keeps the name and, per server, the PIN (<see cref="SavedServers"/>).
/// </summary>
public sealed partial class Launcher
{
    public async Task<ServerProbe> ProbeServerAsync(string address)
    {
        var url = ClientConfig.ServerBase(address ?? "");
        if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            return new ServerProbe(ServerProbeState.Invalid, url, null, 0, 0, 0, Strings.ProbeInvalid);
        }
        try
        {
            var (status, answered, why) = await Api(url).ProbeAsync(_life.Token).ConfigureAwait(false);
            if (status is null)
            {
                // The details (a DNS error, a status code) go to the log; the player gets which of the two it was.
                Note($"probe {url}: {why}");
                return new ServerProbe(answered ? ServerProbeState.NotMkwmp : ServerProbeState.Unreachable, url, null, 0, 0, 0,
                    answered ? Strings.ProbeNotMkwmp : Strings.ProbeUnreachable);
            }
            if (status.LobbyEndpoint is null)
            {
                Note($"probe {url}: an MKWMP server without lobbyEndpoint (too old)");
                return new ServerProbe(ServerProbeState.TooOld, url, status.Name, status.SeatsTotal, status.Seated, status.Queued, Strings.ProbeTooOld);
            }
            return new ServerProbe(ServerProbeState.Ok, url, status.Name, status.SeatsTotal, status.Seated, status.Queued, null);
        }
        catch (OperationCanceledException)
        {
            return new ServerProbe(ServerProbeState.Unreachable, url, null, 0, 0, 0, Strings.ProbeUnreachable);
        }
    }
}

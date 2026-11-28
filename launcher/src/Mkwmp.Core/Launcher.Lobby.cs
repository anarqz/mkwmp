// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// The current lobby (docs/contracts/LAUNCHER_API.md, <c>POST /local/v1/roster</c>): the game tells who is in its
/// online lobby, by the id the server gave each member next to the name their game shows, and the launcher holds it
/// in memory (<see cref="CurrentLobby"/>) for its Current Lobby page and the game's overlay, so a player who renames
/// during the online session is still the same player.
/// <para>
/// NOTHING IS STORED: no file, and the log gets counts, never an id or a name; no report, crash record or server gets
/// any of it. The player shares it by copying a report (<see cref="LobbyReport"/>). The "Previous Races" history of
/// 1.5.4 is gone: what it left in <c>races\</c> beside client.toml is deleted at start.
/// </para>
/// </summary>
public sealed partial class Launcher
{
    private readonly CurrentLobby _lobby;

    public LobbyView Lobby => _lobby.View;

    /// <summary>"mkw.example.net" (with its port when it has one) of a server address; "" when there is none.</summary>
    internal static string HostOf(string server) =>
        Uri.TryCreate(ClientConfig.ServerBase(server), UriKind.Absolute, out var uri) ? uri.Authority : "";

    void ILocalApiHost.Roster(Roster roster)
    {
        bool nextendo;
        string server;
        lock (_gate)
        {
            nextendo = NextendoMode;
            server = nextendo ? new Uri(NextendoApi.Origin).Host : HostOf(_config.Settings.Server);
        }
        var outcome = _lobby.Apply(roster, nextendo, server);
        // Counts only: this log goes into bug reports.
        if (outcome.Started)
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"lobby: an online session started ({outcome.Members} players)"));
        }
        if (outcome.Match)
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"lobby: a match started ({outcome.Members} players)"));
        }
        if (outcome.Ended)
        {
            Note("lobby: the online session ended");
        }
        if (roster.Dropped > 0 && (outcome.Started || outcome.Match))
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"lobby: {roster.Dropped} members without an id were left out"));
        }
        if (outcome.Changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>The game ended without saying "left": nobody is in a lobby any more.</summary>
    private void EndLobby()
    {
        if (_lobby.End())
        {
            Note("lobby: the online session ended (the game closed)");
            RaiseChanged();
        }
    }

    /// <summary>At start: the history "Previous Races" kept in <c>races\</c> beside client.toml is deleted (the player asked for none to be kept).</summary>
    private void RemoveLegacyRaceHistory()
    {
        var deleted = CurrentLobby.RemoveLegacyHistory(ConfigPath);
        if (deleted > 0)
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"lobby: the stored history of previous races was deleted ({deleted} files)"));
        }
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// "Previous races" (docs/contracts/LAUNCHER_API.md, <c>POST /local/v1/roster</c>): the game tells who is in its
/// online lobby, by the id the server gave each member next to the name their game shows, and the launcher keeps it
/// in <c>races\</c> beside client.toml (<see cref="RaceHistory"/>), so a player who renames between races is still
/// the same player.
/// <para>
/// LOCAL ONLY: the log gets counts, never an id or a name; no report, crash record or server gets any of it. The
/// player shares it by copying a report (<see cref="RaceReport"/>).
/// </para>
/// </summary>
public sealed partial class Launcher
{
    private RaceHistory? _races;
    private int _raceRevision;

    private RaceHistory Races => LazyInitializer.EnsureInitialized(ref _races,
        () => new RaceHistory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ConfigPath)) ?? ".", RaceHistory.FolderName), _clock));

    public int RaceHistoryRevision => Volatile.Read(ref _raceRevision);

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
        var outcome = Races.Record(roster, nextendo ? RaceSession.Nextendo : RaceSession.Mkwmp, server);
        // Counts only: this log goes into bug reports.
        if (outcome.Started)
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"previous races: an online session started ({outcome.Members} players)"));
        }
        if (outcome.Match)
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"previous races: a match started ({outcome.Members} players)"));
        }
        if (outcome.Ended)
        {
            Note("previous races: the online session ended");
        }
        if (roster.Dropped > 0 && (outcome.Started || outcome.Match))
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"previous races: {roster.Dropped} members without an id were left out"));
        }
        if (outcome.SaveError is { } error)
        {
            Note($"previous races: the session could not be saved ({error})");
        }
        if (outcome.Changed)
        {
            Interlocked.Increment(ref _raceRevision);
            RaiseChanged();
        }
    }

    /// <summary>At start: what is older than 30 days, or beyond 200 files, goes.</summary>
    private void PruneRaceHistory()
    {
        var deleted = Races.Prune();
        if (deleted > 0)
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"previous races: {deleted} old sessions deleted"));
        }
    }

    public Task<IReadOnlyList<RaceSession>> RaceSessionsAsync() => Task.Run(Races.Sessions);

    public Task<RaceSession?> RaceSessionAsync(string id) => Task.Run(() => Races.Load(id));

    public Task<RacePlayerHistory?> RacePlayerAsync(string uid) => Task.Run(() => Races.Player(uid));

    public Task ClearRaceHistoryAsync() => Task.Run(() =>
    {
        var deleted = Races.Clear();
        Note(string.Create(CultureInfo.InvariantCulture, $"previous races: history cleared ({deleted} sessions)"));
        Interlocked.Increment(ref _raceRevision);
        RaiseChanged();
    });
}

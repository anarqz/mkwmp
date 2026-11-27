// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core;

namespace Mkwmp.App.Design;

/// <summary>
/// The demo's Previous Races: three made-up online sessions (two on Nextendo Network, one on an MKWMP server), in
/// memory only. One player kept the same Player ID under three names across two sessions ("Used 3 names"), another
/// renamed once; the demo's own player is in each ("You"). "Clear History" empties the list until the demo restarts.
/// </summary>
public sealed partial class DesignLauncher
{
    private const string DemoOwnUid = "6f1d2c9e-0b7a-5e43-9a10-3c5d8e7f1a20";

    /// <summary>The Player ID of the demo's player who used three names (the harness opens them: <c>--page races/player</c>).</summary>
    public const string DemoRenamedPlayer = "b3a9f5c1-77d2-5c0e-8f41-9e2ad6c0b7d4";

    private IReadOnlyList<RaceSession>? _demoRaces;
    private int _demoRacesRevision;

    public int RaceHistoryRevision => _demoRacesRevision;

    private IReadOnlyList<RaceSession> DemoRaces => _demoRaces ??= MakeDemoRaces(DateTimeOffset.UtcNow);

    public Task<IReadOnlyList<RaceSession>> RaceSessionsAsync() => Task.FromResult(DemoRaces);

    public Task<RaceSession?> RaceSessionAsync(string id) => Task.FromResult(DemoRaces.FirstOrDefault(s => s.Id == id));

    public Task<RacePlayerHistory?> RacePlayerAsync(string uid)
    {
        var sightings = DemoRaces.Where(s => s.Players.Any(p => p.Uid == uid))
            .Select(s => new RaceSighting(s, s.Players.First(p => p.Uid == uid), [.. s.Matches.Where(m => m.Members.Any(x => x.Uid == uid))]))
            .ToList();
        if (sightings.Count == 0)
        {
            return Task.FromResult<RacePlayerHistory?>(null);
        }
        var names = sightings.SelectMany(s => s.Player.Names).GroupBy(n => n.Name)
            .Select(g => new RaceName(g.Key, g.Min(n => n.First), g.Max(n => n.Last))).OrderBy(n => n.First).ToList();
        return Task.FromResult<RacePlayerHistory?>(new RacePlayerHistory(uid, sightings.Any(s => s.Player.Local), names, sightings));
    }

    public Task ClearRaceHistoryAsync()
    {
        Change(() =>
        {
            _demoRaces = [];
            _demoRacesRevision++;
        });
        return Task.CompletedTask;
    }

    private static IReadOnlyList<RaceSession> MakeDemoRaces(DateTimeOffset now)
    {
        static string Uid(int n) => $"{n:x8}-4c1e-5a2b-9d3f-{n * 7919L:x12}";
        static string Id(DateTimeOffset start) => "lobby-" + start.ToLocalTime().ToString("yyyy-MM-dd-HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);

        // One session: `rounds` are the names of everyone in each race (the uid, then the name shown in that race).
        static RaceSession Session(DateTimeOffset start, string backend, string server, params (string Uid, string Name)[][] rounds)
        {
            var players = new List<(string Uid, List<RaceName> Names)>();
            var matches = new List<RaceMatch>();
            for (var i = 0; i < rounds.Length; i++)
            {
                var time = start.AddMinutes(2 + (i * 5));
                foreach (var (uid, name) in rounds[i])
                {
                    var at = players.FindIndex(p => p.Uid == uid);
                    if (at < 0)
                    {
                        players.Add((uid, []));
                        at = players.Count - 1;
                    }
                    var names = players[at].Names;
                    var known = names.FindIndex(n => n.Name == name);
                    if (known < 0)
                    {
                        names.Add(new RaceName(name, time.AddSeconds(-40), time));
                    }
                    else
                    {
                        names[known] = names[known] with { Last = time };
                    }
                }
                matches.Add(new RaceMatch(time, $"{1000 + i}_{i}_0_12_{start.ToUnixTimeSeconds() & 0xffff:x}", [.. rounds[i].Select(r => new RaceMember(r.Uid, r.Name))]));
            }
            return new RaceSession(Id(start), start, start.AddMinutes(4 + (rounds.Length * 5)), backend, server, matches,
                [.. players.Select(p => new RacePlayer(p.Uid, p.Uid == DemoOwnUid, p.Names))]);
        }

        (string, string) me = (DemoOwnUid, "Alex");
        (string, string) luna = (Uid(11), "Luna"), mateus = (Uid(12), "Mateus"), rin = (Uid(13), "Rin"), sofia = (Uid(14), "Sofia"), kai = (Uid(15), "Kai");
        var tonight = now.AddHours(-2);
        var yesterday = now.AddDays(-1).AddHours(-3);
        var lastWeek = now.AddDays(-6);
        return
        [
            // tonight on Nextendo: the same Player ID is "TurboShell", then "xX_Drift_Xx"; Sofia becomes "Sofi"
            Session(tonight, RaceSession.Nextendo, "nextendo.network",
                [me, luna, mateus, sofia, (DemoRenamedPlayer, "TurboShell")],
                [me, luna, mateus, sofia, (DemoRenamedPlayer, "TurboShell"), rin],
                [me, luna, (sofia.Item1, "Sofi"), (DemoRenamedPlayer, "xX_Drift_Xx"), rin],
                [me, luna, (sofia.Item1, "Sofi"), (DemoRenamedPlayer, "xX_Drift_Xx"), rin, kai]),
            // yesterday on Nextendo: that id again, under a third name
            Session(yesterday, RaceSession.Nextendo, "nextendo.network",
                [me, mateus, kai, (DemoRenamedPlayer, "NotTurbo")],
                [me, mateus, kai, (DemoRenamedPlayer, "NotTurbo"), luna]),
            // last week on an MKWMP server: nobody renamed (the server gives the name there)
            Session(lastWeek, RaceSession.Mkwmp, "mkw.example.net",
                [me, rin, kai, luna],
                [me, rin, kai, luna, mateus],
                [me, rin, luna, mateus]),
        ];
    }
}

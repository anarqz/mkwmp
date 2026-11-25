// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core.Tests;

/// <summary>How the fake seat server answers a join.</summary>
public enum SeatMood
{
    Seated,

    /// <summary>Queued until <see cref="FakeSeatServer.Count"/> polls, then seated.</summary>
    QueuedFor,
    Refused,
    Down,

    /// <summary>409 NAME_TAKEN for <see cref="FakeSeatServer.Count"/> joins, then seated.</summary>
    TakenFor,

    /// <summary>The server deleted the profile (a month unused): 404 until the launcher registers it again, then seated.</summary>
    Expired,
}

/// <summary>A seat server inside the test (docs/contracts/SEAT_API.md, the parts the launcher calls): no socket, no real server.</summary>
internal sealed class FakeSeatServer : HttpMessageHandler
{
    public const string Url = "http://seat.test";

    private readonly Lock _gate = new();

    public SeatMood Mood { get; init; } = SeatMood.Seated;
    public int Count { get; init; }
    public (int Status, string Code) RefuseWith { get; init; } = (401, "PIN_WRONG");
    public (int Status, string Code) Delete { get; init; } = (204, "");

    /// <summary>A server older than restore: ignores <c>pin</c>, answers this one.</summary>
    public string? PicksPin { get; init; }

    /// <summary>GET /seat/v1/profiles/{name} says the name is someone's.</summary>
    public bool TakenName { get; init; }

    /// <summary>The whole network is gone.</summary>
    public bool Unreachable { get; init; }

    /// <summary>The server's own privacy policy (<c>GET /privacy</c>); null: a server older than server-side consent (no policy, no <c>privacyAccepted</c>, no <c>PUT /seat/v1/privacy</c>).</summary>
    public string? PrivacyText { get; set; }

    /// <summary>What a server with <see cref="PrivacyText"/> holds of the profile's consent.</summary>
    public bool PrivacyAccepted { get; set; }

    /// <summary><c>[privacy] enforce</c>: seats, friends and reports answer 403 PRIVACY_REQUIRED until accepted.</summary>
    public bool PrivacyEnforced { get; init; } = true;

    /// <summary>The policy's version: the lower-case hex SHA-256 of its bytes (the quoted ETag of <c>GET /privacy</c>).</summary>
    public string PrivacyVersion => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(PrivacyText ?? "")));

    private HttpResponseMessage PrivacyError(int status, string code) =>
        Json(status, JsonSerializer.Serialize(new { error = new { code, message = $"{code} from the fake", privacyVersion = PrivacyVersion } }));

    /// <summary>The bodies of <c>PUT /seat/v1/privacy</c>.</summary>
    public List<string> PrivacyPuts { get; } = [];

    /// <summary><c>PUT /seat/v1/privacy</c> answers this status instead of storing (0: stores).</summary>
    public int PrivacyPutFails { get; set; }

    public JsonObject FriendsBody { get; set; } = new() { ["cursor"] = 1, ["friends"] = new JsonArray(), ["incoming"] = new JsonArray(), ["outgoing"] = new JsonArray() };

    public int Joins { get; private set; }
    public int Polls { get; private set; }
    public List<string> Deletes { get; } = [];
    public List<string> Registers { get; } = [];
    public List<string> Requests { get; } = [];

    private static HttpResponseMessage Json(int status, string json) =>
        new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(int status, string code) =>
        Json(status, JsonSerializer.Serialize(new { error = new { code, message = $"{code} from the fake" } }));

    /// <summary>
    /// The view's <c>room</c> (SEAT_API.md 2, 1.4.0) as raw JSON: null leaves the key out (a server older than rooms),
    /// <c>"null"</c> is in no room, an object is the room.
    /// </summary>
    public string? RoomJson { get; set; }

    private HttpResponseMessage View(string state, int? position)
    {
        object? ticket = state == "seated" ? new { token = "mkw1.ticket.sig", expiresAt = 9_999_999_999L, lobby = "lobby.test:8905" } : null;
        var view = JsonSerializer.SerializeToNode(new
        {
            bootId = "b1", sessionId = "s1", name = "Ana", sessionKey = "k1", state, position, seatsTotal = 2, seatsFree = 0, nextPollMs = 1000, ticket,
        })!.AsObject();
        if (RoomJson is { } room)
        {
            view["room"] = JsonNode.Parse(room);
        }
        return Json(200, view.ToJsonString());
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unreachable)
        {
            throw new HttpRequestException("connection refused (the fake is unplugged)");
        }
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (method == "GET" && path == "/seat/v1/friends" && request.RequestUri.Query == $"?since={FriendsBody["cursor"]}")
        {
            // The long-poll: nothing changed since that cursor, so the answer waits (the launcher cuts it short).
            await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
        }
        lock (_gate)
        {
            Requests.Add($"{method} {path}");
            if (PrivacyText is not null)
            {
                switch (method, path)
                {
                    // SEAT_API.md 2c, as the 1.4.0 server answers
                    case ("GET", "/privacy"):
                        var policy = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PrivacyText, Encoding.UTF8, "text/markdown") };
                        policy.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{PrivacyVersion}\"");
                        return policy;
                    case ("GET", "/ping"):
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case ("PUT", "/seat/v1/privacy"):
                        if (PrivacyPutFails != 0)
                        {
                            return Error(PrivacyPutFails, "REGISTRY_ERROR");
                        }
                        PrivacyPuts.Add(body);
                        var put = JsonNode.Parse(body)!;
                        var accepted = put["accepted"]!.GetValue<bool>();
                        if (accepted && put["version"]?.GetValue<string>() != PrivacyVersion)
                        {
                            return PrivacyError(409, "PRIVACY_VERSION");
                        }
                        PrivacyAccepted = accepted;
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case ("POST", "/seat/v1/presence") when Mood == SeatMood.Seated:
                        return Json(200, JsonSerializer.Serialize(new { token = "p1", name = "Ana", ttlMs = 90000, privacyAccepted = PrivacyAccepted, privacyVersion = PrivacyVersion, privacyEnforced = PrivacyEnforced }));
                    case ("POST", "/seat/v1/sessions") or ("GET", "/seat/v1/friends") or ("POST", "/seat/v1/friends") or ("GET", "/seat/v1/reports") or ("POST", "/seat/v1/reports")
                        when PrivacyEnforced && !PrivacyAccepted:
                        return PrivacyError(403, "PRIVACY_REQUIRED");
                    default:
                        break;
                }
            }
            switch (method, path)
            {
                case ("GET", "/seat/v1/status"):
                    return Json(200, """{"name":"fake","seatsTotal":2,"seated":0,"queued":0,"lobbyEndpoint":"lobby.test:8905"}""");
                case ("POST", "/seat/v1/sessions"):
                    Joins++;
                    return Mood switch
                    {
                        SeatMood.TakenFor when Joins <= Count => Error(409, "NAME_TAKEN"),
                        SeatMood.QueuedFor => View("queued", 2),
                        SeatMood.Refused => Error(RefuseWith.Status, RefuseWith.Code),
                        SeatMood.Down => Error(503, "UNAVAILABLE"),
                        _ => View("seated", null),
                    };
                case ("POST", "/seat/v1/presence"):
                    return Mood switch
                    {
                        SeatMood.Expired when Registers.Count == 0 => Error(404, "PROFILE_NOT_FOUND"),
                        SeatMood.Refused => Error(RefuseWith.Status, RefuseWith.Code),
                        SeatMood.Down => Error(503, "UNAVAILABLE"),
                        _ => Json(200, """{"token":"p1"}"""),
                    };
                case ("DELETE", "/seat/v1/presence"):
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case ("POST", "/seat/v1/profiles"):
                    Registers.Add(body);
                    var asked = JsonNode.Parse(body)!;
                    var pin = PicksPin ?? asked["pin"]?.GetValue<string>() ?? "654321";
                    return Json(201, JsonSerializer.Serialize(new { name = asked["name"]!.GetValue<string>(), pin }));
                case ("POST", "/seat/v1/friends"):
                    return Json(200, JsonSerializer.Serialize(new { name = JsonNode.Parse(body)!["name"]!.GetValue<string>(), state = "pending" }));
                case ("GET", "/seat/v1/friends"):
                    return Json(200, FriendsBody.ToJsonString());
                case ("GET", "/seat/v1/reports"):
                    return Json(200, """{"report":null}""");
                default:
                    break;
            }
            if (path.StartsWith("/seat/v1/sessions/", StringComparison.Ordinal))
            {
                if (method == "DELETE")
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                Polls++;
                return Mood == SeatMood.QueuedFor && Polls < Count ? View("queued", 1) : View("seated", null);
            }
            if (method == "GET" && path.StartsWith("/seat/v1/profiles/", StringComparison.Ordinal))
            {
                return Json(200, JsonSerializer.Serialize(new { registered = Registers.Count > 0 || TakenName, reserved = false }));
            }
            if (method == "DELETE" && path.StartsWith("/seat/v1/profiles/", StringComparison.Ordinal))
            {
                Deletes.Add(path["/seat/v1/profiles/".Length..]);
                return Delete.Status == 204 ? new HttpResponseMessage(HttpStatusCode.NoContent) : Error(Delete.Status, Delete.Code);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}

/// <summary>A fresh folder with a game (mkw.exe, an old mkwmp.toml with a ticket, an installed patch) and a launcher whose "game" is a command that leaves <c>started</c> behind.</summary>
internal sealed class Rig : IAsyncDisposable
{
    /// <summary>Stand-ins for a supported mkw.exe, and for one that loads mods.</summary>
    public static readonly byte[] TestExe = "mkwmp test mkw.exe"u8.ToArray();
    public static readonly byte[] TestModExe = "mkwmp test mkw.exe with mods"u8.ToArray();

    /// <summary>The release <see cref="TestExe"/> stands for (no mods: the winmm.dll patch), and <see cref="TestModExe"/>'s.</summary>
    public const string LegacyVersion = "0.1.10", LegacyModVersion = "0.1.15";

    /// <summary>The pin, without the earlier releases it updates in place: the fixtures' runtimes are not updates here.</summary>
    public static RuntimeManifest PinWithoutUpdates => RecompRuntime.Pinned with { UpdatesFrom = [] };

    static Rig()
    {
        // MKWMP runs on 0.1.18 only; these older games still exercise the winmm.dll patch and the mod on an earlier runtime.
        Recomp.TestSupported[LegacyVersion] = false;
        Recomp.TestSupported[LegacyModVersion] = true;
        Recomp.TestExes[Updater.Sha256Hex(TestExe)] = LegacyVersion;
        Recomp.TestExes[Updater.Sha256Hex(TestModExe)] = LegacyModVersion;
    }

    /// <param name="executable">What the launcher takes for its own executable (null: none, as under a test host).</param>
    public Rig(FakeSeatServer server, Func<LauncherSettings, LauncherSettings>? edit = null, bool installPatch = true, string? executable = null)
    {
        Server = server;
        Root = NewFolder("core");
        Game = Path.Combine(Root, "game");
        var home = Path.Combine(Root, "launcher");
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(home);
        File.WriteAllBytes(Path.Combine(Game, "mkw.exe"), TestExe);
        File.WriteAllText(Path.Combine(Game, "mkwmp.toml"), "lobby = \"old:8905\"\nticket = \"mkw1.old\"\nvoice_port = 8907\n");
        if (installPatch)
        {
            // An installed game patch (no expected hashes in tests: "not verified").
            Probe.InstallProbe(Game, "probe"u8, false, out _);
            Probe.InstallFile(Game, Probe.Voice, "voice"u8, false);
            File.WriteAllBytes(Path.Combine(Game, Probe.Host), FakeWinmm());
        }
        var settings = new LauncherSettings
        {
            Server = FakeSeatServer.Url,
            Name = "Ana",
            Pin = "123456",
            Game = Game,
            AutoUpdate = false,
            LaunchCommand = "echo up > started",
            AdultConsent = true,
        };
        ConfigPath = Path.Combine(Root, "client.toml");
        new ClientConfig { Settings = edit?.Invoke(settings) ?? settings, Mutes = [new MutedPlayer("42", "Bia")] }.Save(ConfigPath);
        Target = new MkwRecompTarget("1.4.0") { RunningOverride = () => false };
        Launcher = new Launcher(ConfigPath, home, new LauncherOptions
        {
            Version = "1.4.0",
            Executable = executable,
            Targets = [Target],
            Runtime = PinWithoutUpdates,
            BindLocalApi = false, // as if the port were bound
            BackgroundLoops = false,
            Tick = TimeSpan.FromMilliseconds(40),
            BusyRetry = TimeSpan.FromMilliseconds(400),
            UpdateApi = "http://github.test",
            Exit = _ => { },
        }, server);
    }

    public FakeSeatServer Server { get; }
    public string Root { get; }
    public string Game { get; }
    public string ConfigPath { get; }
    public MkwRecompTarget Target { get; }
    public Launcher Launcher { get; }

    public bool Started => File.Exists(Path.Combine(Game, "started"));

    public string? Ticket => File.ReadAllLines(Path.Combine(Game, "mkwmp.toml")).FirstOrDefault(l => l.StartsWith("ticket", StringComparison.Ordinal));

    public static string NewFolder(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mkwmp-core-tests", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task Until(string what, Func<bool> done, int seconds = 10)
    {
        for (var i = 0; i < seconds * 40; i++)
        {
            if (done())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    public async Task<SessionView> SettleSession()
    {
        await Until("the session to end", () => !Launcher.Session.Busy);
        return Launcher.Session;
    }

    public void EnableMod()
    {
        File.WriteAllText(Path.Combine(Game, "mkw.ini"), "[launcher]\nmods = mkwmp\n");
        Directory.CreateDirectory(Path.Combine(Game, "mods", "mkwmp", "plugin"));
        File.WriteAllBytes(Path.Combine(Game, "mods", "mkwmp", "plugin", "mkwmp.dll"), "mod"u8);
    }

    /// <summary>A minimal PE32+ with one section holding an export table naming two functions.</summary>
    public static byte[] FakeWinmm()
    {
        var b = new byte[0x400];
        void Put(int at, uint v) => BitConverter.GetBytes(v).CopyTo(b, at);
        void Put16(int at, ushort v) => BitConverter.GetBytes(v).CopyTo(b, at);
        "MZ"u8.CopyTo(b);
        Put(0x3c, 0x80);
        "PE\0\0"u8.CopyTo(b.AsSpan(0x80));
        const int Coff = 0x84;
        Put16(Coff + 2, 1);
        Put16(Coff + 16, 240);
        const int Opt = Coff + 20;
        Put16(Opt, 0x20b);
        Put(Opt + 112, 0x1000); // export dir RVA
        const int Section = Opt + 240;
        Put(Section + 8, 0x200);
        Put(Section + 12, 0x1000);
        Put(Section + 16, 0x200);
        Put(Section + 20, 0x200);
        const int Exports = 0x200; // file offset of RVA 0x1000
        Put(Exports + 24, 2);
        Put(Exports + 32, 0x1040); // name pointers
        Put(0x240, 0x1080);
        Put(0x244, 0x1090);
        "timeBeginPeriod"u8.CopyTo(b.AsSpan(0x280));
        "timeEndPeriod"u8.CopyTo(b.AsSpan(0x290));
        return b;
    }

    public async ValueTask DisposeAsync()
    {
        await Launcher.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // a "game" still writing: the temp folder is cleaned by the system
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

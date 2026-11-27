// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net;
using System.Net.Http.Headers;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Tests;

/// <summary>
/// Profile pictures on Nextendo Network (Net/NextendoAvatars.cs, Launcher.NextendoAvatars.cs): what the friends list
/// says of someone's look, where a picture is asked (the public route, without the sign-in), when (missing, changed,
/// old: never at every poll), what is kept on disk and for how long, what is refused as a picture, and that nobody's
/// pid or name reaches a file name or a log. Nothing here leaves the test.
/// </summary>
public sealed class NextendoAvatarsTests
{
    private const long Self = 1234567;

    /// <summary>The start of a JPEG and of a PNG, padded: the cache looks at the first bytes only (the window decodes).</summary>
    private static byte[] Jpeg(byte fill, int size = 64) => [0xFF, 0xD8, 0xFF, 0xE0, .. Enumerable.Repeat(fill, size - 4)];

    private static byte[] Png(byte fill, int size = 64) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Repeat(fill, size - 8)];

    private static HttpResponseMessage Picture(byte[] bytes, string type = "image/jpeg")
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        res.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return res;
    }

    private static NextendoAvatarWant Friend(long pid, string key = "k1") => new(pid, key, NextendoAvatars.Fresh);

    /// <summary>A cache over a fresh folder, a fake Nextendo that serves pictures, and a clock the test moves.</summary>
    private sealed class Wired : IDisposable
    {
        private readonly HttpClient _http;

        public Wired(string? folder = null, long most = NextendoAvatars.MaxTotal)
        {
            Folder = folder ?? Path.Combine(Rig.NewFolder("avatars"), "cache", NextendoAvatars.FolderName);
            _http = new HttpClient(Server, disposeHandler: false);
            Avatars = new NextendoAvatars(_http, "mkwmp-test/1", Folder, Clock, Notes.Add) { MostBytes = most };
            Avatars.Changed += () => Changes++;
            Server.Avatar = pid => Pictures.TryGetValue(pid, out var bytes) ? Picture(bytes) : null;
        }

        public string Folder { get; }
        public FakeNextendo Server { get; } = new();
        public ManualClock Clock { get; } = new();
        public NextendoAvatars Avatars { get; }
        public Dictionary<long, byte[]> Pictures { get; } = [];
        public List<string> Notes { get; } = [];
        public int Changes { get; private set; }
        public int Asked => Server.AvatarsAsked.Count;

        public string[] Files => Directory.Exists(Folder) ? [.. Directory.GetFiles(Folder).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)] : [];

        /// <summary>What the 30 s poll does: says who is wanted, then fetches what is due.</summary>
        public async Task<int> PollAsync(params NextendoAvatarWant[] wants)
        {
            Avatars.Want(wants);
            return await Avatars.DrainAsync(TimeSpan.Zero, CancellationToken.None);
        }

        public void Dispose()
        {
            Avatars.Dispose();
            _http.Dispose();
        }
    }

    // ---- what the friends list says of a look ---------------------------------------------

    [Theory]
    [InlineData("#3366FF", "#3366ff")]
    [InlineData("#f0a", "#ff00aa")]
    [InlineData(" #000000 ", "#000000")]
    [InlineData("3366ff", "")]
    [InlineData("#3366f", "")]
    [InlineData("#33 6ff", "")]
    [InlineData("red", "")]
    [InlineData("#3366ff;background:url(x)", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_colour_is_a_hex_colour_or_nothing(string? said, string color)
    {
        Assert.Equal(color, NextendoLook.HexColor(said));
    }

    [Fact]
    public void The_list_says_each_one_s_colour_and_when_their_picture_changed()
    {
        const string List = """
            {"friends":[
              {"pid":11,"username":"luna","name":"Luna","friend_code":"SW-2071-4456-9902","image":"","avatar":"AVATAR","color":"#F00","presence":{"status":1}},
              {"pid":12,"username":"kai","friend_code":"SW-5530-1187-2264"},
              {"pid":13,"username":"rin","avatar":"{\"bg\":\"bg/a.png\",\"char\":\"c/b.png\"}","color":"javascript:alert(1)"}],
             "requests":[{"pid":21,"username":"nico","avatar":"img:x/y.png","color":"#00ff00"},22],
             "sent":[{"pid":31,"username":"carol","color":"#0000ff"},{"pid":11,"username":"luna","color":"#123456"}]}
            """;
        var list = NextendoFriendList.Parse(Json.Parse(List.Replace("AVATAR", "icon-7", StringComparison.Ordinal)))!;
        Assert.Equal([11L, 12, 13, 21, 31], list.Looks.Keys.Order());
        Assert.Equal(["#ff0000", "", "", "#00ff00", "#0000ff"], new[] { 11L, 12, 13, 21, 31 }.Select(pid => list.Looks[pid].Color));
        Assert.All(list.Looks.Values, look => Assert.Matches("^[0-9a-f]{16}$", look.Key)); // a key is a file name's part: never Nextendo's own text
        Assert.Equal(5, list.Looks.Values.Select(l => l.Key).Distinct().Count());

        // who is in the lists is what it was: the look is beside them, not part of them
        Assert.Equal(new NextendoFriend(11, "Luna", "SW-2071-4456-9902", FriendState.Online, "", "", false), list.Friends[0]);
        Assert.Equal(new NextendoPerson(21, "nico", ""), list.Incoming[0]);

        // the same list again: the same keys; another icon, or another colour: another key, for that one only
        var again = NextendoFriendList.Parse(Json.Parse(List.Replace("AVATAR", "icon-7", StringComparison.Ordinal)))!;
        Assert.Equal(list.Looks[11], again.Looks[11]);
        var other = NextendoFriendList.Parse(Json.Parse(List.Replace("AVATAR", "icon-8", StringComparison.Ordinal)))!;
        Assert.NotEqual(list.Looks[11].Key, other.Looks[11].Key);
        Assert.Equal(list.Looks[13], other.Looks[13]);
        var painted = NextendoFriendList.Parse(Json.Parse(List.Replace("AVATAR", "icon-7", StringComparison.Ordinal).Replace("#F00", "#0F0", StringComparison.Ordinal)))!;
        Assert.NotEqual(list.Looks[11].Key, painted.Looks[11].Key);
    }

    // ---- where a picture is asked ---------------------------------------------------------

    [Fact]
    public void A_picture_is_asked_at_nextendo_s_public_route_and_named_by_no_pid()
    {
        Assert.Equal("https://nextendo.network/api/avatar?pid=1800003803", NextendoAvatars.Url(1800003803));
        var id = NextendoAvatars.Id(1800003803);
        Assert.Matches("^[0-9a-f]{20}$", id);
        Assert.DoesNotContain("1800003803", id, StringComparison.Ordinal);
        Assert.NotEqual(id, NextendoAvatars.Id(1800003804));
        Assert.Equal(id, NextendoAvatars.Id(1800003803));
    }

    [Fact]
    public void Only_a_jpeg_or_a_png_is_a_picture()
    {
        Assert.True(NextendoAvatars.IsPicture(Jpeg(1)));
        Assert.True(NextendoAvatars.IsPicture(Png(1)));
        Assert.False(NextendoAvatars.IsPicture("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8));
        Assert.False(NextendoAvatars.IsPicture("<!DOCTYPE html><html><body>sign in</body></html>"u8));
        Assert.False(NextendoAvatars.IsPicture("RIFF\0\0\0\0WEBPVP8 ........"u8)); // WebP: the window cannot decode it
        Assert.False(NextendoAvatars.IsPicture("GIF89a.................."u8));
        Assert.False(NextendoAvatars.IsPicture("MZ\u0090\0\u0003\0\0\0\u0004\0\0\0ÿÿ\0\0"u8));
        Assert.False(NextendoAvatars.IsPicture([0xFF, 0xD8, 0xFF]));
        Assert.False(NextendoAvatars.IsPicture([]));
    }

    // ---- when it is asked -----------------------------------------------------------------

    [Fact]
    public async Task A_picture_is_fetched_once_and_not_again_at_every_poll()
    {
        using var wired = new Wired();
        wired.Pictures[11] = Jpeg(1);
        wired.Pictures[12] = Png(2);
        Assert.Null(wired.Avatars.PathOf(11));

        Assert.Equal(2, await wired.PollAsync(Friend(11), Friend(12)));
        var luna = wired.Avatars.PathOf(11)!;
        Assert.Equal(Jpeg(1), File.ReadAllBytes(luna));
        Assert.Equal(Png(2), File.ReadAllBytes(wired.Avatars.PathOf(12)!));
        Assert.Equal(2, wired.Changes);
        Assert.Equal((2, 128L), wired.Avatars.Held);

        // the friends list is asked every 30 s: for a day that asks for no picture
        for (var i = 0; i < 100; i++)
        {
            wired.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(0, await wired.PollAsync(Friend(11), Friend(12)));
        }
        Assert.Equal(2, wired.Asked);
        wired.Clock.Advance(NextendoAvatars.Fresh - TimeSpan.FromSeconds(3001));
        Assert.Equal(0, await wired.PollAsync(Friend(11), Friend(12)));

        // a day old: asked again, once each; the same picture is the same file, and the window is told nothing
        wired.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, await wired.PollAsync(Friend(11), Friend(12)));
        Assert.Equal(0, await wired.PollAsync(Friend(11), Friend(12)));
        Assert.Equal((luna, 2, 4), (wired.Avatars.PathOf(11), wired.Changes, wired.Asked));
        Assert.Equal(2, wired.Files.Length);
    }

    [Fact]
    public async Task A_changed_look_fetches_that_one_picture_at_once()
    {
        using var wired = new Wired();
        wired.Pictures[11] = Jpeg(1);
        wired.Pictures[12] = Jpeg(2);
        await wired.PollAsync(Friend(11), Friend(12));
        var (luna, kai) = (wired.Avatars.PathOf(11)!, wired.Avatars.PathOf(12)!);

        // Luna chose another picture: her key is another one
        wired.Pictures[11] = Jpeg(9);
        wired.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await wired.PollAsync(Friend(11, "k2"), Friend(12)));
        Assert.Equal([11L, 12, 11], wired.Server.AvatarsAsked);
        var now = wired.Avatars.PathOf(11)!;
        Assert.NotEqual(luna, now); // another file: what the window decoded of the old one is not shown for the new
        Assert.False(File.Exists(luna));
        Assert.Equal(Jpeg(9), File.ReadAllBytes(now));
        Assert.Equal(kai, wired.Avatars.PathOf(12));
        Assert.Equal(3, wired.Changes);
        Assert.Equal(0, await wired.PollAsync(Friend(11, "k2"), Friend(12)));

        // someone whose look is not known (the player, a player met) keeps the picture in hand until it is old
        Assert.Equal(0, await wired.PollAsync(new NextendoAvatarWant(11, NextendoAvatars.NoKey, NextendoAvatars.Fresh)));
        Assert.Equal(0, await wired.PollAsync(Friend(11, "k2")));
    }

    [Fact]
    public async Task The_player_s_own_picture_is_asked_again_after_an_hour()
    {
        using var wired = new Wired();
        wired.Pictures[Self] = Jpeg(1);
        var own = new NextendoAvatarWant(Self, NextendoAvatars.NoKey, NextendoAvatars.OwnFresh);
        Assert.Equal(1, await wired.PollAsync(own));
        wired.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, await wired.PollAsync(own));
        wired.Pictures[Self] = Jpeg(2);
        wired.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await wired.PollAsync(own));
        Assert.Equal(Jpeg(2), File.ReadAllBytes(wired.Avatars.PathOf(Self)!));
        Assert.Equal((2, 1), (wired.Changes, wired.Files.Length));
    }

    [Fact]
    public async Task Nobody_is_asked_for_twice_and_only_who_is_wanted()
    {
        using var wired = new Wired();
        wired.Pictures[11] = Jpeg(1);
        Assert.Equal(1, await wired.PollAsync(Friend(11), Friend(11, "other"), Friend(0), Friend(-5)));
        Assert.Equal([11L], wired.Server.AvatarsAsked);
        Assert.Equal(0, await wired.Avatars.DrainAsync(TimeSpan.Zero, CancellationToken.None));
        // nobody wanted (PLAY went to MKWMP servers): nothing is asked, what is here stays for the next time
        wired.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, await wired.PollAsync());
        Assert.NotNull(wired.Avatars.PathOf(11));
    }

    // ---- what is no picture ---------------------------------------------------------------

    [Fact]
    public async Task No_picture_is_remembered_for_a_day_and_the_disc_keeps_its_initials()
    {
        using var wired = new Wired();
        wired.Server.Avatar = pid => pid switch
        {
            11 => null, // 404
            12 => Picture("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray(), "image/svg+xml"),
            13 => Picture("<html>sign in</html>"u8.ToArray(), "image/jpeg"), // called a JPEG, and is none
            14 => Picture(Jpeg(1, NextendoAvatars.MaxBytes + 1)),
            15 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.example/a.jpg") } },
            16 => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => Picture(Jpeg(7, NextendoAvatars.MaxBytes)),
        };
        NextendoAvatarWant[] all = [Friend(11), Friend(12), Friend(13), Friend(14), Friend(15), Friend(16), Friend(17)];
        Assert.Equal(7, await wired.PollAsync(all));
        Assert.All(new long[] { 11, 12, 13, 14, 15, 16 }, pid => Assert.Null(wired.Avatars.PathOf(pid)));
        Assert.Equal(NextendoAvatars.MaxBytes, new FileInfo(wired.Avatars.PathOf(17)!).Length); // 2 MB is the most
        Assert.Equal(1, wired.Changes);
        Assert.Equal((1, (long)NextendoAvatars.MaxBytes), wired.Avatars.Held);
        Assert.All(wired.Files, name => Assert.EndsWith(".img", name, StringComparison.Ordinal)); // never a name a shell would run or a browser open
        Assert.Empty(wired.Notes);

        wired.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, await wired.PollAsync(all));
        // a day later they are asked again (someone may have chosen a picture), and one who did has it
        wired.Server.Avatar = pid => pid == 11 ? Picture(Png(3)) : null;
        wired.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(7, await wired.PollAsync(all));
        Assert.Equal(Png(3), File.ReadAllBytes(wired.Avatars.PathOf(11)!));
        Assert.Null(wired.Avatars.PathOf(17)); // and one who took theirs away has none
        Assert.Equal(7, wired.Files.Length);
    }

    // ---- the pace -------------------------------------------------------------------------

    [Fact]
    public async Task A_429_stops_the_asking_until_its_retry_after_is_over()
    {
        using var wired = new Wired();
        var limited = true;
        wired.Server.Avatar = _ =>
        {
            if (!limited)
            {
                return Picture(Jpeg(1));
            }
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return res;
        };
        NextendoAvatarWant[] all = [Friend(11), Friend(12), Friend(13)];
        Assert.Equal(1, await wired.PollAsync(all)); // the first answer says to wait: the others are not asked
        limited = false;
        wired.Clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(0, await wired.PollAsync(all));
        Assert.Equal(1, wired.Asked);
        wired.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, await wired.PollAsync(all));
        Assert.Equal((3, 3), (wired.Avatars.Held.Count, wired.Changes));
        Assert.Empty(wired.Notes);
    }

    [Fact]
    public async Task A_failure_waits_longer_and_longer_keeps_what_is_shown_and_says_so_once_without_naming_anyone()
    {
        using var wired = new Wired();
        wired.Pictures[1800000011] = Jpeg(1);
        await wired.PollAsync(Friend(1800000011));
        var shown = wired.Avatars.PathOf(1800000011);

        wired.Server.Avatar = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        NextendoAvatarWant[] all = [Friend(1800000011, "k2"), Friend(1800000012)];
        Assert.Equal(1, await wired.PollAsync(all));
        Assert.Equal(0, await wired.PollAsync(all));
        wired.Clock.Advance(NextendoRoster.Backoff(1));
        Assert.Equal(1, await wired.PollAsync(all)); // one try, then a longer wait
        wired.Clock.Advance(NextendoRoster.Backoff(1));
        Assert.Equal(0, await wired.PollAsync(all));
        wired.Server.Unreachable = true;
        wired.Clock.Advance(NextendoRoster.Backoff(2));
        Assert.Equal(1, await wired.PollAsync(all));
        Assert.Equal(shown, wired.Avatars.PathOf(1800000011)); // the old picture stays while the new one does not come
        Assert.True(File.Exists(shown));

        var note = Assert.Single(wired.Notes); // once, not at every try
        Assert.Contains("HTTP 502", note, StringComparison.Ordinal);
        Assert.DoesNotContain("18000000", note, StringComparison.Ordinal);
        Assert.DoesNotContain("avatar?", note, StringComparison.Ordinal);

        // it answers again: both come
        (wired.Server.Unreachable, wired.Server.Avatar) = (false, _ => Picture(Jpeg(5)));
        wired.Clock.Advance(NextendoRoster.Backoff(3));
        Assert.Equal(2, await wired.PollAsync(all));
        Assert.Equal(Jpeg(5), File.ReadAllBytes(wired.Avatars.PathOf(1800000011)!));
        Assert.Single(wired.Notes);
    }

    // ---- what is kept on disk -------------------------------------------------------------

    [Fact]
    public async Task The_least_recently_wanted_pictures_go_when_all_take_too_much()
    {
        using var wired = new Wired(most: 250);
        wired.Server.Avatar = pid => Picture(Jpeg((byte)pid, 100));
        await wired.PollAsync(Friend(11));
        wired.Clock.Advance(TimeSpan.FromMinutes(1));
        await wired.PollAsync(Friend(11), Friend(12));
        wired.Clock.Advance(TimeSpan.FromMinutes(1));
        // 11 is no longer in the lists; 13 does not fit beside 11 and 12: 11, wanted the longest ago, goes
        await wired.PollAsync(Friend(12), Friend(13));
        Assert.Null(wired.Avatars.PathOf(11));
        Assert.NotNull(wired.Avatars.PathOf(12));
        Assert.NotNull(wired.Avatars.PathOf(13));
        Assert.Equal((2, 200L), wired.Avatars.Held);
        Assert.Equal(2, wired.Files.Length);
    }

    [Fact]
    public async Task The_pictures_are_there_at_the_next_start_and_the_folder_is_tidied()
    {
        string folder;
        string luna;
        using (var first = new Wired())
        {
            folder = first.Folder;
            first.Pictures[11] = Jpeg(1);
            first.Pictures[13] = Jpeg(3);
            Assert.Equal(3, await first.PollAsync(Friend(11), Friend(12), Friend(13)));
            luna = first.Avatars.PathOf(11)!;
            // no pid and no name in a file's name: who the friends are is not read off the folder
            Assert.All(first.Files, name => Assert.Matches("^[0-9a-f]{20}-[0-9a-z]{1,16}-([0-9a-f]{12}|none)\\.img$", name));
        }
        // what is not the cache's: a picture too large, a file half written, a second picture of the same person, someone else's file
        File.WriteAllBytes(Path.Combine(folder, NextendoAvatars.Id(14) + "-k1-0123456789ab.img"), new byte[NextendoAvatars.MaxBytes + 1]);
        File.WriteAllBytes(luna + ".tmp", [1, 2, 3]);
        var twin = Path.Combine(folder, NextendoAvatars.Id(11) + "-k0-aaaaaaaaaaaa.img");
        File.WriteAllBytes(twin, Jpeg(8));
        File.SetLastWriteTimeUtc(twin, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "the player's own");
        // and a picture nobody wanted for a month
        var old = Directory.GetFiles(folder).Single(f => Path.GetFileName(f).StartsWith(NextendoAvatars.Id(13), StringComparison.Ordinal));
        File.SetLastAccessTimeUtc(old, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        using var second = new Wired(folder);
        second.Pictures[11] = Jpeg(1);
        Assert.Equal(luna, second.Avatars.PathOf(11));
        Assert.Null(second.Avatars.PathOf(12)); // "no picture" is remembered too
        Assert.Null(second.Avatars.PathOf(13));
        Assert.Null(second.Avatars.PathOf(14));
        Assert.Equal(0, await second.PollAsync(Friend(11), Friend(12))); // nothing is asked again at a start
        Assert.Equal(2, second.Files.Count(n => n.EndsWith(".img", StringComparison.Ordinal)));
        Assert.Equal(["notes.txt"], second.Files.Where(n => !n.EndsWith(".img", StringComparison.Ordinal)));
        // a day after it was fetched it is due, restart or not
        second.Clock.Advance(NextendoAvatars.Fresh);
        Assert.Equal(2, await second.PollAsync(Friend(11), Friend(12)));
    }

    [Fact]
    public async Task Unlinking_leaves_nobody_s_picture_on_the_pc()
    {
        using var wired = new Wired();
        wired.Pictures[11] = Jpeg(1);
        await wired.PollAsync(Friend(11), Friend(12));
        Assert.Equal(2, wired.Files.Length);
        wired.Avatars.Clear();
        Assert.Empty(wired.Files);
        Assert.Null(wired.Avatars.PathOf(11));
        Assert.Equal(0, await wired.Avatars.DrainAsync(TimeSpan.Zero, CancellationToken.None)); // and nobody is wanted any more
    }

    // ---- the launcher ---------------------------------------------------------------------

    /// <summary>A launcher with a linked Nextendo account (bia, pid 1234567) that plays on Nextendo, or on MKWMP servers; its fake Nextendo serves pictures.</summary>
    private sealed class AvatarRig : IAsyncDisposable
    {
        private readonly Rig _rig;

        public AvatarRig(string playOn = "nextendo")
        {
            People = new FakePeople(Nextendo);
            Nextendo.Avatar = pid => Pictures.TryGetValue(pid, out var bytes) ? Picture(bytes) : null;
            _rig = new Rig(Seat, s => s with { Launch = false }, http: new TwoNetworks(Seat, Nextendo),
                config: c => c with { Nextendo = NextendoLink.Of(playOn, Self, "bia", "SW-1111-2222-3333") },
                prepare: path => Assert.True(NextendoVault.Beside(path).Save("refresh-0")));
            Launcher.Changed += (_, _) => Interlocked.Increment(ref _changes);
        }

        private int _changes;

        public FakeSeatServer Seat { get; } = new();
        public FakeNextendo Nextendo { get; } = new();
        public FakePeople People { get; }
        public Dictionary<long, byte[]> Pictures { get; } = [];
        public Launcher Launcher => _rig.Launcher;
        public int Changes => Volatile.Read(ref _changes);
        public string Folder => Path.Combine(Launcher.LauncherFolder, Launcher.CacheFolderName, NextendoAvatars.FolderName);

        public string Log()
        {
            using var file = new FileStream(Launcher.Log.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }

        public ValueTask DisposeAsync() => _rig.DisposeAsync();
    }

    private static readonly string[] LogSecrets = ["Luna", "Kai", "Nico", "Dario", "1234567", "avatar?pid", "access-", "#3366ff"];

    private static void Populate(AvatarRig rig)
    {
        var people = rig.People;
        people.Friends.Add(FakePeople.Person(11, "Luna", "SW-2071-4456-9902", status: 2, appId: NextendoPresence.MkwTitleId, favorite: true));
        people.Friends.Add(FakePeople.Person(12, "Kai", "SW-5530-1187-2264", status: 1));
        people.Friends.Add(FakePeople.Person(13, "Bea", "SW-9048-3317-6695"));
        people.Requests.Add(FakePeople.Person(21, "Nico", "SW-6621-5508-3317"));
        people.Sent.Add(FakePeople.Person(31, "Carol", "SW-5587-1120-9964"));
        people.Blocked.Add(FakePeople.Person(51, "Grumpy42", "SW-7719-0034-5562"));
        people.Recent.AddRange([FakePeople.Person(11, "Luna", "SW-2071-4456-9902"), FakePeople.Person(51, "Grumpy42", "SW-7719-0034-5562"), FakePeople.Person(41, "Dario", "SW-3302-7719-4458")]);
        foreach (var pid in new long[] { Self, 11, 12, 21, 41, 51 })
        {
            rig.Pictures[pid] = Jpeg((byte)pid);
        }
    }

    [Fact]
    public async Task On_nextendo_the_window_gets_everyone_s_picture_and_colour_and_the_player_s_own()
    {
        await using var rig = new AvatarRig();
        Populate(rig);
        Assert.Equal(NextendoAvatar.None, rig.Launcher.NextendoAvatarOf(11)); // nothing asked yet: the disc with the initials
        Assert.Equal(NextendoAvatar.None, rig.Launcher.NextendoOwnAvatar);

        await rig.Launcher.RefreshNextendoFriendsAsync();
        // the list came: Nextendo's colour is behind the initials at once, the pictures follow
        Assert.Equal(new NextendoAvatar(null, "#3366ff"), rig.Launcher.NextendoAvatarOf(11));
        Assert.Empty(rig.Nextendo.AvatarsAsked);
        var before = rig.Changes;
        Assert.Equal(7, await rig.Launcher.NextendoAvatarsStepAsync(CancellationToken.None));
        // the player first, who asks, the friends (who is on first), who was asked, who was met; never who is blocked
        Assert.Equal([Self, 21, 11, 12, 13, 31, 41], rig.Nextendo.AvatarsAsked);
        Assert.Equal(5, rig.Changes - before); // the window is told of each picture that arrived

        var luna = rig.Launcher.NextendoAvatarOf(11);
        Assert.Equal("#3366ff", luna.Color);
        Assert.Equal(Jpeg(11), File.ReadAllBytes(luna.Path!));
        Assert.StartsWith(rig.Folder, luna.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Jpeg(unchecked((byte)Self)), File.ReadAllBytes(rig.Launcher.NextendoOwnAvatar.Path!));
        Assert.Equal("", rig.Launcher.NextendoOwnAvatar.Color); // Nextendo does not say the player's own colour
        Assert.Equal(new NextendoAvatar(null, "#3366ff"), rig.Launcher.NextendoAvatarOf(13)); // Bea has no picture: her colour
        Assert.NotNull(rig.Launcher.NextendoAvatarOf(21).Path);
        Assert.NotNull(rig.Launcher.NextendoAvatarOf(41).Path); // met: a picture, and no colour (that list has none)
        Assert.Null(rig.Launcher.NextendoAvatarOf(51).Path);
        Assert.Equal(NextendoAvatar.None, rig.Launcher.NextendoAvatarOf(0));

        // the list again and again (every 30 s; the panel opened): not one picture more is asked for
        for (var i = 0; i < 5; i++)
        {
            await rig.Launcher.RefreshNextendoFriendsAsync();
            Assert.Equal(0, await rig.Launcher.NextendoAvatarsStepAsync(CancellationToken.None));
        }
        Assert.Equal(7, rig.Nextendo.AvatarsAsked.Count);
        // and the list itself stays without pictures
        Assert.DoesNotContain(rig.People.Calls, call => call.StartsWith("GET /api/friends", StringComparison.Ordinal) && !call.Contains("images=0", StringComparison.Ordinal) && !call.Contains("/blocked", StringComparison.Ordinal));

        // Kai chose another picture and another colour on Nextendo: the next list says so, and only his is fetched
        rig.People.Friends[1]["avatar"] = "a2";
        rig.People.Friends[1]["color"] = "#00AA00";
        rig.Pictures[12] = Jpeg(99);
        var kai = rig.Launcher.NextendoAvatarOf(12).Path;
        before = rig.Changes;
        await rig.Launcher.RefreshNextendoFriendsAsync();
        Assert.Equal(new NextendoAvatar(kai, "#00aa00"), rig.Launcher.NextendoAvatarOf(12)); // the colour now, the old picture until the new one is here
        Assert.True(rig.Changes > before);
        Assert.Equal(1, await rig.Launcher.NextendoAvatarsStepAsync(CancellationToken.None));
        Assert.Equal(12, rig.Nextendo.AvatarsAsked[^1]);
        Assert.Equal(Jpeg(99), File.ReadAllBytes(rig.Launcher.NextendoAvatarOf(12).Path!));

        // nothing of it reached an MKWMP server, a log or a file's name
        Assert.Empty(rig.Seat.Requests);
        var log = rig.Log();
        Assert.All(LogSecrets, secret => Assert.DoesNotContain(secret, log, StringComparison.Ordinal));
        Assert.All(Directory.GetFiles(rig.Folder).Select(Path.GetFileName), name => Assert.DoesNotContain("1234567", name!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_mkwmp_servers_no_picture_is_asked_and_unlinking_deletes_them()
    {
        await using var rig = new AvatarRig();
        Populate(rig);
        await rig.Launcher.RefreshNextendoFriendsAsync();
        Assert.Equal(7, await rig.Launcher.NextendoAvatarsStepAsync(CancellationToken.None));
        Assert.NotEmpty(Directory.GetFiles(rig.Folder));

        // PLAY goes to MKWMP servers: the discs are the launcher's own again, and Nextendo is asked for nothing
        await rig.Launcher.SetPlayOnAsync(PlayNetwork.Mkwmp);
        Assert.Equal(NextendoAvatar.None, rig.Launcher.NextendoAvatarOf(11));
        Assert.Equal(NextendoAvatar.None, rig.Launcher.NextendoOwnAvatar);
        await rig.Launcher.RefreshNextendoFriendsAsync();
        Assert.Equal(0, await rig.Launcher.NextendoAvatarsStepAsync(CancellationToken.None));
        Assert.Equal(7, rig.Nextendo.AvatarsAsked.Count);
        Assert.NotEmpty(Directory.GetFiles(rig.Folder)); // kept for when PLAY goes back there

        // back on Nextendo: they are shown from the files, nothing is fetched again
        await rig.Launcher.SetPlayOnAsync(PlayNetwork.Nextendo);
        await rig.Launcher.RefreshNextendoFriendsAsync();
        Assert.Equal(0, await rig.Launcher.NextendoAvatarsStepAsync(CancellationToken.None));
        Assert.NotNull(rig.Launcher.NextendoAvatarOf(11).Path);

        await rig.Launcher.UnlinkNextendoAsync();
        Assert.Empty(Directory.GetFiles(rig.Folder));
        Assert.Equal(NextendoAvatar.None, rig.Launcher.NextendoAvatarOf(11));
        Assert.Equal(0, await rig.Launcher.NextendoAvatarsStepAsync(CancellationToken.None));
        Assert.Equal(7, rig.Nextendo.AvatarsAsked.Count);
    }
}

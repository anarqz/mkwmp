// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using Mkwmp.Core;
using Mkwmp.Core.Net;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.Design;

/// <summary>
/// The demo's Nextendo account: nothing leaves the PC and no browser opens. As a real install, the demo plays on
/// Nextendo Network unless it stands for one that already played on MKWMP servers: <c>--demo</c> (a server, a
/// profile) is on MKWMP servers and sees home's invitation; <c>--demo-offline</c> and <c>--demo-fresh</c> are on
/// Nextendo with nobody signed in. <c>--demo-nextendo</c> is signed in and playing on Nextendo Network,
/// <c>--demo-nextendo-linked</c> signed in and playing on MKWMP servers, <c>--demo-mkwmp</c> on MKWMP servers
/// whatever the start (with <c>--demo-offline</c>: the locked "Configure Online"). "Sign in" signs a made-up account
/// in after a moment; <c>--state nextendo-linking</c> is that moment, held.
/// <para>
/// Playing on Nextendo (<c>--demo-nextendo</c>, or <c>--demo-nextendo-friends</c>, which is the same), the account has
/// made-up Nextendo friends: two requests, friends who play, are online or offline (two favourites), a request sent
/// and two players met in a race; one player is blocked. Every action works on that list, and nothing leaves the PC.
/// <c>--state nx-nofriends</c>: nobody yet; <c>--state nx-loading</c>: the list has not come; <c>--state nx-error</c>:
/// Nextendo does not answer. The player and some of these people have a profile picture (a made-up one, drawn here
/// into the temp folder: no network), some only Nextendo's colour behind their initials, the others neither.
/// </para>
/// </summary>
public sealed partial class DesignLauncher
{
    private bool _nextendoLinked;
    private bool _nextendoLinking;
    private PlayNetwork _playOn = PlayNetwork.Nextendo;
    private (string Text, bool IsError)? _nextendoNote;
    private CancellationTokenSource? _linking;

    /// <summary>PLAY goes to Nextendo: no MKWMP server is "contacted" in the demo either.</summary>
    private bool NextendoActive => _nextendoLinked && _playOn == PlayNetwork.Nextendo && !_offlineOnly;

    public NextendoView Nextendo => new(
        _nextendoLinked, _nextendoLinked ? "Alex" : "", _nextendoLinked ? "SW-4821-9130-5567" : "", _playOn, NextendoActive,
        _nextendoLinking, false, NextendoActive ? CoreStrings.NextendoAllowed : "", NextendoActive ? "good" : "muted", _nextendoNote?.Text, _nextendoNote?.IsError ?? false);

    /// <summary>The demo's players in lobbies: on Nextendo Network the "Race · Open · 3/24" lobby of its Status page; on MKWMP servers the demo server's seats.</summary>
    public int? PlayersInLobbies => NextendoActive ? 3 : OnlineEnabled ? LobbyPlayers.FromServer(Server) : null;

    /// <summary>For screenshots: a linked account, playing on Nextendo or on MKWMP servers.</summary>
    public void PretendNextendo(bool playing) => Change(() =>
    {
        (_nextendoLinked, _playOn) = (true, playing ? PlayNetwork.Nextendo : PlayNetwork.Mkwmp);
        Sync();
    });

    /// <summary>For screenshots: on MKWMP servers, as an install that chose them (or had them before Nextendo was the default).</summary>
    public void PretendMkwmp() => Change(() =>
    {
        _playOn = PlayNetwork.Mkwmp;
        Sync();
    });

    /// <summary>For screenshots (<c>--state nextendo-linking</c>): the sign-in page is open in the browser, and stays so.</summary>
    private void PretendNextendoLinking() => Change(() => (_nextendoLinking, _nextendoNote) = (true, (CoreStrings.NextendoLinkWaiting, false)));

    public async Task LinkNextendoAsync(Func<Uri, bool> openBrowser)
    {
        if (_nextendoLinking)
        {
            return;
        }
        var linking = _linking = new CancellationTokenSource();
        Change(() => (_nextendoLinking, _nextendoNote) = (true, (CoreStrings.NextendoLinkWaiting, false)));
        try
        {
            await Task.Delay(2200, linking.Token).ConfigureAwait(false);
            Change(() =>
            {
                // a first sign-in is the choice of Nextendo, as in the real launcher
                (_nextendoLinking, _playOn, _nextendoNote) = (false, _nextendoLinked || Session.Busy ? _playOn : PlayNetwork.Nextendo, (CoreStrings.NextendoLinked("Alex"), false));
                _nextendoLinked = true;
                Sync();
            });
        }
        catch (OperationCanceledException)
        {
            Change(() => (_nextendoLinking, _nextendoNote) = (false, (CoreStrings.NextendoLinkCancelled, false)));
        }
        linking.Dispose();
    }

    public void CancelNextendoLink()
    {
        try
        {
            _linking?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // it ended by itself
        }
    }

    public Task UnlinkNextendoAsync()
    {
        Change(() =>
        {
            (_nextendoLinked, _nextendoNote) = (false, (CoreStrings.NextendoUnlinked, false)); // the choice of network stays
            Sync();
        });
        return Task.CompletedTask;
    }

    public Task SetPlayOnAsync(PlayNetwork network)
    {
        if (Session.Busy)
        {
            Change(() => _nextendoNote = (CoreStrings.PressStopFirst, true));
            return Task.CompletedTask;
        }
        Change(() =>
        {
            (_playOn, _nextendoNote) = (network, null);
            Sync();
        });
        return Task.CompletedTask;
    }

    public Task RefreshNextendoAsync() => Task.CompletedTask;

    // ── friends on Nextendo ──

    private NextendoFriendsView _nxFriends = DemoNextendoFriends;

    private static readonly NextendoFriendsView DemoNextendoFriends = new(
        true, true,
        [
            new NextendoFriend(1800000011, "Luna", "SW-2071-4456-9902", FriendState.Playing, "MKW", "Online", true),
            new NextendoFriend(1800000012, "Kai", "SW-5530-1187-2264", FriendState.Playing, "MKW", "", false),
            new NextendoFriend(1800000013, "Mateus", "SW-8812-0045-7731", FriendState.Online, "", "", true),
            new NextendoFriend(1800000014, "Sofia", "SW-3349-6620-1058", FriendState.Online, "", "", false),
            new NextendoFriend(1800000015, "Rin", "SW-7705-2291-4480", FriendState.Playing, "Splatoon 2", "Turf War", false),
            new NextendoFriend(1800000016, "Jonas", "SW-1264-9973-0526", FriendState.Offline, "", "", false),
            new NextendoFriend(1800000017, "Bea", "SW-9048-3317-6695", FriendState.Offline, "", "", true),
            new NextendoFriend(1800000018, "Theo", "SW-4416-7702-8843", FriendState.Offline, "", "", false),
        ],
        [new NextendoPerson(1800000021, "Nico", "SW-6621-5508-3317"), new NextendoPerson(1800000022, "Yuki", "SW-0193-8846-2275")],
        [new NextendoPerson(1800000031, "Carol", "SW-5587-1120-9964")],
        [new NextendoPerson(1800000041, "Dario", "SW-3302-7719-4458"), new NextendoPerson(1800000042, "Mei", "SW-8870-2265-1139")],
        [new NextendoPerson(1800000051, "Grumpy42", "SW-7719-0034-5562")],
        null, false, false, true);

    public NextendoFriendsView NextendoFriends => NextendoActive ? _nxFriends with { ShowOnline = Settings.NextendoShowOnline } : NextendoFriendsView.Off;

    /// <summary>For screenshots (<c>--state nx-nofriends | nx-loading | nx-error</c>).</summary>
    private void PretendNextendoFriends(string state) => Change(() => _nxFriends = state switch
    {
        "nx-nofriends" => _nxFriends with { Friends = [], Incoming = [], Sent = [], Recent = [], Blocked = [] },
        "nx-loading" => _nxFriends with { Loaded = false, Friends = [], Incoming = [], Sent = [], Recent = [] },
        _ => _nxFriends with { Loaded = false, Friends = [], Incoming = [], Sent = [], Recent = [], Note = CoreStrings.NextendoUnreachable, NoteIsError = true },
    });

    private async Task NextendoFriendsAsync(Func<NextendoFriendsView, NextendoFriendsView> change)
    {
        if (!NextendoActive || _nxFriends.Busy)
        {
            return;
        }
        Change(() => _nxFriends = _nxFriends with { Busy = true });
        await Pause(300).ConfigureAwait(false);
        Change(() => _nxFriends = change(_nxFriends) with { Busy = false });
    }

    private string NextendoNameOf(long pid) =>
        _nxFriends.Friends.FirstOrDefault(f => f.Pid == pid)?.Name
        ?? _nxFriends.Incoming.Concat(_nxFriends.Sent).Concat(_nxFriends.Recent).Concat(_nxFriends.Blocked).FirstOrDefault(p => p.Pid == pid)?.Name
        ?? CoreStrings.NextendoSomeone;

    public Task AddNextendoFriendAsync(string friendCode) => NextendoFriendsAsync(v =>
    {
        if (NextendoFriendCode.Normalize(friendCode) is not { } code)
        {
            return v with { Note = CoreStrings.NextendoFriendCodeRule, NoteIsError = true };
        }
        if (code == Nextendo.FriendCode)
        {
            return v with { Note = CoreStrings.NextendoFriendCodeOwn, NoteIsError = true };
        }
        if (v.Recent.FirstOrDefault(p => p.FriendCode == code) is not { } met)
        {
            // only the players "met" exist in the demo; any other code is nobody's
            return v with { Note = CoreStrings.NextendoFriendCodeUnknown, NoteIsError = true };
        }
        return v with { Recent = [.. v.Recent.Where(p => p.Pid != met.Pid)], Sent = [.. v.Sent, met], Note = CoreStrings.RequestSent(met.Name), NoteIsError = false };
    });

    public Task AcceptNextendoFriendAsync(long pid) => NextendoFriendsAsync(v => v.Incoming.FirstOrDefault(p => p.Pid == pid) is { } who
        ? v with
        {
            Incoming = [.. v.Incoming.Where(p => p.Pid != pid)],
            Friends = [.. v.Friends, new NextendoFriend(who.Pid, who.Name, who.FriendCode, FriendState.Online, "", "", false)],
            Note = CoreStrings.FriendsNow(who.Name),
            NoteIsError = false,
        }
        : v);

    public Task DeclineNextendoFriendAsync(long pid) => NextendoFriendsAsync(v =>
        v with { Incoming = [.. v.Incoming.Where(p => p.Pid != pid)], Note = CoreStrings.RequestDeclined(NextendoNameOf(pid)), NoteIsError = false });

    public Task RemoveNextendoFriendAsync(long pid) => NextendoFriendsAsync(v =>
        v with { Friends = [.. v.Friends.Where(f => f.Pid != pid)], Note = CoreStrings.FriendRemoved(NextendoNameOf(pid)), NoteIsError = false });

    public Task BlockNextendoPlayerAsync(long pid) => NextendoFriendsAsync(v =>
    {
        var name = NextendoNameOf(pid);
        var code = v.Friends.FirstOrDefault(f => f.Pid == pid)?.FriendCode ?? "";
        return v with
        {
            Friends = [.. v.Friends.Where(f => f.Pid != pid)],
            Incoming = [.. v.Incoming.Where(p => p.Pid != pid)],
            Blocked = [.. v.Blocked, new NextendoPerson(pid, name, code)],
            Note = CoreStrings.NextendoBlocked(name),
            NoteIsError = false,
        };
    });

    public Task UnblockNextendoPlayerAsync(long pid) => NextendoFriendsAsync(v =>
        v with { Blocked = [.. v.Blocked.Where(p => p.Pid != pid)], Note = CoreStrings.NextendoUnblocked(NextendoNameOf(pid)), NoteIsError = false });

    public Task FavoriteNextendoFriendAsync(long pid, bool favorite) => NextendoFriendsAsync(v =>
        v with { Friends = [.. v.Friends.Select(f => f.Pid == pid ? f with { Favorite = favorite } : f)], Note = null });

    public Task RefreshNextendoFriendsAsync() => Task.CompletedTask;

    // ── profile pictures ──

    private const long DemoOwnPid = 1800000001;

    /// <summary>Who has a picture in the demo (its two colours), and who only a colour.</summary>
    private static readonly Dictionary<long, (string Color, string? Figure)> DemoLooks = new()
    {
        [DemoOwnPid] = ("#2f6fed", "#ffd9a0"),
        [1800000011] = ("#7b3ff2", "#ffe3f1"), // Luna
        [1800000012] = ("#e4572e", null), // Kai: a colour, no picture
        [1800000013] = ("#1f9d6b", "#fff4c2"), // Mateus
        [1800000014] = ("#f2c14e", null), // Sofia: a light colour
        [1800000015] = ("#d7263d", "#ffffff"), // Rin
        [1800000017] = ("#0e7c86", "#d8f6ff"), // Bea (offline: dimmed)
        [1800000021] = ("#3a2a6a", "#ffcf99"), // Nico asks
        [1800000022] = ("#c2185b", null), // Yuki
        [1800000041] = ("#455a64", "#ffe0b2"), // Dario, met in a race
    };

    private static readonly Dictionary<long, string?> DemoPictures = [];

    public NextendoAvatar NextendoAvatarOf(long pid)
    {
        if (!NextendoActive || !DemoLooks.TryGetValue(pid, out var look))
        {
            return NextendoAvatar.None;
        }
        lock (DemoPictures)
        {
            if (!DemoPictures.TryGetValue(pid, out var path))
            {
                DemoPictures[pid] = path = look.Figure is null ? null : DemoPicture(pid, look.Color, look.Figure);
            }
            return new NextendoAvatar(path, look.Color);
        }
    }

    public NextendoAvatar NextendoOwnAvatar => NextendoAvatarOf(DemoOwnPid);

    /// <summary>A 256×256 PNG in the temp folder: a plain figure on a colour that gets darker downwards. Null when it cannot be written (the disc then shows the initials).</summary>
    private static string? DemoPicture(long pid, string back, string figure)
    {
        const int Side = 256;
        static (int R, int G, int B) Rgb(string hex) => (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));
        var (bg, fg) = (Rgb(back), Rgb(figure));
        var pixels = new byte[Side * Side * 4];
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                var shade = 1.0 - (0.35 * y / Side);
                var (dx, dy) = (x - 128.0, y - 100.0);
                var head = (dx * dx) + (dy * dy) < 46 * 46;
                var (sx, sy) = ((x - 128.0) / 84, (y - 250.0) / 86);
                var body = (sx * sx) + (sy * sy) < 1;
                var (r, g, b) = head || body ? fg : ((int)(bg.R * shade), (int)(bg.G * shade), (int)(bg.B * shade));
                var at = ((y * Side) + x) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = ((byte)b, (byte)g, (byte)r, 255);
            }
        }
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "mkwmp-demo-avatars");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"demo-{pid % 1000}.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
                System.Windows.Media.Imaging.BitmapSource.Create(Side, Side, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, Side * 4)));
            using var stream = File.Create(file);
            encoder.Save(stream);
            return file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

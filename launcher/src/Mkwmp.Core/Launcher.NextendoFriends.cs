// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// Friends on Nextendo Network (Net/NextendoFriends.cs): while PLAY goes there with a linked account
/// (<see cref="NextendoMode"/>), the launcher keeps that account's friends list current, tells the player what is new
/// in it (the same toasts and the same three settings as an MKWMP server's friends), manages it (add by friend code,
/// accept, decline, remove, block, unblock, favourite) and says on Nextendo that the player is online, or playing.
/// <para>
/// Nextendo has no push channel: the list is asked for again and again, at the pace <see cref="NextendoRoster"/>
/// sets. Nothing of it reaches an MKWMP server or a log: no friend's name, pid, friend code or id is ever written to
/// one. The game gets, on <c>/local/v1/friends</c>, the friends whose id on the game's servers Nextendo told (so it
/// can match them to the friends its server names); Nextendo tells none yet, and the answer is empty until it does.
/// </para>
/// </summary>
public sealed partial class Launcher
{
    /// <summary>How often the presence loop looks at what must be said (it asks Nextendo only when something is new or due).</summary>
    private static readonly TimeSpan PresenceTick = TimeSpan.FromSeconds(3);

    private readonly NextendoRoster _nxFriends;
    private readonly NextendoPresence _nxPresence;
    private readonly Signal _nxWake = new();

    // ---- under _gate ----------------------------------------------------------------------
    private NextendoFriendList? _nxList;
    private IReadOnlyList<NextendoPerson> _nxRecent = NextendoFriendsView.Off.Recent;
    private IReadOnlyList<NextendoPerson> _nxRecentShown = NextendoFriendsView.Off.Recent;
    private IReadOnlyList<NextendoPerson> _nxBlocked = NextendoFriendsView.Off.Blocked;
    private (string Text, bool IsError)? _nxNote;

    /// <summary>The note says the list does not come (not what an action answered): it goes away when the list comes again.</summary>
    private bool _nxNoteIsPoll;
    private bool _nxBusy;

    /// <summary>The account the lists are of (0: none).</summary>
    private long _nxFor;

    /// <summary>The list does not come: said in the log once, not at every try.</summary>
    private bool _nxFailing;
    private long _nxPolledAt;

    /// <summary>Above 0 while the launcher closes or the account is being unlinked: nothing is said of the player on Nextendo but "offline".</summary>
    private int _nxQuiet;
    private TimeSpan _nxNext = NextendoRoster.Every;
    private volatile NextendoFriendsView _nxView = NextendoFriendsView.Off;

    public NextendoFriendsView NextendoFriends => _nxView;

    /// <summary>Under the lock, from <see cref="Rebuild"/>.</summary>
    private NextendoFriendsView NextendoFriendsNow()
    {
        var off = NextendoFriendsView.Off;
        var show = _config.Settings.NextendoShowOnline;
        return !NextendoMode ? off with { ShowOnline = show }
            : new NextendoFriendsView(true, _nxList is not null, _nxList?.Friends ?? off.Friends, _nxList?.Incoming ?? off.Incoming, _nxList?.Sent ?? off.Sent,
                _nxRecentShown, _nxBlocked, _nxNote?.Text, _nxNote?.IsError ?? false, _nxBusy, show);
    }

    /// <summary>"Players you met": those who can be asked (a friend code), and are not the player, a friend, asked, asking or blocked. Under the lock.</summary>
    private void FilterRecent()
    {
        var self = _config.Nextendo.Pid;
        var known = new HashSet<long>(_nxBlocked.Select(p => p.Pid)) { self };
        if (_nxList is { } list)
        {
            known.UnionWith(list.Friends.Select(f => f.Pid));
            known.UnionWith(list.Incoming.Select(p => p.Pid));
            known.UnionWith(list.Sent.Select(p => p.Pid));
        }
        NextendoPerson[] shown = [.. _nxRecent.Where(p => p.FriendCode.Length > 0 && !known.Contains(p.Pid)).Take(8)];
        if (!shown.SequenceEqual(_nxRecentShown))
        {
            _nxRecentShown = shown;
        }
    }

    private static bool SameList(NextendoFriendList? a, NextendoFriendList b) =>
        a is not null && a.Friends.SequenceEqual(b.Friends) && a.Incoming.SequenceEqual(b.Incoming) && a.Sent.SequenceEqual(b.Sent);

    /// <summary>
    /// Who the lists in hand name (names, friend codes, ids): what a minidump that leaves this PC must not carry of
    /// other people (the game holds its friends in memory). Launcher.Crash.cs overwrites them.
    /// </summary>
    private List<string> NextendoPeopleSecrets()
    {
        lock (_gate)
        {
            var people = (_nxList?.Friends ?? []).Select(f => new NextendoPerson(f.Pid, f.Name, f.FriendCode, f.Account))
                .Concat(_nxList?.Incoming ?? []).Concat(_nxList?.Sent ?? []).Concat(_nxRecent).Concat(_nxBlocked);
            return [.. people.SelectMany(p => new[] { p.Name, p.FriendCode, p.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Account })];
        }
    }

    /// <summary>
    /// What <c>GET /local/v1/friends</c> gives the game on Nextendo (NEXTENDO_API.md §4): the friends whose id on the
    /// game's servers Nextendo told, so the game can match them; none while it tells no ids. Null while the first
    /// list is on its way (the game reads its friends once per sign-in: it is told to ask again, not given an empty
    /// list); a list that does not come is an empty one.
    /// </summary>
    IReadOnlyList<NextendoFriend>? ILocalApiHost.NextendoGameFriends
    {
        get
        {
            lock (_gate)
            {
                if (_nxList is not { } list)
                {
                    return _options.BackgroundLoops && NextendoMode && !_nxFailing ? null : [];
                }
                var self = _config.Nextendo.Pid;
                return NextendoFriendList.ForGame(list.Friends, self, NextendoOwnAccount(self), LocalApi.MaxFriends);
            }
        }
    }

    // ---- keeping the list current ---------------------------------------------------------

    private async Task NextendoFriendsLoopAsync(CancellationToken life)
    {
        while (!life.IsCancellationRequested)
        {
            try
            {
                if (!NextendoMode)
                {
                    await ForgetNextendoFriendsAsync().ConfigureAwait(false);
                    await _nxWake.WaitAsync(TimeSpan.FromSeconds(5), life).ConfigureAwait(false);
                    continue;
                }
                // An action or the panel just asked: not again at once, only sooner than planned.
                var next = _clock.GetElapsedTime(Volatile.Read(ref _nxPolledAt)) < TimeSpan.FromSeconds(2) && Volatile.Read(ref _nxFor) != 0
                    ? _nxNext
                    : await PollNextendoFriendsAsync(life).ConfigureAwait(false);
                await _nxWake.WaitAsync(next, life).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (life.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Not on Nextendo any more (unlinked, MKWMP servers chosen): nothing of the account's friends stays in memory.</summary>
    private async Task ForgetNextendoFriendsAsync()
    {
        if (Interlocked.Exchange(ref _nxFor, 0) == 0)
        {
            return;
        }
        await _nxFriends.ResetAsync().ConfigureAwait(false);
        var off = NextendoFriendsView.Off;
        Mutate(() => (_nxList, _nxRecent, _nxRecentShown, _nxBlocked, _nxNote, _nxNoteIsPoll, _nxBusy, _nxFailing) = (null, off.Recent, off.Recent, off.Blocked, null, false, false, false));
    }

    /// <summary>Asks Nextendo for the list once, shows it, and notifies what is new in it. How long until the next look.</summary>
    private async Task<TimeSpan> PollNextendoFriendsAsync(CancellationToken cancel)
    {
        var link = Config.Nextendo;
        if (!NextendoMode)
        {
            return TimeSpan.FromSeconds(5);
        }
        if (Volatile.Read(ref _nxFor) != link.Pid)
        {
            await ForgetNextendoFriendsAsync().ConfigureAwait(false); // another account signed in
            Volatile.Write(ref _nxFor, link.Pid);
        }
        var poll = await _nxFriends.PollAsync(Settings.Background, link.Pid, cancel).ConfigureAwait(false);
        Volatile.Write(ref _nxPolledAt, _clock.GetTimestamp());
        _nxNext = poll.Next;
        if (!NextendoMode || Config.Nextendo.Pid != link.Pid)
        {
            return TimeSpan.FromSeconds(1); // the answer for an account that was left meanwhile is nobody's
        }
        var failed = false;
        Mutate(() =>
        {
            if (poll.Error is { } error)
            {
                failed = !_nxFailing;
                _nxFailing = true;
                if (!_nxBusy && (_nxNote is null || _nxNoteIsPoll))
                {
                    (_nxNote, _nxNoteIsPoll) = ((error.Kind == NextendoError.Unreachable ? Strings.NextendoUnreachable : error.Message, true), true);
                }
                return;
            }
            _nxFailing = false;
            if (poll.List is { } list && !SameList(_nxList, list))
            {
                _nxList = list;
            }
            FilterRecent();
            if (_nxNoteIsPoll && !_nxBusy)
            {
                (_nxNote, _nxNoteIsPoll) = (null, false); // a problem that went away
            }
        });
        if (failed && poll.Error is { Kind: NextendoError.Unreachable or NextendoError.Bad } why)
        {
            Note($"nextendo: the friends list does not come ({why.Message}); trying again later"); // never who is in it
        }
        foreach (var news in poll.Events)
        {
            Toast(news.Toast());
        }
        return poll.Next;
    }

    public async Task RefreshNextendoFriendsAsync()
    {
        if (!NextendoMode)
        {
            return;
        }
        try
        {
            _nxFriends.Hurry();
            // a panel opened and closed in a hurry does not ask each time (the loop keeps the list current anyway)
            if (!_options.BackgroundLoops || !NextendoFriends.Loaded || _clock.GetElapsedTime(Volatile.Read(ref _nxPolledAt)) > TimeSpan.FromSeconds(3))
            {
                await PollNextendoFriendsAsync(_life.Token).ConfigureAwait(false);
            }
            await RefreshNextendoExtrasAsync(force: false).ConfigureAwait(false);
            _nxWake.Set();
        }
        catch (OperationCanceledException)
        {
            // the launcher is closing
        }
    }

    /// <summary>The players met lately and the blocked ones: asked when the panel opens, and after a block or an unblock.</summary>
    private async Task RefreshNextendoExtrasAsync(bool force)
    {
        var pid = Config.Nextendo.Pid;
        var (recent, blocked) = await _nxFriends.ExtrasAsync(force, _life.Token).ConfigureAwait(false);
        if ((recent is null && blocked is null) || !NextendoMode || Config.Nextendo.Pid != pid)
        {
            return;
        }
        Mutate(() =>
        {
            if (recent is not null && !recent.SequenceEqual(_nxRecent))
            {
                _nxRecent = recent;
            }
            if (blocked is not null && !blocked.SequenceEqual(_nxBlocked))
            {
                _nxBlocked = blocked;
            }
            FilterRecent();
        });
    }

    // ---- managing it ----------------------------------------------------------------------

    /// <summary>What Nextendo calls someone of the lists in hand; "a player" when it gave no name.</summary>
    private string NextendoNameOf(long pid)
    {
        lock (_gate)
        {
            var name = _nxList?.Friends.FirstOrDefault(f => f.Pid == pid)?.Name
                ?? (_nxList?.Incoming ?? []).Concat(_nxList?.Sent ?? []).Concat(_nxRecent).Concat(_nxBlocked).FirstOrDefault(p => p.Pid == pid)?.Name;
            return name is { Length: > 0 } ? name : Strings.NextendoSomeone;
        }
    }

    /// <summary>One action on the friends list, its outcome as the note, then the list again (and sooner for a minute).</summary>
    /// <param name="run">Does it with the access token; the text of a note that says it went well (null: nothing to say).</param>
    private async Task NextendoFriendActionAsync(Func<string, CancellationToken, Task<string?>> run, bool extras = false)
    {
        if (!NextendoMode)
        {
            return;
        }
        lock (_gate)
        {
            if (_nxBusy)
            {
                return;
            }
            _nxBusy = true;
            Rebuild();
        }
        RaiseChanged();
        (string Text, bool IsError)? note;
        try
        {
            var done = await _nextendo.CallAsync(access => run(access, _life.Token), _life.Token).ConfigureAwait(false);
            note = done is null ? null : (done, false);
        }
        catch (NextendoException e)
        {
            note = (e.Kind == NextendoError.Refused ? e.Message : NextendoProblem(e), true);
        }
        catch (OperationCanceledException)
        {
            note = (Strings.NextendoUnreachable, true);
        }
        Mutate(() => (_nxBusy, _nxNote, _nxNoteIsPoll) = (false, note, false));
        try
        {
            _nxFriends.Hurry();
            await PollNextendoFriendsAsync(_life.Token).ConfigureAwait(false);
            if (extras)
            {
                await RefreshNextendoExtrasAsync(force: true).ConfigureAwait(false);
            }
            _nxWake.Set();
        }
        catch (OperationCanceledException)
        {
            // the launcher is closing
        }
    }

    public Task AddNextendoFriendAsync(string friendCode)
    {
        if (!NextendoMode)
        {
            return Task.CompletedTask;
        }
        if (NextendoFriendCode.Normalize(friendCode) is not { } code)
        {
            Mutate(() => (_nxNote, _nxNoteIsPoll) = ((Strings.NextendoFriendCodeRule, true), false));
            return Task.CompletedTask;
        }
        if (string.Equals(code, Config.Nextendo.FriendCode, StringComparison.OrdinalIgnoreCase))
        {
            Mutate(() => (_nxNote, _nxNoteIsPoll) = ((Strings.NextendoFriendCodeOwn, true), false));
            return Task.CompletedTask;
        }
        return NextendoFriendActionAsync(async (access, cancel) =>
        {
            var (friend, already) = await _nextendoApi.AddFriendAsync(access, code, cancel).ConfigureAwait(false);
            var name = friend.Name.Length > 0 ? friend.Name : code;
            return already ? Strings.FriendsNow(name) : Strings.RequestSent(name);
        });
    }

    private Task NextendoVerbAsync(string verb, long pid, Func<string, string?> done, bool extras = false)
    {
        var name = NextendoNameOf(pid);
        return NextendoFriendActionAsync(async (access, cancel) =>
        {
            await _nextendoApi.FriendActionAsync(access, verb, pid, cancel).ConfigureAwait(false);
            return done(name);
        }, extras);
    }

    public Task AcceptNextendoFriendAsync(long pid) => NextendoVerbAsync("accept", pid, Strings.FriendsNow);

    public Task DeclineNextendoFriendAsync(long pid) => NextendoVerbAsync("decline", pid, Strings.RequestDeclined);

    public Task RemoveNextendoFriendAsync(long pid) => NextendoVerbAsync("remove", pid, Strings.FriendRemoved);

    public Task BlockNextendoPlayerAsync(long pid) => NextendoVerbAsync("block", pid, Strings.NextendoBlocked, extras: true);

    public Task UnblockNextendoPlayerAsync(long pid) => NextendoVerbAsync("unblock", pid, Strings.NextendoUnblocked, extras: true);

    public Task FavoriteNextendoFriendAsync(long pid, bool favorite) => NextendoFriendActionAsync(async (access, cancel) =>
    {
        await _nextendoApi.FavoriteAsync(access, pid, favorite, cancel).ConfigureAwait(false);
        return null;
    });

    // ---- presence -------------------------------------------------------------------------

    /// <summary>
    /// What Nextendo is to be told of the player now: playing while a game of theirs is online there, online while
    /// only the launcher runs; nothing (null) when PLAY does not go to Nextendo, nobody is signed in, or the player
    /// turned "Show me online to friends" off.
    /// </summary>
    internal NextendoPresenceState? NextendoPresenceWanted
    {
        get
        {
            lock (_gate)
            {
                if (!NextendoMode || !_config.Settings.NextendoShowOnline || Volatile.Read(ref _nxQuiet) > 0)
                {
                    return null;
                }
                var played = _sessionState == SessionState.Running && _nextendoSession && _gameRunning;
                // a game started outside the launcher that asked for its Nextendo session here (the launcher stands by for it)
                var hosted = _local.Instance.ParentPid != 0 && Volatile.Read(ref _nextendoServed) != 0;
                return played || hosted ? NextendoPresence.InGame : NextendoPresence.InLauncher;
            }
        }
    }

    private async Task NextendoPresenceLoopAsync(CancellationToken life)
    {
        while (!life.IsCancellationRequested)
        {
            try
            {
                await _nxPresence.StepAsync(NextendoPresenceWanted, life).ConfigureAwait(false);
                await Task.Delay(PresenceTick, life).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (life.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One look at what must be said, outside the loop (tests, and a launcher without background loops).</summary>
    internal Task<bool> NextendoPresenceStepAsync(CancellationToken cancel) => _nxPresence.StepAsync(NextendoPresenceWanted, cancel);

    /// <summary>Leaving, or unlinking: friends see the player offline at once, when Nextendo can be reached in time.</summary>
    private async Task NextendoOfflineAsync(TimeSpan patience)
    {
        using var limit = new CancellationTokenSource(patience);
        try
        {
            await _nxPresence.StepAsync(null, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // not in time: Nextendo forgets the presence by itself after 90 s
        }
    }
}

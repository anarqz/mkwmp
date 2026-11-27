// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// Profile pictures on Nextendo Network (Net/NextendoAvatars.cs): while PLAY goes there with a linked account
/// (<see cref="NextendoMode"/>), the window shows the picture each person chose on Nextendo instead of a disc with
/// their initials: the player's own, their friends', and those of who asked, who was asked and who was met.
/// <para>
/// The friends list (asked every 30 s, without pictures) says who they are and when someone's look changed; the
/// pictures themselves come one by one from Nextendo's public picture route, only when one is missing, changed or a
/// day old, and are kept in <c>cache\nextendo-avatars</c> beside the launcher. No blocked player's picture is asked
/// for. Unlinking the account deletes them all. Nothing of it reaches an MKWMP server or a log.
/// </para>
/// </summary>
public sealed partial class Launcher
{
    private readonly NextendoAvatars _nxAvatars;

    /// <summary>The launcher's own client for pictures (it follows no redirect); null when every request goes through one handler (tests).</summary>
    private readonly HttpClient? _avatarHttp;
    private readonly Signal _nxAvatarWake = new();

    /// <summary>Under _gate: how Nextendo draws the people of the list in hand.</summary>
    private IReadOnlyDictionary<long, NextendoLook> _nxLooks = new Dictionary<long, NextendoLook>();

    public NextendoAvatar NextendoAvatarOf(long pid)
    {
        if (pid <= 0 || !NextendoMode)
        {
            return NextendoAvatar.None;
        }
        string color;
        lock (_gate)
        {
            color = _nxLooks.GetValueOrDefault(pid)?.Color ?? "";
        }
        var path = _nxAvatars.PathOf(pid);
        return path is null && color.Length == 0 ? NextendoAvatar.None : new NextendoAvatar(path, color);
    }

    public NextendoAvatar NextendoOwnAvatar => NextendoAvatarOf(Config.Nextendo.Pid);

    /// <summary>Under the lock: takes the looks of a list that arrived. True when someone's changed (a colour, another picture chosen).</summary>
    private bool FollowLooks(NextendoFriendList list)
    {
        if (_nxLooks.Count == list.Looks.Count && list.Looks.All(l => _nxLooks.TryGetValue(l.Key, out var had) && had == l.Value))
        {
            return false;
        }
        _nxLooks = list.Looks;
        return true;
    }

    /// <summary>Not on Nextendo any more: nobody's picture is wanted (the files stay for the next time, unless the account was unlinked).</summary>
    private void ForgetNextendoAvatars()
    {
        lock (_gate)
        {
            _nxLooks = new Dictionary<long, NextendoLook>();
        }
        _nxAvatars.Want([]);
    }

    /// <summary>
    /// After a list arrived: whose pictures the window shows now, the most looked at first (the player, who asks, the
    /// friends who are on, the others, who was asked, who was met). Asks nothing itself; the loop does, for those due.
    /// </summary>
    /// <param name="looksChanged">Someone's colour changed: the window draws again.</param>
    private void WantNextendoAvatars(bool looksChanged)
    {
        var wants = new List<NextendoAvatarWant>();
        lock (_gate)
        {
            if (!NextendoMode)
            {
                return;
            }
            NextendoAvatarWant Of(long pid) => new(pid, _nxLooks.GetValueOrDefault(pid)?.Key ?? NextendoAvatars.NoKey, NextendoAvatars.Fresh);
            wants.Add(new NextendoAvatarWant(_config.Nextendo.Pid, NextendoAvatars.NoKey, NextendoAvatars.OwnFresh));
            if (_nxList is { } list)
            {
                wants.AddRange(list.Incoming.Select(p => Of(p.Pid)));
                wants.AddRange(list.Friends.OrderBy(f => f.State).ThenByDescending(f => f.Favorite).Select(f => Of(f.Pid)));
                wants.AddRange(list.Sent.Select(p => Of(p.Pid)));
            }
            wants.AddRange(_nxRecentShown.Select(p => Of(p.Pid)));
        }
        if (_nxAvatars.Want(wants) && _options.BackgroundLoops)
        {
            _nxAvatarWake.Set();
        }
        if (looksChanged)
        {
            RaiseChanged();
        }
    }

    private async Task NextendoAvatarsLoopAsync(CancellationToken life)
    {
        while (!life.IsCancellationRequested)
        {
            try
            {
                // woken when a list names a picture that is due; else a look now and then (a wait Nextendo asked for is over)
                await _nxAvatarWake.WaitAsync(TimeSpan.FromMinutes(5), life).ConfigureAwait(false);
                if (NextendoMode)
                {
                    await _nxAvatars.DrainAsync(NextendoAvatars.Gap, life).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (life.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>The pictures that are due, now and without the pauses (tests, and a launcher without background loops). How many were asked for.</summary>
    internal Task<int> NextendoAvatarsStepAsync(CancellationToken cancel) =>
        NextendoMode ? _nxAvatars.DrainAsync(TimeSpan.Zero, cancel) : Task.FromResult(0);
}

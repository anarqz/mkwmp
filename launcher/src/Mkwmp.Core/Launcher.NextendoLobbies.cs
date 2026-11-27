// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// "Players in lobbies" on home's Play card (<see cref="ILauncher.PlayersInLobbies"/>). On Nextendo Network it is asked of its live
/// status (Net/NextendoLive.cs) every 45 s, only while the window shows home (<see cref="WatchPlayersInLobbies"/>):
/// nothing is asked while the launcher is hidden or on another page. On MKWMP servers it is the seats the status the
/// launcher already polls (<see cref="Server"/>) reports: no new call.
/// </summary>
public sealed partial class Launcher
{
    private readonly NextendoLobbies _nxLobbies;
    private readonly Signal _lobbiesWake = new();
    private int _lobbiesWanted;
    private DateTimeOffset _lobbiesDue = DateTimeOffset.MinValue;

    /// <summary>Under the lock.</summary>
    private int? _nxLobbyPlayers;

    private bool _lobbiesFailing;

    public int? PlayersInLobbies
    {
        get
        {
            lock (_gate)
            {
                return NextendoMode ? _nxLobbyPlayers : IsOnline(_config.Settings) ? LobbyPlayers.FromServer(_server) : null;
            }
        }
    }

    public void WatchPlayersInLobbies(bool on)
    {
        if (Interlocked.Exchange(ref _lobbiesWanted, on ? 1 : 0) != (on ? 1 : 0))
        {
            _lobbiesWake.Set();
        }
    }

    private async Task NextendoLobbiesLoopAsync(CancellationToken life)
    {
        while (!life.IsCancellationRequested)
        {
            try
            {
                if (!NextendoMode || Volatile.Read(ref _lobbiesWanted) == 0)
                {
                    if (!NextendoMode)
                    {
                        await _nxLobbies.ResetAsync().ConfigureAwait(false);
                        _lobbiesDue = DateTimeOffset.MinValue;
                        SetLobbyPlayers(null);
                    }
                    await _lobbiesWake.WaitAsync(TimeSpan.FromSeconds(5), life).ConfigureAwait(false);
                    continue;
                }
                var now = _clock.GetUtcNow();
                if (now >= _lobbiesDue)
                {
                    var poll = await _nxLobbies.PollAsync(life).ConfigureAwait(false);
                    _lobbiesDue = _clock.GetUtcNow() + poll.Next;
                    if (NextendoMode)
                    {
                        SetLobbyPlayers(poll.Players);
                    }
                    if (poll.Error is { } why)
                    {
                        if (!_lobbiesFailing && why.Kind is NextendoError.Unreachable or NextendoError.Bad)
                        {
                            Note($"nextendo: the players in lobbies do not come ({why.Message}); trying again later");
                        }
                        _lobbiesFailing = true;
                    }
                    else
                    {
                        _lobbiesFailing = false;
                    }
                }
                await _lobbiesWake.WaitAsync(TimeSpan.FromSeconds(5), life).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (life.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void SetLobbyPlayers(int? players)
    {
        bool changed;
        lock (_gate)
        {
            changed = _nxLobbyPlayers != players;
            _nxLobbyPlayers = players;
        }
        if (changed)
        {
            RaiseChanged();
        }
    }
}

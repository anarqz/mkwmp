// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;

namespace Mkwmp.Core.Net;

/// <summary>What the local API answers the game.</summary>
internal abstract record SeatAnswer
{
    /// <param name="Room">The seat's lobby room, as the seat server last said (null: none).</param>
    public sealed record Seated(string Ticket, string Lobby, string Name, SeatRoom? Room = null) : SeatAnswer;

    public sealed record Queued(int Position, int SeatsTotal) : SeatAnswer;

    public sealed record Refused(string Message) : SeatAnswer;

    public sealed record Unavailable(string Message) : SeatAnswer;
}

/// <summary>The seat as the window shows it (<paramref name="Message"/>: why, for Busy and Refused).</summary>
internal sealed record SeatSnapshot(SeatPhase Phase, int Position, string? Message);

/// <summary>
/// The launcher's seat: the one place that joins, heartbeats, rejoins after a server restart and,
/// in <see cref="SeatMode.Online"/>, lets the seat go once the game stops asking for it. The game
/// reaches it through the local API (<see cref="WantAsync"/>); <see cref="TickAsync"/> is called
/// about once a second by the PLAY loop.
/// </summary>
internal sealed class SeatSession(SeatApi api, SeatMode mode, string name, string pin, Action<string> note, TimeProvider clock, TimeSpan? busyRetry = null) : IDisposable
{
    /// <summary>The game stops asking (left Online Play, quit): the seat goes after this.</summary>
    public static readonly TimeSpan IdleRelease = TimeSpan.FromSeconds(90);

    /// <summary>A join that failed (busy, unreachable) is retried after this.</summary>
    public static readonly TimeSpan DefaultBusyRetry = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _busyRetry = busyRetry ?? DefaultBusyRetry;
    private (string Sid, string Key, string Boot)? _session;
    private SeatView? _view;
    private long _nextPoll = clock.GetTimestamp();
    private long? _lastWant;

    /// <summary>Only refusals the player must fix (wrong PIN, unregistered...) stick for the session; a busy join is retried.</summary>
    private string? _refused;
    private string? _busy;
    private int? _shownPosition;

    public SeatApi Api => api;

    private bool Due => clock.GetTimestamp() >= _nextPoll;

    private void PollIn(TimeSpan delay) => _nextPoll = clock.GetTimestamp() + (long)(delay.TotalSeconds * clock.TimestampFrequency);

    /// <summary>The game asks for a seat (POST /local/v1/seat). Joins if needed.</summary>
    public async Task<SeatAnswer> WantAsync(CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            _lastWant = clock.GetTimestamp();
            if (_refused is not null)
            {
                return new SeatAnswer.Refused(_refused);
            }
            if (_session is null)
            {
                // A busy join retries at most every BusyRetry.
                if (_busy is not null && !Due)
                {
                    return new SeatAnswer.Unavailable(_busy);
                }
                if (await JoinAsync(cancel).ConfigureAwait(false) is { } failed)
                {
                    return failed;
                }
            }
            return Answer();
        }
        finally
        {
            _gate.Release();
        }
    }

    private SeatAnswer Answer() => _view switch
    {
        { Ticket: { } t } v => new SeatAnswer.Seated(t.Token, t.Lobby, v.Name, v.Room),
        { } v => new SeatAnswer.Queued(v.Position ?? 0, v.SeatsTotal),
        null => new SeatAnswer.Unavailable(Strings.NotConnectedYet),
    };

    /// <summary>Null when joined; else what to answer the game.</summary>
    private async Task<SeatAnswer?> JoinAsync(CancellationToken cancel)
    {
        try
        {
            var v = await api.JoinAsync(name, pin, cancel).ConfigureAwait(false);
            _session = (v.SessionId, v.SessionKey ?? "", v.BootId);
            PollIn(TimeSpan.FromMilliseconds(Math.Clamp(v.NextPollMs, 1_000, 30_000)));
            Show(v);
            _view = v;
            _busy = null;
            return null;
        }
        catch (ApiException e) when (e.Kind == ApiError.Busy)
        {
            if (_busy != e.Message)
            {
                note(e.Message);
            }
            _busy = e.Message;
            PollIn(_busyRetry);
            return new SeatAnswer.Unavailable(e.Message);
        }
        catch (ApiException e) when (e.Kind == ApiError.Refused)
        {
            note($"the server refused: {e.Message}");
            _refused = e.Message;
            return new SeatAnswer.Refused(e.Message);
        }
        catch (ApiException e)
        {
            return new SeatAnswer.Unavailable(e.Message);
        }
    }

    /// <summary>Called about once a second. Returns a new ticket (and its lobby) to write for the game, if any.</summary>
    public async Task<(string Ticket, string Lobby)?> TickAsync(CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var before = _view?.Ticket?.Token;
            var wanted = mode switch
            {
                SeatMode.Start => _refused is null,
                _ => _lastWant is { } t && clock.GetElapsedTime(t) < IdleRelease,
            };
            if (!wanted)
            {
                if (_session is { } s)
                {
                    _session = null;
                    note("the game stopped asking; releasing the seat");
                    await api.LeaveAsync(s.Sid, s.Key).ConfigureAwait(false);
                    _view = null;
                }
                return null;
            }
            if (_session is null)
            {
                if (Due && await JoinAsync(cancel).ConfigureAwait(false) is not null)
                {
                    PollIn(_busyRetry);
                }
            }
            else if (Due)
            {
                await HeartbeatAsync(cancel).ConfigureAwait(false);
            }
            return _view?.Ticket is { } after && after.Token != before ? (after.Token, after.Lobby) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancel)
    {
        var (sid, key, boot) = _session!.Value;
        try
        {
            var v = await api.PollAsync(sid, key, cancel).ConfigureAwait(false);
            if (v.BootId != boot)
            {
                note("the server restarted; rejoining");
                Rejoin();
                return;
            }
            PollIn(TimeSpan.FromMilliseconds(Math.Clamp(v.NextPollMs, 1_000, 30_000)));
            Show(v);
            _view = v;
        }
        catch (ApiException e) when (e.Kind == ApiError.Gone)
        {
            note("session expired; rejoining");
            Rejoin();
        }
        catch (ApiException e)
        {
            note($"{e.Message}; retrying");
            PollIn(TimeSpan.FromSeconds(5));
        }
    }

    private void Rejoin()
    {
        _session = null;
        _view = null;
        _nextPoll = clock.GetTimestamp();
    }

    private void Show(SeatView v)
    {
        if (v.State == "queued" && _shownPosition != v.Position)
        {
            note(string.Create(CultureInfo.InvariantCulture, $"server full ({v.SeatsTotal - v.SeatsFree}/{v.SeatsTotal} seats); you are {Ordinal(v.Position ?? 0)} in queue"));
        }
        if (v.State == "seated" && _view?.State != "seated")
        {
            note("seated");
        }
        _shownPosition = v.State == "queued" ? v.Position : null;
    }

    /// <summary>What the window shows: the seat's state.</summary>
    public async Task<SeatSnapshot> SnapshotAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_refused is not null)
            {
                return new SeatSnapshot(SeatPhase.Refused, 0, _refused);
            }
            return _view switch
            {
                { Ticket: not null } => new SeatSnapshot(SeatPhase.Seated, 0, null),
                { } v => new SeatSnapshot(SeatPhase.Queued, v.Position ?? 0, null),
                null when _session is null && _busy is not null => new SeatSnapshot(SeatPhase.Busy, 0, _busy),
                null when _session is null && mode == SeatMode.Online => new SeatSnapshot(SeatPhase.WaitingForGame, 0, null),
                null => new SeatSnapshot(SeatPhase.Joining, 0, null),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Frees the seat (game exited, STOP, the launcher closes).</summary>
    public async Task ReleaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is { } s)
            {
                _session = null;
                await api.LeaveAsync(s.Sid, s.Key).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string Ordinal(int n) => n.ToString(CultureInfo.InvariantCulture) + ((n % 10, n % 100) switch
    {
        (_, >= 11 and <= 13) => "th",
        (1, _) => "st",
        (2, _) => "nd",
        (3, _) => "rd",
        _ => "th",
    });

    public void Dispose() => _gate.Dispose();
}

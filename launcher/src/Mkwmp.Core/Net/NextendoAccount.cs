// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Net;

/// <summary>The game token in hand, with when it ends.</summary>
internal sealed record NextendoGrant(NextendoGameToken Token, DateTimeOffset ExpiresAt);

/// <summary>
/// The linked account's tokens, kept current (docs/contracts/NEXTENDO_API.md §1, §4). The refresh token is in the
/// <see cref="NextendoVault"/> and rotates: each refresh stores the new one before anything else uses it. The access
/// token (an hour) and the game token (<c>nx2…</c>, a day) live in memory only; the game token is renewed when less
/// than an hour of it is left, and a 429's <c>Retry-After</c> is waited out. <c>online-status</c> is asked at most
/// once a minute. One call at a time.
/// </summary>
internal sealed class NextendoAccount(NextendoApi api, NextendoVault vault, TimeProvider clock) : IDisposable
{
    /// <summary>The game is never handed a token with less than this left.</summary>
    public static readonly TimeSpan RenewGameTokenBefore = TimeSpan.FromHours(1);

    /// <summary>An access token this close to its end is replaced first.</summary>
    public static readonly TimeSpan RenewAccessBefore = TimeSpan.FromSeconds(60);

    /// <summary>How long an answer of <c>online-status</c> stands.</summary>
    public static readonly TimeSpan OnlineFor = TimeSpan.FromSeconds(60);

    private sealed record Access(string Token, DateTimeOffset ExpiresAt);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _refresh;
    private Access? _access;
    private NextendoGrant? _grant;
    private DateTimeOffset _grantRetryAt = DateTimeOffset.MinValue;
    private NextendoNetwork? _network;
    private (NextendoOnline Answer, DateTimeOffset At)? _online;

    /// <summary>A token that ends at <paramref name="expiresAt"/> (null: none in hand) must be replaced before it is used.</summary>
    internal static bool Stale(DateTimeOffset now, DateTimeOffset? expiresAt, TimeSpan margin) => expiresAt is not { } end || end - now < margin;

    /// <summary>Every token in memory: what a report's minidump must not carry (a dump is a piece of the game's memory).</summary>
    public IReadOnlyList<string> Secrets
    {
        get
        {
            var all = new List<string>();
            if (Volatile.Read(ref _refresh) is { } refresh)
            {
                all.Add(refresh);
            }
            if (_access is { } access)
            {
                all.Add(access.Token);
            }
            if (_grant is { } grant)
            {
                all.Add(grant.Token.Token);
            }
            return all;
        }
    }

    /// <summary>A sign-in just made: its refresh token is kept, its access token used. False when it could not be kept on disk (it then lasts this run).</summary>
    public async Task<bool> AdoptAsync(NextendoTokens tokens, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            Drop();
            return Keep(tokens);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Unlinked: nothing of the account stays, in memory or on disk. The refresh token that was kept, to revoke it.</summary>
    public async Task<string?> ForgetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var refresh = _refresh ?? vault.Load();
            Drop();
            vault.Clear();
            return refresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Drop()
    {
        (_refresh, _access, _grant, _network, _online) = (null, null, null, null, null);
        _grantRetryAt = DateTimeOffset.MinValue;
    }

    private bool Keep(NextendoTokens tokens)
    {
        _refresh = tokens.Refresh;
        _access = new Access(tokens.Access, clock.GetUtcNow() + TimeSpan.FromSeconds(tokens.ExpiresIn));
        return vault.Save(tokens.Refresh);
    }

    /// <summary>An access token with at least a minute left; a new one (and the refresh token it rotates to) when not.</summary>
    private async Task<string> AccessLockedAsync(bool fresh, CancellationToken cancel)
    {
        if (!fresh && _access is { } have && !Stale(clock.GetUtcNow(), have.ExpiresAt, RenewAccessBefore))
        {
            return have.Token;
        }
        var refresh = _refresh ??= vault.Load();
        if (refresh is null)
        {
            throw new NextendoException(NextendoError.SignedOut, Strings.NextendoLinkAgain);
        }
        _access = null;
        Keep(await api.RefreshAsync(refresh, cancel).ConfigureAwait(false)); // the new refresh token is on disk before the access token is used
        return _access!.Token;
    }

    /// <summary>A call with the access token; one that is refused as no longer good is made once more with a new one.</summary>
    private async Task<T> WithAccessAsync<T>(Func<string, Task<T>> call, CancellationToken cancel)
    {
        var access = await AccessLockedAsync(fresh: false, cancel).ConfigureAwait(false);
        try
        {
            return await call(access).ConfigureAwait(false);
        }
        catch (NextendoException e) when (e.Kind == NextendoError.SignedOut)
        {
            access = await AccessLockedAsync(fresh: true, cancel).ConfigureAwait(false);
            return await call(access).ConfigureAwait(false);
        }
    }

    private async Task<T> LockedAsync<T>(Func<Task<T>> run, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            return await run().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A call with the access token that does not hold the gate while it runs (the friends list, presence: they must
    /// never keep the game waiting for its token). One that is refused as no longer good is made once more with a new
    /// token; when another call renewed it meanwhile, with that one.
    /// </summary>
    public async Task<T> CallAsync<T>(Func<string, Task<T>> call, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(call);
        var access = await LockedAsync(() => AccessLockedAsync(fresh: false, cancel), cancel).ConfigureAwait(false);
        try
        {
            return await call(access).ConfigureAwait(false);
        }
        catch (NextendoException e) when (e.Kind == NextendoError.SignedOut)
        {
            var stale = access;
            access = await LockedAsync(() => AccessLockedAsync(fresh: _access?.Token == stale, cancel), cancel).ConfigureAwait(false);
            return await call(access).ConfigureAwait(false);
        }
    }

    /// <summary>Who the account is, as Nextendo says now.</summary>
    public Task<NextendoUser> UserAsync(CancellationToken cancel) =>
        LockedAsync(() => WithAccessAsync(access => api.UserInfoAsync(access, cancel), cancel), cancel);

    /// <summary>Where the game connects and the certificate it trusts; asked again when <paramref name="fresh"/> (each PLAY).</summary>
    public Task<NextendoNetwork> NetworkAsync(bool fresh, CancellationToken cancel) => LockedAsync(
        async () =>
        {
            if (!fresh && _network is { } known)
            {
                return known;
            }
            return _network = await WithAccessAsync(access => api.NetworkAsync(access, cancel), cancel).ConfigureAwait(false);
        },
        cancel);

    /// <summary>The game token, with at least an hour left whenever Nextendo gives one.</summary>
    public Task<NextendoGrant> GameTokenAsync(CancellationToken cancel) => LockedAsync(() => GrantLockedAsync(cancel), cancel);

    private async Task<NextendoGrant> GrantLockedAsync(CancellationToken cancel)
    {
        var now = clock.GetUtcNow();
        if (_grant is { } good && !Stale(now, good.ExpiresAt, RenewGameTokenBefore))
        {
            return good;
        }
        // Less than an hour left (or none): a new one, unless Nextendo said to wait and the one in hand still works.
        var usable = _grant is { } old && old.ExpiresAt > now ? old : null;
        if (now < _grantRetryAt)
        {
            return usable ?? throw Limited(_grantRetryAt - now);
        }
        try
        {
            var token = await WithAccessAsync(access => api.GameTokenAsync(access, cancel), cancel).ConfigureAwait(false);
            _online = null; // asked with the old token
            return _grant = new NextendoGrant(token, clock.GetUtcNow() + TimeSpan.FromSeconds(token.ExpiresIn));
        }
        catch (NextendoException e) when (e.Kind == NextendoError.Limited)
        {
            _grantRetryAt = clock.GetUtcNow() + e.RetryAfter;
            return usable ?? throw Limited(e.RetryAfter);
        }
        catch (NextendoException e) when (e.Kind == NextendoError.Unreachable && usable is not null)
        {
            return usable;
        }
    }

    private static NextendoException Limited(TimeSpan wait) =>
        new(NextendoError.Limited, Strings.NextendoLimited((long)Math.Ceiling(wait.TotalSeconds)), wait);

    /// <summary>Whether the account may go online now; the last answer stands for a minute unless <paramref name="fresh"/>.</summary>
    public Task<NextendoOnline> OnlineAsync(bool fresh, CancellationToken cancel) => LockedAsync(
        async () =>
        {
            if (!fresh && _online is { } last && clock.GetUtcNow() - last.At < OnlineFor && clock.GetUtcNow() >= last.At)
            {
                return last.Answer;
            }
            var grant = await GrantLockedAsync(cancel).ConfigureAwait(false);
            NextendoOnline answer;
            try
            {
                answer = await api.OnlineAsync(grant.Token.Token, cancel).ConfigureAwait(false);
            }
            catch (NextendoException e) when (e.Kind == NextendoError.SignedOut)
            {
                // The game token itself was refused: a new one, once.
                _grant = null;
                grant = await GrantLockedAsync(cancel).ConfigureAwait(false);
                answer = await api.OnlineAsync(grant.Token.Token, cancel).ConfigureAwait(false);
            }
            _online = (answer, clock.GetUtcNow());
            return answer;
        },
        cancel);

    public void Dispose() => _gate.Dispose();
}

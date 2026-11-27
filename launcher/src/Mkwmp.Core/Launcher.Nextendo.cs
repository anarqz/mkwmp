// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net.Sockets;
using System.Text.Json;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core;

/// <summary>
/// Nextendo Network (docs/contracts/NEXTENDO_API.md): linking an account in the browser (OAuth 2.0 with PKCE, the
/// answer on 127.0.0.1), the choice of where PLAY goes online, PLAY on Nextendo, and the session the game asks for
/// (<c>GET /local/v1/nextendo/session</c>).
/// <para>
/// The link is this PC's: who the account is and the choice are in client.toml (<c>[nextendo]</c>), the sign-in in
/// <see cref="NextendoVault"/>, and none of it is sent to an MKWMP server. Nextendo Network is where PLAY goes online
/// by default (<see cref="OnNextendo"/>), and while it is, <see cref="IsOnline"/> is false: no MKWMP server is
/// contacted at all (no status, presence, friends, seat or voice), exactly as with no server chosen. Without a linked
/// account PLAY starts the game offline; with one (<see cref="NextendoMode"/>) the game goes online there.
/// </para>
/// </summary>
public sealed partial class Launcher
{
    private readonly NextendoApi _nextendoApi;
    private readonly NextendoAccount _nextendo;

    // ---- under _gate ----------------------------------------------------------------------
    private bool _nextendoLinking;
    private bool _nextendoChecking;
    private CancellationTokenSource? _nextendoLinkCancel;
    private (string Text, bool IsError)? _nextendoNote;

    /// <summary>How the account stood when Nextendo was last asked: allowed, a reason of <c>online-status</c>, or what went wrong.</summary>
    private (NextendoStanding Kind, string Detail)? _nextendoStanding;

    /// <summary>The session under way goes to Nextendo (its status says so; it holds no seat, as an offline one).</summary>
    private bool _nextendoSession;

    private volatile NextendoView _nextendoView = NextendoView.None;

    /// <summary>The game of this PLAY already got a session: "online somewhere else" is then this game itself.</summary>
    private int _nextendoServed;
    private string? _nextendoToasted;

    private enum NextendoStanding { Allowed, Refused, Problem }

    public NextendoView Nextendo => _nextendoView;

    /// <summary>
    /// PLAY goes to Nextendo Network now: an account is linked, Nextendo is chosen, and this run is not offline only
    /// (a required update that could not be installed: the mod in the game may be older than Nextendo mode).
    /// </summary>
    internal bool NextendoMode => _config is { Nextendo.Linked: true, PlayOn: PlayNetwork.Nextendo } && !_offlineOnly;

    /// <summary>
    /// Nextendo Network is where PLAY goes online (the default; the saved choice), linked or not: MKWMP servers are
    /// left alone. With no account linked PLAY is offline, and the window asks to sign in.
    /// </summary>
    internal bool OnNextendo => _config.PlayOn == PlayNetwork.Nextendo;

    bool ILocalApiHost.Nextendo => NextendoMode;

    /// <summary>Under the lock, from <see cref="Rebuild"/>.</summary>
    private NextendoView NextendoNow()
    {
        var link = _config.Nextendo;
        var (status, tone) = !link.Linked ? ("", "muted") : _nextendoStanding switch
        {
            { Kind: NextendoStanding.Allowed } => (Strings.NextendoAllowed, "good"),
            { Kind: NextendoStanding.Refused } refused => (Strings.NextendoRefused(refused.Detail), refused.Detail == "suspended" ? "bad" : "warn"),
            { Kind: NextendoStanding.Problem } problem => (problem.Detail, "bad"),
            _ => ("", "muted"),
        };
        return new NextendoView(link.Linked, link.Username, link.FriendCode, _config.PlayOn, NextendoMode, _nextendoLinking, _nextendoChecking,
            status, tone, _nextendoNote?.Text, _nextendoNote?.IsError ?? false);
    }

    /// <summary>What the player reads for a failed call; the details of a network failure go to the log (never a token: the messages hold none).</summary>
    private string NextendoProblem(NextendoException e)
    {
        if (e.Kind != NextendoError.Unreachable)
        {
            return e.Message;
        }
        Note($"nextendo: {e.Message}");
        return Strings.NextendoUnreachable;
    }

    // ---- link, unlink, the choice ---------------------------------------------------------

    public async Task LinkNextendoAsync(Func<Uri, bool> openBrowser)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        if (Session.Busy && OnNextendo)
        {
            // the game plays with the account linked now, or offline for want of one: neither changes under it
            Mutate(() => _nextendoNote = (Strings.PressStopFirst, true));
            return;
        }
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_nextendoLinking)
            {
                return;
            }
            cancel = _nextendoLinkCancel = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            (_nextendoLinking, _nextendoNote) = (true, null);
            Rebuild();
        }
        RaiseChanged();
        (string Text, bool IsError) note;
        var linked = false;
        try
        {
            using var loopback = new NextendoLoopback();
            var (verifier, state) = (Pkce.NewVerifier(), Pkce.NewState());
            if (!openBrowser(NextendoApi.AuthorizeUrl(loopback.RedirectUri, Pkce.Challenge(verifier), state)))
            {
                note = (Strings.NextendoLinkNoBrowser, true);
            }
            else
            {
                Mutate(() => _nextendoNote = (Strings.NextendoLinkWaiting, false));
                var answer = await loopback.WaitAsync(state, _options.NextendoLinkPatience, cancel.Token).ConfigureAwait(false);
                if (answer.Kind != CallbackKind.Code)
                {
                    note = (Strings.NextendoLinkDenied, true);
                }
                else
                {
                    var tokens = await _nextendoApi.ExchangeAsync(answer.Value, loopback.RedirectUri, verifier, cancel.Token).ConfigureAwait(false);
                    var user = await _nextendoApi.UserInfoAsync(tokens.Access, cancel.Token).ConfigureAwait(false);
                    var kept = await _nextendo.AdoptAsync(tokens, cancel.Token).ConfigureAwait(false);
                    var before = OnNextendo;
                    // A first link is the choice of Nextendo (an install that plays on MKWMP servers was invited to it);
                    // linking again keeps the choice, and nothing changes under a game that runs on an MKWMP server.
                    var choose = !Config.Nextendo.Linked && !Session.Busy;
                    EditConfig(c =>
                    {
                        var named = c.Nextendo.Named(user.Pid, user.Username, user.FriendCode);
                        return c with { Nextendo = choose && named.Linked ? named.Playing(PlayNetwork.Nextendo) : named };
                    }, wakeFriends: false);
                    linked = Config.Nextendo.Linked;
                    Note("nextendo: account linked; the link stays on this PC"); // never who: logs go into bug reports
                    note = kept ? (Strings.NextendoLinked(user.Username), false) : (Strings.NextendoLinkNotKept, true);
                    NetworkChanged(before);
                }
            }
        }
        catch (TimeoutException)
        {
            note = (Strings.NextendoLinkTimeout, true);
        }
        catch (OperationCanceledException)
        {
            note = (Strings.NextendoLinkCancelled, false);
        }
        catch (NextendoException e)
        {
            note = (NextendoProblem(e), true);
        }
        catch (LauncherException e)
        {
            note = (e.Message, true);
        }
        catch (SocketException e)
        {
            Note($"nextendo: no port on 127.0.0.1 for the sign-in's answer: {e.Message}");
            note = (Strings.NextendoLinkNoBrowser, true);
        }
        Mutate(() =>
        {
            (_nextendoLinking, _nextendoLinkCancel, _nextendoNote) = (false, null, note);
            if (linked)
            {
                _nextendoStanding = null; // another account, perhaps: asked again below
            }
        });
        cancel.Dispose();
        _nxWake.Set(); // a linked account's friends, from now
        if (linked)
        {
            _ = RefreshNextendoAsync();
        }
    }

    public void CancelNextendoLink()
    {
        lock (_gate)
        {
            try
            {
                _nextendoLinkCancel?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // it ended by itself meanwhile
            }
        }
    }

    public async Task UnlinkNextendoAsync()
    {
        if (Session.Busy && NextendoMode)
        {
            Mutate(() => _nextendoNote = (Strings.PressStopFirst, true));
            return;
        }
        CancelNextendoLink();
        if (!Config.Nextendo.Linked)
        {
            return;
        }
        var before = OnNextendo;
        // Friends see the player offline first (it takes the sign-in), then the account is forgotten here (the choice
        // of network stays: on Nextendo PLAY is offline until signed in again), then revoked there when it can be reached.
        string? refresh;
        (string Text, bool IsError) note = (Strings.NextendoUnlinked, false);
        Interlocked.Increment(ref _nxQuiet);
        try
        {
            await NextendoOfflineAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            refresh = await _nextendo.ForgetAsync().ConfigureAwait(false);
            try
            {
                EditConfig(c => c with { Nextendo = c.Nextendo.Unlinked() }, wakeFriends: false);
            }
            catch (LauncherException e)
            {
                note = (e.Message, true);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _nxQuiet);
        }
        _nxWake.Set();
        Mutate(() => (_nextendoStanding, _nextendoNote) = (null, note));
        Note("nextendo: account unlinked");
        NetworkChanged(before);
        if (refresh is not null)
        {
            try
            {
                await _nextendoApi.RevokeAsync(refresh, _life.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is NextendoException or OperationCanceledException)
            {
                Note("nextendo: the sign-in could not be revoked there now (forgotten on this PC)");
            }
        }
    }

    public Task SetPlayOnAsync(PlayNetwork network) => Task.Run(() =>
    {
        if (Config.PlayOn == network)
        {
            return;
        }
        if (Session.Busy)
        {
            Mutate(() => _nextendoNote = (Strings.PressStopFirst, true));
            return;
        }
        var before = OnNextendo;
        try
        {
            EditConfig(c => c with { Nextendo = c.Nextendo.Playing(network) }, wakeFriends: false);
            Mutate(() => _nextendoNote = null);
        }
        catch (LauncherException e)
        {
            Mutate(() => _nextendoNote = (e.Message, true));
            return;
        }
        NetworkChanged(before);
        if (NextendoMode)
        {
            _ = RefreshNextendoAsync();
        }
    });

    /// <summary>
    /// PLAY goes somewhere else now. To Nextendo: this launcher leaves the MKWMP server at once (friends there see it
    /// offline), and its loops go quiet. To MKWMP servers: they follow the server again.
    /// </summary>
    private void NetworkChanged(bool wasNextendo)
    {
        var now = OnNextendo;
        if (now == wasNextendo)
        {
            return;
        }
        Note(now ? "online: Nextendo Network (no MKWMP server is contacted)" : "online: MKWMP servers");
        _nxWake.Set(); // friends are Nextendo's now, or no longer
        ServerChanged(now ? Settings.Server : "");
    }

    public async Task RefreshNextendoAsync()
    {
        lock (_gate)
        {
            if (_nextendoChecking || !_config.Nextendo.Linked)
            {
                return;
            }
            _nextendoChecking = true;
            Rebuild();
        }
        RaiseChanged();
        (NextendoStanding, string)? standing = null;
        try
        {
            var user = await _nextendo.UserAsync(_life.Token).ConfigureAwait(false);
            FollowAccount(user.Pid, user.Username, user.FriendCode);
            // Whether it may go online is asked with a game token: only while PLAY goes there.
            if (NextendoMode)
            {
                var online = await _nextendo.OnlineAsync(fresh: false, _life.Token).ConfigureAwait(false);
                standing = online.Allow ? (NextendoStanding.Allowed, "") : (NextendoStanding.Refused, online.Reason);
            }
        }
        catch (NextendoException e)
        {
            standing = (NextendoStanding.Problem, NextendoProblem(e));
        }
        catch (OperationCanceledException)
        {
            // the launcher is closing
        }
        Mutate(() => (_nextendoChecking, _nextendoStanding) = (false, standing));
    }

    /// <summary>The account's name or friend code changed on Nextendo: client.toml follows (the choice of network stays).</summary>
    private void FollowAccount(long pid, string username, string friendCode)
    {
        var have = Config.Nextendo;
        var now = have.Named(pid, username, friendCode);
        if (!have.Linked || !now.Linked || now == have)
        {
            return;
        }
        try
        {
            EditConfig(c => c.Nextendo.Linked ? c with { Nextendo = c.Nextendo.Named(pid, username, friendCode) } : c, wakeFriends: false);
        }
        catch (LauncherException e)
        {
            Note(e.Message);
        }
    }

    // ---- PLAY on Nextendo -----------------------------------------------------------------

    /// <summary>
    /// The session file of a PLAY on Nextendo (§3), after asking Nextendo three things: may the account go online now
    /// (else PLAY ends with the reason), where the game connects and which certificate it trusts (asked at each
    /// PLAY), and who the account is. No MKWMP server is asked anything.
    /// </summary>
    private async Task<SessionFile> NextendoSessionFileAsync(string address, CancellationToken life)
    {
        try
        {
            var online = await _nextendo.OnlineAsync(fresh: true, life).ConfigureAwait(false);
            Mutate(() => _nextendoStanding = online.Allow ? (NextendoStanding.Allowed, "") : (NextendoStanding.Refused, online.Reason));
            if (!online.Allow)
            {
                Note($"nextendo: not allowed online ({online.Reason})");
                throw new LauncherException(Strings.NextendoRefused(online.Reason));
            }
            var network = await _nextendo.NetworkAsync(fresh: true, life).ConfigureAwait(false);
            var grant = await _nextendo.GameTokenAsync(life).ConfigureAwait(false);
            FollowAccount(grant.Token.Pid, grant.Token.Username, grant.Token.FriendCode);
            Note($"playing on Nextendo Network ({network.ServerHost}): no MKWMP server is contacted, no seat is taken"); // never who: logs go into bug reports
            return SessionFile.Nextendo(grant.Token.Username, address, Token, LauncherExe, network.CaPem);
        }
        catch (NextendoException e)
        {
            var why = NextendoProblem(e);
            Mutate(() => _nextendoStanding = (NextendoStanding.Problem, why));
            throw new LauncherException(why, e);
        }
    }

    /// <summary>
    /// <c>GET /local/v1/nextendo/session</c> (§4): the game token with at least an hour left, who it is for and where
    /// the game connects. <c>online-status</c> is asked again (at most once a minute); a refusal is shown here once,
    /// and the game stays offline.
    /// </summary>
    async Task<LocalResponse> ILocalApiHost.NextendoSessionAsync(CancellationToken cancel)
    {
        if (!NextendoMode)
        {
            return LocalApi.NextendoRefused("unlinked", Strings.NextendoNotLinked);
        }
        try
        {
            var online = await _nextendo.OnlineAsync(fresh: false, cancel).ConfigureAwait(false);
            // Once this game is online with the account, "online somewhere else" is the game itself asking again.
            if (!online.Allow && !(online.Reason == "elsewhere" && Volatile.Read(ref _nextendoServed) != 0))
            {
                var why = Strings.NextendoRefused(online.Reason);
                Mutate(() => _nextendoStanding = (NextendoStanding.Refused, online.Reason));
                ShowNextendoRefusal(online.Reason, why);
                return LocalApi.NextendoRefused(online.Reason, why);
            }
            var grant = await _nextendo.GameTokenAsync(cancel).ConfigureAwait(false);
            var network = await _nextendo.NetworkAsync(fresh: false, cancel).ConfigureAwait(false);
            Volatile.Write(ref _nextendoServed, 1);
            return new LocalResponse(200, JsonSerializer.Serialize(new
            {
                state = "ready",
                nexToken = grant.Token.Token,
                expiresAt = grant.ExpiresAt.ToUnixTimeMilliseconds(),
                pid = grant.Token.Pid,
                username = grant.Token.Username,
                friendCode = grant.Token.FriendCode,
                serverHost = network.ServerHost,
                natHost = network.NatHost,
                caFile = Probe.NextendoCa,
            }));
        }
        catch (NextendoException e) when (e.Kind is NextendoError.SignedOut or NextendoError.Scope)
        {
            var reason = e.Kind == NextendoError.Scope ? "scope" : "unlinked";
            Mutate(() => _nextendoStanding = (NextendoStanding.Problem, e.Message));
            ShowNextendoRefusal(reason, e.Message);
            return LocalApi.NextendoRefused(reason, e.Message);
        }
        catch (NextendoException e)
        {
            return new LocalResponse(503, JsonSerializer.Serialize(new { state = "unavailable", message = NextendoProblem(e) }));
        }
        catch (OperationCanceledException)
        {
            return new LocalResponse(503, JsonSerializer.Serialize(new { state = "unavailable", message = Strings.NextendoUnreachable }));
        }
    }

    /// <summary>The game was refused its session: said once per reason and PLAY (the game asks again and again).</summary>
    private void ShowNextendoRefusal(string reason, string message)
    {
        if (Interlocked.Exchange(ref _nextendoToasted, reason) == reason)
        {
            return;
        }
        Note($"nextendo: the game stays offline ({reason})");
        Toast(new Toast("Nextendo Network", message, ToastKind.Info));
    }
}

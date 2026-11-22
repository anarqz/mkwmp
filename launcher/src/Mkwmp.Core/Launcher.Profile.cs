// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>The profile (gamertag + PIN), the launcher's presence on the server, friends and reports.</summary>
public sealed partial class Launcher
{
    /// <summary>Wakes the friends loop (an action, or the name/PIN changed).</summary>
    private readonly Signal _friendsWake = new();

    private FriendWatch _friendWatch = new();

    /// <summary>Who the presence token belongs to (server, name key, PIN): a change signs in again.</summary>
    private ((string Server, string Name, string Pin) Who, string Token)? _presence;
    private (string Server, string Name, string Pin)? _reportAskedFor;

    private (string Server, string Name, string Pin)? Ident(LauncherSettings s) =>
        IsOnline(s) && ClientConfig.ValidName(s.Name) && s.Pin.Length == 6 ? (s.Server, s.Name.ToLowerInvariant(), s.Pin) : null;

    /// <summary>What PLAY shows for an API error (the details of a network failure go to the log).</summary>
    private string Reason(ApiException e)
    {
        if (e.Kind != ApiError.Transient)
        {
            return e.Message;
        }
        Note(e.Message);
        return Strings.Unreachable;
    }

    // ---- register, use a PIN, delete ------------------------------------------------------

    /// <summary>REGISTER: asks the server for the name. Success saves the PIN to client.toml; a taken name asks for its PIN instead.</summary>
    public async Task RegisterAsync(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Settings.Name != name)
        {
            await SaveSettingsAsync(Settings with { Name = name, Pin = "" }).ConfigureAwait(false);
        }
        if (!IsOnline(Settings))
        {
            // No server chosen: the name stays a local one, and nothing is asked of anyone.
            Mutate(() => (_register, _registerMessage) = (RegisterState.Failed, OnlineOffWhy));
            return;
        }
        // Signing in comes before the consent: the consent is the server's to keep (Launcher.Privacy.cs), asked for right after.
        Mutate(() => (_register, _registerMessage) = (RegisterState.Working, null));
        (RegisterState State, string? Message) outcome;
        try
        {
            var pin = await Api().RegisterAsync(name, _life.Token).ConfigureAwait(false);
            EditConfig(c => c with { Settings = c.Settings with { Pin = pin } }, wakeFriends: true);
            Note($"registered {name}");
            outcome = (RegisterState.Idle, null);
        }
        catch (ApiException e) when (e.Kind == ApiError.Taken)
        {
            outcome = (RegisterState.Taken, null);
        }
        catch (Exception e) when (e is ApiException or LauncherException or OperationCanceledException)
        {
            outcome = (RegisterState.Failed, e.Message);
        }
        Mutate(() => (_register, _registerMessage) = outcome);
    }

    /// <summary>
    /// Recover: the name is on the server already and the player has its PIN. The PIN is kept and,
    /// with a server chosen, tried at once (a sign-in): a wrong one is not kept and says so. A
    /// server that cannot be reached leaves the PIN in place, to be tried at the next sign-in.
    /// </summary>
    public async Task UsePinAsync(string name, string pin)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(pin);
        await SaveSettingsAsync(Settings with { Name = name, Pin = pin }).ConfigureAwait(false);
        if (Ident(Settings) is null)
        {
            Mutate(() => (_register, _registerMessage) = (RegisterState.Idle, null));
            return;
        }
        Mutate(() => (_register, _registerMessage) = (RegisterState.Working, null));
        (RegisterState State, string? Message) outcome = (RegisterState.Idle, null);
        try
        {
            await PresenceTokenAsync(_life.Token, needConsent: false).ConfigureAwait(false);
            Note($"signed in as {name} with its PIN");
        }
        catch (ApiException e) when (e.Kind == ApiError.Refused)
        {
            // Not this name's PIN: it does not stay, so the profile does not look signed in.
            try
            {
                EditConfig(c => c with { Settings = c.Settings with { Pin = "" } }, wakeFriends: false);
            }
            catch (LauncherException save)
            {
                Note(save.Message);
            }
            outcome = (RegisterState.Failed, e.Message);
        }
        catch (Exception e) when (e is ApiException or OperationCanceledException)
        {
            Note($"could not try the PIN now: {e.Message}");
        }
        Mutate(() => (_register, _registerMessage) = outcome);
        _friendsWake.Set();
    }

    /// <summary>
    /// Deletes the profile from the server. Deleted, or not on this server at all: the launcher
    /// forgets it. A wrong PIN or server trouble keeps everything (to retry).
    /// </summary>
    public async Task DeleteProfileAsync()
    {
        if (Session.Busy)
        {
            Mutate(() => (_register, _registerMessage) = (RegisterState.Failed, Strings.StopBeforeDelete));
            return;
        }
        if (!IsOnline(Settings))
        {
            Mutate(() => (_register, _registerMessage) = (RegisterState.Failed, OnlineOffWhy));
            return;
        }
        Mutate(() => (_register, _registerMessage) = (RegisterState.Working, null));
        var s = Settings;
        (RegisterState State, string? Message) outcome;
        try
        {
            var how = await Api().DeleteProfileAsync(s.Name, s.Pin, _life.Token).ConfigureAwait(false);
            ForgetProfile();
            Note(how == Deleted.Removed ? $"deleted the profile {s.Name}" : $"the server has no profile {s.Name}; forgot it");
            outcome = (RegisterState.Deleted, null);
        }
        catch (Exception e) when (e is ApiException or LauncherException or OperationCanceledException)
        {
            outcome = (RegisterState.Failed, e.Message);
        }
        Mutate(() => (_register, _registerMessage) = outcome);
    }

    /// <summary>Forgets the profile: name, PIN, its voice mutes, its friends and presence, and the ticket in the game's session file.</summary>
    private void ForgetProfile()
    {
        EditConfig(c => (c with { Settings = c.Settings with { Name = "", Pin = "" }, Mutes = [] }).Forgetting(c.Settings.Server), wakeFriends: false);
        var s = Settings;
        var target = _targets.Get(s.Target);
        if (GameFinder.Detect(target, s.Game) is { } install)
        {
            try
            {
                target.ForgetTicket(install);
            }
            catch (LauncherException e)
            {
                Note($"clearing the ticket in the game's session file: {e.Message}");
            }
        }
        Mutate(() =>
        {
            ClearFriendsAndReport();
            _presence = null;
        });
        _friendsWake.Set();
    }

    private void ClearFriendsAndReport()
    {
        (_friendList, _friendsNote, _friendsBusy) = (null, null, false);
        (_reportLoaded, _openReport, _reportNote, _reportBusy) = (null, null, null, false);
        _reportAskedFor = null;
        _friendWatch = new FriendWatch();
    }

    /// <summary>
    /// Another server, or none (Disconnect): this launcher leaves the old one at once (friends there
    /// see it offline), forgets what it knew of it (friends, the open report, its status), and the
    /// loops follow the new one, or go quiet when there is none.
    /// </summary>
    private void ServerChanged(string oldServer)
    {
        string? token = null;
        var online = IsOnline(Settings);
        Mutate(() =>
        {
            token = _presence?.Token;
            _presence = null;
            ClearFriendsAndReport();
            _server = online ? new ServerView(ServerState.Checking, null, 0, 0, 0, null) : NotAsked;
        });
        if (token is not null && !string.IsNullOrWhiteSpace(oldServer))
        {
            _ = Api(oldServer).PresenceEndAsync(token);
        }
        Note(online ? $"server: {Settings.Server}" : "server: none (offline)");
        _friendsWake.Set();
        _statusWake.Set();
    }

    /// <summary>The 18+ consent changed: sign in again (or stop being signed in).</summary>
    private void ConsentChanged()
    {
        ((string Server, string Name, string Pin) Who, string Token)? was = null;
        Mutate(() =>
        {
            was = _presence;
            _presence = null;
            ClearFriendsAndReport();
            if (_register == RegisterState.Failed && _registerMessage == Strings.NoConsent)
            {
                (_register, _registerMessage) = (RegisterState.Idle, null);
            }
        });
        if (was is { } left)
        {
            _ = Api(left.Who.Server).PresenceEndAsync(left.Token);
        }
        _friendsWake.Set();
        _statusWake.Set();
    }

    /// <summary>
    /// The presence token for the current name + PIN, signing in if needed. A profile the server
    /// deleted (a month unused) is registered again with the saved PIN first. Signing in tells what
    /// the server holds of the player's consent (Launcher.Privacy.cs); everything but the consent
    /// itself (<paramref name="needConsent"/> false) is then refused until it is given: friends,
    /// reports, a seat.
    /// </summary>
    private async Task<string> PresenceTokenAsync(CancellationToken cancel, bool needConsent = true)
    {
        var s = Settings;
        if (!IsOnline(s))
        {
            throw ApiException.Refused(OnlineOffWhy); // no server chosen (or offline only until MKWMP is updated): nobody to ask
        }
        var id = Ident(s) ?? throw ApiException.Refused(Strings.RegisterFirst);
        var token = await SignInAsync(s, id, cancel).ConfigureAwait(false);
        if (needConsent && !Settings.AdultConsent)
        {
            throw ApiException.Refused(Strings.NoConsent);
        }
        return token;
    }

    private async Task<string> SignInAsync(LauncherSettings s, (string Server, string Name, string Pin) id, CancellationToken cancel)
    {
        lock (_gate)
        {
            if (_presence is { } p && p.Who == id)
            {
                return p.Token;
            }
        }
        var api = Api(s.Server);
        Presence presence;
        try
        {
            presence = await api.PresenceAsync(id.Name, id.Pin, cancel).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.Kind == ApiError.Refused && e.Message == Strings.NotRegistered)
        {
            string pin;
            try
            {
                pin = await api.RestoreAsync(s.Name, id.Pin, cancel).ConfigureAwait(false);
            }
            catch (ApiException taken) when (taken.Kind == ApiError.Taken)
            {
                throw ApiException.Refused(Strings.ExpiredAndTaken(s.Name));
            }
            if (pin == id.Pin)
            {
                Note($"{s.Name} had expired on the server; registered it again with your PIN");
            }
            else
            {
                // An older server picked its own PIN: keep that one, or nobody has it.
                try
                {
                    EditConfig(c => c with { Settings = c.Settings with { Pin = pin } }, wakeFriends: true);
                }
                catch (LauncherException save)
                {
                    throw ApiException.Refused(save.Message);
                }
                Note($"{s.Name} had expired on the server; registered it again (new PIN)");
                id = (id.Server, id.Name, pin);
            }
            presence = await api.PresenceAsync(id.Name, id.Pin, cancel).ConfigureAwait(false);
        }
        lock (_gate)
        {
            _presence = (id, presence.Token);
        }
        ConsentFromServer(id.Server, presence);
        return presence.Token;
    }

    /// <summary>Leaving: friends see this launcher offline at once.</summary>
    private async Task SignOutAsync()
    {
        ((string Server, string Name, string Pin) Who, string Token)? was;
        lock (_gate)
        {
            was = _presence;
            _presence = null;
        }
        if (was is { } left)
        {
            await Api(left.Who.Server).PresenceEndAsync(left.Token).ConfigureAwait(false);
        }
    }

    // ---- friends --------------------------------------------------------------------------

    private void FriendsProblem(ApiException e)
    {
        ConsentRefused(e);
        Mutate(() => _friendsNote = (e.Kind == ApiError.Transient ? Strings.CantReachNow : e.Message, true));
    }

    /// <summary>
    /// Keeps the friends list current while the launcher runs (window, tray or headless): a
    /// long-poll that returns on any change, cut short by any settings save or friend action.
    /// Holds no seat.
    /// </summary>
    private async Task FriendsLoopAsync(CancellationToken life)
    {
        ((string Server, string Name, string Pin) Who, long Cursor)? cursor = null;
        while (!life.IsCancellationRequested)
        {
            try
            {
                if (Ident(Settings) is not { } id)
                {
                    Mutate(() => (_friendList, _friendWatch) = (null, new FriendWatch()));
                    cursor = null;
                    await _friendsWake.WaitAsync(TimeSpan.FromSeconds(5), life).ConfigureAwait(false);
                    continue;
                }
                if (cursor is { } old && old.Who != id)
                {
                    cursor = null;
                    Mutate(() => (_friendList, _friendWatch) = (null, new FriendWatch()));
                }
                string token;
                try
                {
                    token = await PresenceTokenAsync(life).ConfigureAwait(false);
                }
                catch (ApiException e)
                {
                    FriendsProblem(e);
                    var wait = e.Kind switch
                    {
                        ApiError.Limited => Math.Min(e.Seconds, 900),
                        ApiError.Refused => 60,
                        _ => 5,
                    };
                    await _friendsWake.WaitAsync(TimeSpan.FromSeconds(wait), life).ConfigureAwait(false);
                    continue;
                }
                MaybeAskForReport(id);
                var (woken, list, error) = await FriendsOrWakeAsync(token, cursor?.Cursor, life).ConfigureAwait(false);
                if (woken)
                {
                    cursor = null; // something changed here: fetch now
                }
                else if (list is not null)
                {
                    cursor = (id, list.Cursor);
                    var toasts = new List<Toast>();
                    Mutate(() =>
                    {
                        if (_friendsNote is { IsError: true } && !_friendsBusy)
                        {
                            _friendsNote = null; // a problem that went away
                        }
                        if (_friendList?.Cursor != list.Cursor)
                        {
                            toasts.AddRange(_friendWatch.Diff(list, _config.Settings.Background, _clock, _clock.GetTimestamp()).Select(e => FriendWatch.ToastFor(e.Kind, e.Name)));
                        }
                        _friendList = list;
                    });
                    foreach (var toast in toasts)
                    {
                        Toast(toast);
                    }
                }
                else if (error is { Kind: ApiError.SignedOut })
                {
                    lock (_gate)
                    {
                        _presence = null;
                    }
                    cursor = null;
                }
                else if (error is not null)
                {
                    FriendsProblem(error);
                    cursor = null;
                    await Task.Delay(TimeSpan.FromSeconds(error.Kind == ApiError.Limited ? Math.Min(error.Seconds, 900) : 5), life).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (life.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>The friends long-poll, or a wake-up, whichever comes first (the other is cancelled).</summary>
    private async Task<(bool Woken, FriendList? List, ApiException? Error)> FriendsOrWakeAsync(string token, long? since, CancellationToken life)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(life);
        var call = Api().FriendsAsync(token, since, either.Token);
        var wake = _friendsWake.WaitAsync(Timeout.InfiniteTimeSpan, either.Token);
        await Task.WhenAny(call, wake).ConfigureAwait(false);
        await either.CancelAsync().ConfigureAwait(false);
        var woken = false;
        try
        {
            woken = await wake.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the poll answered first
        }
        try
        {
            var list = await call.ConfigureAwait(false);
            return woken ? (true, null, null) : (false, list, null);
        }
        catch (ApiException e)
        {
            return woken ? (true, null, null) : (false, null, e);
        }
        catch (OperationCanceledException)
        {
            life.ThrowIfCancellationRequested();
            return (true, null, null);
        }
    }

    /// <summary>One friends action (add, accept, reject/cancel/remove), then refresh.</summary>
    private async Task FriendActionAsync(Func<SeatApi, string, Task<string>> run)
    {
        Mutate(() => _friendsBusy = true);
        (string Text, bool IsError) note;
        try
        {
            var token = await PresenceTokenAsync(_life.Token).ConfigureAwait(false);
            note = (await run(Api(), token).ConfigureAwait(false), false);
        }
        catch (ApiException e) when (e.Kind == ApiError.SignedOut)
        {
            lock (_gate)
            {
                _presence = null;
            }
            note = (Strings.CantReachNow, true);
        }
        catch (ApiException e)
        {
            ConsentRefused(e);
            note = (e.Kind == ApiError.Transient ? Strings.CantReachNow : e.Message, true);
        }
        catch (OperationCanceledException)
        {
            note = (Strings.CantReachNow, true);
        }
        Mutate(() => (_friendsBusy, _friendsNote) = (false, note));
        _friendsWake.Set();
    }

    public Task AddFriendAsync(string name) => FriendActionAsync(async (api, token) =>
    {
        var (who, state) = await api.FriendRequestAsync(token, name, _life.Token).ConfigureAwait(false);
        return state == "accepted" ? Strings.FriendsNow(who) : Strings.RequestSent(who);
    });

    public Task AcceptFriendAsync(string name) => FriendActionAsync(async (api, token) =>
    {
        await api.FriendAcceptAsync(token, name, _life.Token).ConfigureAwait(false);
        return Strings.FriendsNow(name);
    });

    /// <summary>Reject a request, cancel yours, or remove a friend.</summary>
    public Task RemoveFriendAsync(string name, FriendRemoval why) => FriendActionAsync(async (api, token) =>
    {
        await api.FriendRemoveAsync(token, name, _life.Token).ConfigureAwait(false);
        return why switch
        {
            FriendRemoval.Reject => Strings.RequestDeclined(name),
            FriendRemoval.Cancel => Strings.RequestCancelled(name),
            _ => Strings.FriendRemoved(name),
        };
    });

    /// <summary>An unmute from the window (mutes themselves come from the game's menus).</summary>
    public Task UnmuteAsync(string account) => Task.Run(() =>
    {
        try
        {
            EditConfig(c => c with { Mutes = LocalApi.ApplyMute(c.Mutes, account, "", muted: false) }, wakeFriends: false);
            Mutate(() => _saveError = null);
        }
        catch (LauncherException e)
        {
            Mutate(() => _saveError = e.Message);
        }
    });

    // ---- reports --------------------------------------------------------------------------

    /// <summary>Once per sign-in: is a report of this profile waiting for review? (The Rust window asked when SETTINGS opened.)</summary>
    private void MaybeAskForReport((string Server, string Name, string Pin) id)
    {
        lock (_gate)
        {
            if (_reportAskedFor == id)
            {
                return;
            }
            _reportAskedFor = id;
        }
        _ = RefreshReportAsync();
    }

    /// <summary>Asks the server for this profile's open report.</summary>
    public Task RefreshReportAsync() => ReportActionAsync(async (api, token) => (true, await api.ReportAsync(token, _life.Token).ConfigureAwait(false), null));

    /// <summary>Reports <paramref name="name"/>'s voice chat.</summary>
    public Task SendReportAsync(string name, string reason, string? details) => ReportActionAsync(async (api, token) =>
    {
        var report = await api.ReportFileAsync(token, name, reason, details ?? "", _life.Token).ConfigureAwait(false);
        return (true, report, Strings.ReportSent(report.Reported));
    });

    /// <summary>Takes back the report waiting for review.</summary>
    public Task WithdrawReportAsync() => ReportActionAsync(async (api, token) =>
    {
        await api.ReportWithdrawAsync(token, _life.Token).ConfigureAwait(false);
        return (true, null, Strings.ReportWithdrawn);
    });

    /// <summary>One report call; <paramref name="run"/> answers the open report (when it knows) and a note.</summary>
    private async Task ReportActionAsync(Func<SeatApi, string, Task<(bool Known, OpenReport? Open, string? Note)>> run)
    {
        Mutate(() =>
        {
            _reportBusy = true;
            _reportLoaded ??= false;
        });
        (bool Known, OpenReport? Open, string? Note) outcome = default;
        string? error = null;
        try
        {
            var token = await PresenceTokenAsync(_life.Token).ConfigureAwait(false);
            outcome = await run(Api(), token).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.Kind == ApiError.SignedOut)
        {
            lock (_gate)
            {
                _presence = null;
            }
            error = Strings.CantReachNow;
        }
        catch (ApiException e)
        {
            ConsentRefused(e);
            error = e.Kind == ApiError.Transient ? Strings.CantReachNow : e.Message;
        }
        catch (OperationCanceledException)
        {
            error = Strings.CantReachNow;
        }
        Mutate(() =>
        {
            _reportBusy = false;
            if (error is not null)
            {
                _reportNote = (error, true);
                return;
            }
            if (outcome.Known)
            {
                (_reportLoaded, _openReport) = (true, outcome.Open);
            }
            _reportNote = outcome.Note is null ? null : (outcome.Note, false);
        });
    }
}

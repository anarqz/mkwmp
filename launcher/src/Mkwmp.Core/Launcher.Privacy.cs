// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Security.Cryptography;
using System.Text;
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// The consent (18+, the privacy policy), kept by the server the player signed in on
/// (docs/contracts/SEAT_API.md, "Privacy and consent"). The order is: choose a server, sign in
/// (register, or recover with the PIN), then consent to that server's own policy
/// (<c>GET /privacy</c>, Markdown, shown inside the launcher; its sha256 is the version consented
/// to). A sign-in says what the server holds (<c>privacyAccepted</c>), and
/// <see cref="LauncherSettings.AdultConsent"/> follows it; accepting or withdrawing is
/// <c>PUT /seat/v1/privacy</c>. Until accepted, friends, reports and a seat are refused, by the
/// launcher and by the server (<c>PRIVACY_REQUIRED</c>).
/// <para>
/// A server older than this (no <c>/privacy</c>, no <c>privacyAccepted</c>) keeps working as
/// before: the launcher's own <c>adult_consent</c> decides. The launcher has no policy of its own, so
/// for such a server it says the server publishes none (<see cref="Strings.PrivacyNone"/>).
/// </para>
/// </summary>
public sealed partial class Launcher
{
    /// <summary>The policy last shown, per server (under <see cref="_gate"/>): its version goes with the consent.</summary>
    private (string Server, string Version)? _shownPolicy;

    /// <summary>What the last sign-in said: the server keeps consent (true), does not (false), or was not asked yet (null). Under <see cref="_gate"/>.</summary>
    private bool? _serverKeepsConsent;

    /// <summary>The server refuses seats, friends and reports without the consent (else it only records it). Under <see cref="_gate"/>.</summary>
    private bool _privacyEnforced;

    /// <summary>The sha256 (lower-case hex) of a policy's text as the server sent it: the version a consent names (the server's quoted ETag).</summary>
    internal static string PrivacyVersion(string markdown) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(markdown)));

    /// <summary>
    /// The server refused something for want of the consent (<c>403 PRIVACY_REQUIRED</c>: its policy
    /// changed, or the consent was withdrawn elsewhere): the launcher's setting follows, so the
    /// consent page is what the player meets next.
    /// </summary>
    private void ConsentRefused(ApiException e)
    {
        if (e.Kind != ApiError.Refused || e.Message != Strings.NoConsent || !Settings.AdultConsent)
        {
            return;
        }
        try
        {
            EditConfig(c => c with { Settings = c.Settings with { AdultConsent = false } }, wakeFriends: false);
            lock (_gate)
            {
                _shownPolicy = null; // another version now: fetched again before the next consent
            }
            Note("the server asks for the consent to its privacy policy again");
        }
        catch (LauncherException save)
        {
            Note(save.Message);
        }
    }

    /// <summary>A sign-in's answer: the launcher's setting follows what the server holds (a server that holds nothing leaves it alone).</summary>
    private void ConsentFromServer(string server, Presence presence)
    {
        lock (_gate)
        {
            _serverKeepsConsent = presence.PrivacyAccepted is not null;
            _privacyEnforced = presence.PrivacyEnforced;
        }
        if (presence.PrivacyAccepted is not { } accepted || accepted == Settings.AdultConsent || !ClientConfig.SameServer(server, Settings.Server))
        {
            return;
        }
        try
        {
            // Not ConsentChanged: the sign-in that told us stays.
            EditConfig(c => c with { Settings = c.Settings with { AdultConsent = accepted } }, wakeFriends: false);
            Note(accepted ? "the server has your consent to its privacy policy" : "the server has no consent for its current privacy policy: it is asked for again");
        }
        catch (LauncherException e)
        {
            Note(e.Message);
        }
    }

    public async Task<PrivacyPolicy> GetPrivacyPolicyAsync()
    {
        var s = Settings;
        if (!IsOnline(s))
        {
            return PrivacyPolicy.None(Strings.PrivacyNoServer);
        }
        try
        {
            if (await Api(s.Server).PrivacyTextAsync(_life.Token).ConfigureAwait(false) is not var (markdown, version))
            {
                return PrivacyPolicy.None(Strings.PrivacyNone); // a server without its own policy (before 1.4.0): nothing stands in for it
            }
            lock (_gate)
            {
                _shownPolicy = (s.Server, version);
            }
            return new PrivacyPolicy(markdown, version, true, ClientConfig.ServerBase(s.Server) + "/privacy", null);
        }
        catch (Exception e) when (e is ApiException or OperationCanceledException)
        {
            Note($"privacy policy: {e.Message}");
            return PrivacyPolicy.None(Strings.PrivacyUnreachable);
        }
    }

    /// <summary>
    /// Gives or withdraws the consent. Signed in on a server that keeps it: stored there first
    /// (with the version of the policy that was shown; fetched now when none was), and only then
    /// here. Any other case (no profile yet, an older server): here alone, as before. A server
    /// that should have stored it and could not leaves everything as it was, with the reason in
    /// <see cref="ProfileView.Message"/>.
    /// </summary>
    public async Task SetConsentAsync(bool adult)
    {
        var s = Settings;
        if (IsOnline(s) && Ident(s) is { } id)
        {
            try
            {
                var token = await SignInAsync(s, id, _life.Token).ConfigureAwait(false);
                bool? keeps;
                string? version;
                lock (_gate)
                {
                    keeps = _serverKeepsConsent;
                    version = _shownPolicy is { } shown && ClientConfig.SameServer(shown.Server, s.Server) ? shown.Version : null;
                }
                if (keeps != false)
                {
                    if (adult && version is null)
                    {
                        version = (await GetPrivacyPolicyAsync().ConfigureAwait(false)).Version;
                    }
                    var stored = await Api(s.Server).PrivacyPutAsync(token, adult, version, _life.Token).ConfigureAwait(false);
                    Note(stored ? (adult ? "consent stored on the server" : "consent withdrawn on the server") : "this server keeps no consent: kept here");
                }
            }
            catch (Exception e) when (e is ApiException or OperationCanceledException)
            {
                Note($"consent: {e.Message}");
                var changed = e is ApiException && e.Message == Strings.PrivacyChanged;
                bool enforced;
                lock (_gate)
                {
                    enforced = _privacyEnforced;
                    if (changed)
                    {
                        _shownPolicy = null; // the next accept names the version that is current now (the page shows it again)
                    }
                }
                // A server that only records the consent does not get to block it: kept here, stored there at a later try.
                // One that enforces it, or whose policy changed under the player, leaves everything as it was.
                if (!adult || enforced || changed)
                {
                    var why = e is ApiException { Kind: not ApiError.Transient } api ? api.Message : Strings.ConsentNotStored;
                    Mutate(() => (_register, _registerMessage) = (RegisterState.Failed, why));
                    return;
                }
            }
        }
        Mutate(() =>
        {
            if (_register == RegisterState.Failed)
            {
                (_register, _registerMessage) = (RegisterState.Idle, null);
            }
        });
        await SaveSettingsAsync(Settings with { AdultConsent = adult }).ConfigureAwait(false);
    }
}

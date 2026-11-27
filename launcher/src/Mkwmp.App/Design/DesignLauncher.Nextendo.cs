// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core;

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
}

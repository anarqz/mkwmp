// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.Design;

/// <summary>
/// The demo's Nextendo account: nothing leaves the PC and no browser opens. <c>--demo-nextendo</c> starts linked and
/// playing on Nextendo Network, <c>--demo-nextendo-linked</c> linked and playing on MKWMP servers; "Link" links a
/// made-up account after a moment.
/// </summary>
public sealed partial class DesignLauncher
{
    private bool _nextendoLinked;
    private bool _nextendoLinking;
    private PlayNetwork _playOn;
    private (string Text, bool IsError)? _nextendoNote;
    private CancellationTokenSource? _linking;

    /// <summary>PLAY goes to Nextendo: no MKWMP server is "contacted" in the demo either.</summary>
    private bool NextendoActive => _nextendoLinked && _playOn == PlayNetwork.Nextendo && !_offlineOnly;

    public NextendoView Nextendo => new(
        _nextendoLinked, _nextendoLinked ? "Alex" : "", _nextendoLinked ? "SW-4821-9130-5567" : "", _nextendoLinked ? _playOn : PlayNetwork.Mkwmp, NextendoActive,
        _nextendoLinking, false, NextendoActive ? CoreStrings.NextendoAllowed : "", NextendoActive ? "good" : "muted", _nextendoNote?.Text, _nextendoNote?.IsError ?? false);

    /// <summary>For screenshots: a linked account, playing on Nextendo or on MKWMP servers.</summary>
    public void PretendNextendo(bool playing) => Change(() =>
    {
        (_nextendoLinked, _playOn) = (true, playing ? PlayNetwork.Nextendo : PlayNetwork.Mkwmp);
        Sync();
    });

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
                (_nextendoLinking, _nextendoLinked, _nextendoNote) = (false, true, (CoreStrings.NextendoLinked("Alex"), false));
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
            (_nextendoLinked, _playOn, _nextendoNote) = (false, PlayNetwork.Mkwmp, (CoreStrings.NextendoUnlinked, false));
            Sync();
        });
        return Task.CompletedTask;
    }

    public Task SetPlayOnAsync(PlayNetwork network)
    {
        if (Session.Busy || (network == PlayNetwork.Nextendo && !_nextendoLinked))
        {
            Change(() => _nextendoNote = (Session.Busy ? CoreStrings.PressStopFirst : CoreStrings.NextendoNotLinked, true));
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

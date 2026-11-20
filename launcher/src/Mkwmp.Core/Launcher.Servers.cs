// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// The servers to choose from (Settings › Online, and the welcome's "Choose a server"): the
/// beholder's list (<see cref="BeholderClient"/>), each with its latency measured to the server
/// itself, sorted by it. Nothing here runs by itself: the window asks while its page is open.
/// Without a beholder (the release default is still empty) the list is off and only a typed
/// address connects.
/// </summary>
public sealed partial class Launcher
{
    private readonly SemaphoreSlim _serverListWork = new(1, 1);
    private volatile ServerListView _serverListView = ServerListView.Off;

    public ServerListView Servers => _serverListView;

    /// <summary>The beholder to ask: the setting, else the release default; null when there is none or it may not be asked (not https).</summary>
    private Uri? Beholder => BeholderClient.Allowed(Settings.Beholder.Length > 0 ? Settings.Beholder : _options.Beholder ?? BeholderClient.DefaultUrl);

    private void SetServers(ServerListView view)
    {
        _serverListView = view;
        RaiseChanged();
    }

    public async Task RefreshServersAsync()
    {
        if (Beholder is not { } beholder)
        {
            SetServers(ServerListView.Off);
            return;
        }
        if (!await _serverListWork.WaitAsync(0).ConfigureAwait(false))
        {
            return; // one at a time: the view shows the one that runs
        }
        try
        {
            var before = _serverListView;
            SetServers(before with { State = before.Items.Count > 0 ? before.State : ServerListState.Loading, Busy = true, Note = null });
            IReadOnlyList<Listing> listed;
            var state = ServerListState.Ready;
            string? note = null;
            try
            {
                listed = await BeholderClient.ListAsync(_http, beholder, _userAgent, _life.Token).ConfigureAwait(false);
                try
                {
                    BeholderClient.Save(LauncherFolder, listed);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // a read-only folder only loses the fallback
                }
            }
            catch (ApiException e)
            {
                Note($"server list: {e.Message}");
                // The last good list, said to be that; latencies are still measured now.
                listed = BeholderClient.Load(LauncherFolder) ?? [];
                (state, note) = (listed.Count > 0 ? ServerListState.Cached : ServerListState.Failed, Strings.BeholderUnreachable);
            }
            // What was measured before stays on the rows until the new measure arrives.
            var known = before.Items.ToDictionary(i => i.Address, StringComparer.OrdinalIgnoreCase);
            var items = listed.Select(l => known.TryGetValue(l.Endpoint, out var old)
                ? old with { Name = l.Name, Region = l.Region, Version = l.Version, Capacity = l.Capacity, Seated = l.Seated, Queued = l.Queued }
                : new ListedServer(l.Name, l.Endpoint, l.Region, l.Version, l.Capacity, l.Seated, l.Queued, null, null)).ToList();
            SetServers(new ServerListView(state, Sorted(items), true, note));
            await MeasureAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the launcher is closing
        }
        finally
        {
            SetServers(_serverListView with { Busy = false });
            _serverListWork.Release();
        }
    }

    public async Task MeasureServersAsync()
    {
        if (_serverListView.Items.Count == 0 || !await _serverListWork.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            SetServers(_serverListView with { Busy = true });
            await MeasureAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the launcher is closing
        }
        finally
        {
            SetServers(_serverListView with { Busy = false });
            _serverListWork.Release();
        }
    }

    /// <summary>Times every listed server, a few at once; each row is updated as its answer arrives (and the rows re-sorted).</summary>
    private async Task MeasureAsync()
    {
        using var slots = new SemaphoreSlim(BeholderClient.Parallel);
        var update = new Lock();
        await Task.WhenAll(_serverListView.Items.Select(async item =>
        {
            await slots.WaitAsync(_life.Token).ConfigureAwait(false);
            try
            {
                var ping = await BeholderClient.PingAsync(_http, item.Address, _userAgent, _clock, _life.Token).ConfigureAwait(false);
                lock (update)
                {
                    var view = _serverListView;
                    var items = view.Items.Select(i => !ReferenceEquals(i, item) && i.Address != item.Address ? i
                        : ping is { } p
                            ? i with { LatencyMs = p.Milliseconds, Online = true, Capacity = p.Status.SeatsTotal, Seated = p.Status.Seated, Queued = p.Status.Queued }
                            : i with { LatencyMs = null, Online = false }).ToList();
                    SetServers(view with { Items = Sorted(items) });
                }
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
    }

    /// <summary>By latency; the ones not measured yet after them, the ones that did not answer last; then by name.</summary>
    private static IReadOnlyList<ListedServer> Sorted(IEnumerable<ListedServer> items) =>
        [.. items.OrderBy(i => i.Online switch { true => 0, null => 1, _ => 2 }).ThenBy(i => i.LatencyMs ?? int.MaxValue).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
}

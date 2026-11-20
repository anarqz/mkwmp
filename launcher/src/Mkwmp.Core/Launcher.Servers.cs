// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Net;

namespace Mkwmp.Core;

/// <summary>
/// The servers to choose from (Settings › Online, and the welcome's "Choose a server"): the
/// beholder's list (<see cref="BeholderClient"/>), each with its latency measured to the server
/// itself, nearest first; the window shows the first <see cref="ServerListView.PageSize"/>.
/// Nothing here runs by itself: the window asks while its page is open. Without a beholder the
/// list is off and only a typed address connects.
/// <para>
/// A list may hold hundreds of servers (the beholder's bound is 500): only the first
/// <see cref="ServerListView.MeasuredAtMost"/> of them, in the beholder's order and not counting
/// the hidden ones, are contacted, plus every server the player has a profile on (they are the
/// ones worth a number whatever their place in the list). <see cref="BeholderClient.Parallel"/> at
/// once, <see cref="BeholderClient.PingTimeout"/> each. The others are listed after the measured
/// ones, without a number.
/// </para>
/// <para>
/// A hidden server (<see cref="HideServerAsync"/>, client.toml's <c>[[server_list.hidden]]</c>) is
/// neither listed nor contacted; the next one takes its place.
/// </para>
/// </summary>
public sealed partial class Launcher
{
    private readonly SemaphoreSlim _serverListWork = new(1, 1);
    private readonly Lock _serverListLock = new();
    private volatile ServerListView _serverListView = ServerListView.Off;

    /// <summary>Everything the beholder listed, in its order, hidden ones included, each with what was measured of it.</summary>
    private IReadOnlyList<ListedServer> _serverListAll = [];

    public ServerListView Servers
    {
        get
        {
            var view = _serverListView;
            var hidden = Config.HiddenServers;
            return ReferenceEquals(view.Hidden, hidden) ? view : view with { Hidden = hidden };
        }
    }

    /// <summary>The beholder to ask: the setting, else the release default; null when there is none or it may not be asked (not https).</summary>
    private Uri? Beholder => BeholderClient.Allowed(Settings.Beholder.Length > 0 ? Settings.Beholder : _options.Beholder ?? BeholderClient.DefaultUrl);

    private void SetServers(ServerListView view)
    {
        _serverListView = view with { Hidden = Config.HiddenServers };
        RaiseChanged();
    }

    /// <summary>The view again from <see cref="_serverListAll"/>: without the hidden ones, nearest first.</summary>
    private void ShowServers(Func<ServerListView, ServerListView>? change = null)
    {
        lock (_serverListLock)
        {
            var view = _serverListView with { Items = ServerRanking.Visible(_serverListAll, Config.HiddenServers) };
            SetServers(change is null ? view : change(view));
        }
    }

    public async Task RefreshServersAsync()
    {
        if (Beholder is not { } beholder)
        {
            lock (_serverListLock)
            {
                _serverListAll = [];
                SetServers(ServerListView.Off);
            }
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
            lock (_serverListLock)
            {
                // What was measured before stays on the rows until the new measure arrives.
                var known = _serverListAll.ToDictionary(i => i.Address, StringComparer.OrdinalIgnoreCase);
                _serverListAll = [.. listed.Select(l => known.TryGetValue(l.Endpoint, out var old)
                    ? old with { Name = l.Name, Region = l.Region, Version = l.Version, Capacity = l.Capacity, Seated = l.Seated, Queued = l.Queued, PublicKey = l.PublicKey }
                    : new ListedServer(l.Name, l.Endpoint, l.Region, l.Version, l.Capacity, l.Seated, l.Queued, null, null) { PublicKey = l.PublicKey })];
                ShowServers(view => view with { State = state, Busy = true, Note = note });
            }
            await MeasureAsync(onlyNew: false).ConfigureAwait(false);
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

    public Task MeasureServersAsync() => MeasureServersAsync(onlyNew: false);

    private async Task MeasureServersAsync(bool onlyNew)
    {
        if (_serverListView.Items.Count == 0 || !await _serverListWork.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            SetServers(_serverListView with { Busy = true });
            await MeasureAsync(onlyNew).ConfigureAwait(false);
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

    /// <summary>
    /// Times the servers worth a number (<see cref="ServerRanking.ToMeasure"/>), a few at once; each row is updated as
    /// its answer arrives (and the rows ranked again). <paramref name="onlyNew"/>: only the ones never measured (a
    /// server that came into the measured set because another was hidden).
    /// </summary>
    private async Task MeasureAsync(bool onlyNew)
    {
        IReadOnlyList<ListedServer> todo;
        lock (_serverListLock)
        {
            var config = Config;
            todo = ServerRanking.ToMeasure(_serverListAll, config.HiddenServers, [.. config.Servers.Select(p => p.Server), config.Settings.Server]);
        }
        if (onlyNew)
        {
            todo = [.. todo.Where(i => i.Online is null)];
        }
        using var slots = new SemaphoreSlim(BeholderClient.Parallel);
        await Task.WhenAll(todo.Select(async item =>
        {
            await slots.WaitAsync(_life.Token).ConfigureAwait(false);
            try
            {
                var ping = await BeholderClient.PingAsync(_http, item.Address, _userAgent, _clock, _life.Token).ConfigureAwait(false);
                lock (_serverListLock)
                {
                    _serverListAll = [.. _serverListAll.Select(i => i.Address != item.Address ? i
                        : ping is { } p
                            ? i with { LatencyMs = p.Milliseconds, Online = true, Capacity = p.Status.SeatsTotal, Seated = p.Status.Seated, Queued = p.Status.Queued }
                            : i with { LatencyMs = null, Online = false })];
                    ShowServers();
                }
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
    }

    public async Task<bool> HideServerAsync(ListedServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Id.Length == 0 || (IsOnline(Settings) && ClientConfig.SameServer(server.Address, Settings.Server)))
        {
            return false; // the server in use stays in sight: disconnect first
        }
        var entry = new HiddenServer(server.Id, server.Name, server.Address);
        if (!await EditHiddenAsync(hidden => [.. hidden.Where(h => h.Id != entry.Id).TakeLast(ClientConfig.MaxHidden - 1), entry]).ConfigureAwait(false))
        {
            return false;
        }
        // The one that took its place in the measured set has no number yet.
        await MeasureServersAsync(onlyNew: true).ConfigureAwait(false);
        return true;
    }

    public async Task ShowServerAsync(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (await EditHiddenAsync(hidden => [.. hidden.Where(h => h.Id != id)]).ConfigureAwait(false))
        {
            await MeasureServersAsync(onlyNew: true).ConfigureAwait(false);
        }
    }

    /// <summary>Saves the hidden list (off the caller's thread) and shows the list without them; false when client.toml could not be written.</summary>
    private Task<bool> EditHiddenAsync(Func<IReadOnlyList<HiddenServer>, IReadOnlyList<HiddenServer>> edit) => Task.Run(() =>
    {
        try
        {
            EditConfig(c => c with { HiddenServers = edit(c.HiddenServers) }, wakeFriends: false);
        }
        catch (LauncherException e)
        {
            Note($"server list: {e.Message}");
            SetServers(_serverListView with { Note = e.Message });
            return false;
        }
        ShowServers();
        return true;
    });
}

/// <summary>Which listed servers are shown and in what order, and which are measured. No state: the rules of <see cref="Launcher.Servers"/>.</summary>
internal static class ServerRanking
{
    public static bool IsHidden(ListedServer server, IReadOnlyList<HiddenServer> hidden) => hidden.Any(h => h.Is(server));

    /// <summary>
    /// The servers not hidden, nearest first: the ones that answered by latency, then the ones not measured, then the
    /// ones that did not answer; ties by name. So the first five are the five nearest that answered, and the others
    /// fill in only when fewer than five did.
    /// </summary>
    public static IReadOnlyList<ListedServer> Visible(IEnumerable<ListedServer> listed, IReadOnlyList<HiddenServer> hidden) =>
        [.. listed.Where(i => !IsHidden(i, hidden))
            .OrderBy(i => i.Online switch { true => 0, null => 1, _ => 2 })
            .ThenBy(i => i.LatencyMs ?? int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The servers to contact: the first <see cref="ServerListView.MeasuredAtMost"/> of the list (the beholder's
    /// order) that are not hidden, and every listed one among <paramref name="used"/> (the addresses the player has a
    /// profile on, and the one in use), wherever it is in the list. Never a hidden one.
    /// </summary>
    public static IReadOnlyList<ListedServer> ToMeasure(IReadOnlyList<ListedServer> listed, IReadOnlyList<HiddenServer> hidden, IReadOnlyList<string> used, int atMost = ServerListView.MeasuredAtMost)
    {
        var shown = listed.Where(i => !IsHidden(i, hidden)).ToList();
        return [.. shown.Take(atMost), .. shown.Skip(atMost).Where(i => used.Any(u => u.Length > 0 && ClientConfig.SameServer(u, i.Address)))];
    }
}

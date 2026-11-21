// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using Mkwmp.App.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>One server of the list, as its row shows it.</summary>
public sealed class ServerRow : ObservableObject
{
    private ListedServer _server;
    private readonly Strings _s;

    internal ServerRow(ListedServer server, Strings s, Action<ListedServer> pick, Action<ServerRow> menu)
    {
        _server = server;
        _s = s;
        PickCommand = new RelayCommand(() => pick(_server));
        MenuCommand = new RelayCommand(() => menu(this));
    }

    internal ListedServer Server => _server;

    public string Address => _server.Address;

    public string Name => _server.Name;

    /// <summary>"sa-east · v1.4.0 · mkw.example.net".</summary>
    public string Detail => string.Join(" · ", new[] { _server.Region, _server.Version is { Length: > 0 } v ? "v" + v : null, ProfileForm.HostOf(_server.Address) }
        .Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>"12/32".</summary>
    public string Players => _server.Capacity > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{_server.Seated}/{_server.Capacity}")
        : "";

    public string Queue => _server.Queued > 0 ? _s.Format("servers.queued", ("n", _server.Queued.ToString(CultureInfo.CurrentCulture))) : _s["servers.players"];

    public int Bars => _server.Bars;

    /// <summary>"23 ms", "no answer", or "…" while it is not measured (yet, or at all: a long list measures its first ones).</summary>
    public string Latency => _server.Online switch
    {
        true when _server.LatencyMs is { } ms => string.Create(CultureInfo.InvariantCulture, $"{ms} ms"),
        false => _s["servers.no_answer"],
        _ => "…",
    };

    public bool Answers => _server.Online != false;

    public ICommand PickCommand { get; }

    /// <summary>The row's "…" (the mouse and touch; a controller has Y): what else can be done with this server.</summary>
    public ICommand MenuCommand { get; }

    /// <summary>What the "…" button is called (its tooltip and its name for a screen reader).</summary>
    public string MenuLabel => _s["servers.hide"];

    internal void Update(ListedServer server)
    {
        if (!Equals(_server, server))
        {
            _server = server;
            RaiseAll();
        }
    }
}

/// <summary>
/// The servers to choose from, for Settings › Online and the welcome's "Choose a Server" (view:
/// Views/Settings/ServerList.xaml): the beholder's list with each server's latency (measured to
/// the server itself), the <see cref="ServerListView.PageSize"/> nearest first and that many more
/// with each "Show more"; a refresh button, and a new measure every few seconds while its page is
/// open (<see cref="Start"/> / <see cref="Stop"/>). Rows are updated in place, so the focused one
/// stays focused while the numbers move. Y on a row (or its "…") hides that server on this PC:
/// the next one takes its place (<see cref="ILauncher.HideServerAsync"/>).
/// </summary>
public sealed class ServerPicker : ObservableObject
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    private readonly ShellViewModel _shell;
    private readonly Action<ListedServer> _pick;
    private readonly DispatcherTimer _timer;
    private int _limit = ServerListView.PageSize;
    private ServerRow? _focused;

    public ServerPicker(ShellViewModel shell, Action<ListedServer> pick)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _pick = pick ?? throw new ArgumentNullException(nameof(pick));
        RefreshCommand = RelayCommand.Async(() => Launcher.RefreshServersAsync(), () => HasList && !Busy);
        MoreCommand = new RelayCommand(ShowMore, () => HasMore);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Every };
        _timer.Tick += (_, _) => RelayCommand.Async(() => Launcher.MeasureServersAsync()).Execute(null);
        Sync();
    }

    public Strings S => _shell.S;

    private ILauncher Launcher => _shell.Launcher;

    /// <summary>The rows on screen: the nearest <see cref="Shown"/> of the list.</summary>
    public ObservableCollection<ServerRow> Rows { get; } = [];

    /// <summary>A beholder is set: there is a list to show (it may be empty or still loading).</summary>
    public bool HasList => Launcher.Servers.State != ServerListState.Off;

    public bool HasRows => Rows.Count > 0;

    public bool Busy => Launcher.Servers.Busy;

    /// <summary>How many servers the list may show now (five, and five more with each "Show more").</summary>
    public int Shown => _limit;

    /// <summary>Servers of the list that are not on screen.</summary>
    public int Remaining => Math.Max(0, Launcher.Servers.Items.Count - _limit);

    public bool HasMore => Remaining > 0;

    /// <summary>"12 more servers".</summary>
    public string MoreCaption => HasMore ? S.Format("servers.more_count", ("n", Remaining.ToString(CultureInfo.CurrentCulture))) : "";

    /// <summary>Five more rows; the focus stays on "Show more" (or goes to the last row when that was the end).</summary>
    public ICommand MoreCommand { get; }

    /// <summary>What stands in for the rows: loading, nobody listed, or why the list could not be read.</summary>
    public string Empty => Rows.Count > 0 ? ""
        : Launcher.Servers.State switch
        {
            ServerListState.Loading => S["servers.loading"],
            ServerListState.Failed => Launcher.Servers.Note ?? S["servers.none"],
            _ => Busy ? S["servers.loading"]
                : Launcher.Servers.Hidden.Count > 0 ? S["servers.all_hidden"]
                : S["servers.none"],
        };

    /// <summary>The picture over <see cref="Empty"/>: offline when the list could not be read, an empty sky when nobody is
    /// listed; none while it loads or with rows.</summary>
    public string? EmptyArt => Rows.Count > 0 || Busy || Launcher.Servers.State == ServerListState.Loading ? null
        : Launcher.Servers.State == ServerListState.Failed ? "pack://application:,,,/Assets/Art/empty-offline.png"
        : "pack://application:,,,/Assets/Art/empty-servers.png";

    /// <summary>"The list could not be read right now" over a cached list.</summary>
    public string Note => Rows.Count > 0 ? Launcher.Servers.Note ?? "" : "";

    public ICommand RefreshCommand { get; }

    // ── hiding a server: Y on its row, or its "…" ──

    /// <summary>The row under the focus (the view tells); null while the focus is elsewhere.</summary>
    public ServerRow? Focused
    {
        get => _focused;
        set
        {
            if (Set(ref _focused, value))
            {
                HintsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The page shows <see cref="Hint"/> in its hint bar while a row has the focus.</summary>
    public event EventHandler? HintsChanged;

    /// <summary>"Y Hide" while a row has the focus; else null.</summary>
    public Hint? Hint => _focused is not null && Rows.Contains(_focused) ? new Hint(PadButton.Y, S["servers.hide.short"]) : null;

    /// <summary>Y on a row: its menu. True when used (the page then keeps Y from opening the friends panel).</summary>
    public bool OnButton(PadButton button)
    {
        if (button != PadButton.Y || _focused is not { } row || !Rows.Contains(row))
        {
            return false;
        }
        ShowMenu(row);
        return true;
    }

    /// <summary>What can be done with a server besides connecting: "Hide this server" (and why not, for the one in use).</summary>
    private void ShowMenu(ServerRow row)
    {
        var server = row.Server;
        if (Launcher.OnlineEnabled && string.Equals(ProfileForm.HostOf(server.Address), ProfileForm.HostOf(Launcher.Settings.Server), StringComparison.OrdinalIgnoreCase))
        {
            _ = _shell.MessageAsync(server.Name, S["servers.hide.in_use"]);
            return;
        }
        var modal = new ModalViewModel { Title = server.Name, Body = S["servers.hide.body"] };
        modal.Actions.Add(new ModalAction(S["servers.hide"], () =>
        {
            _shell.CloseModal(modal);
            RelayCommand.Async(() => HideAsync(server)).Execute(null);
        }, Controls.ButtonKind.Outline, isDefault: true));
        modal.Bottom = new ModalAction(S["common.cancel"], () => _shell.CloseModal(modal));
        _shell.ShowModal(modal);
    }

    /// <summary>The view puts the focus on the row at this place (the last one when there are fewer): the list changed under it.</summary>
    public event EventHandler<int>? FocusRequested;

    private async Task HideAsync(ListedServer server)
    {
        var at = Math.Max(0, Rows.ToList().FindIndex(r => r.Address == server.Address));
        if (!await Launcher.HideServerAsync(server).ConfigureAwait(true))
        {
            await _shell.MessageAsync(server.Name, Launcher.Servers.Note ?? S["servers.hide.in_use"]).ConfigureAwait(true);
            return;
        }
        Sync();
        FocusRequested?.Invoke(this, at); // the server that took its place
    }

    private void ShowMore()
    {
        var first = Rows.Count;
        _limit += ServerListView.PageSize;
        Sync();
        FocusRequested?.Invoke(this, first); // the first of the new rows
    }

    /// <summary>
    /// The page is open: read the list and measure now, then measure again every 15 s. Always asks
    /// once (the launcher answers "off" when no beholder is set): before, a list that had never been
    /// read counted as no list, so it was never read and only the address box showed.
    /// </summary>
    public void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }
        _timer.Start();
        RelayCommand.Async(() => Launcher.RefreshServersAsync()).Execute(null);
    }

    public void Stop() => _timer.Stop();

    /// <summary>The launcher's list changed: the rows follow, each kept when its server is still among the ones shown.</summary>
    public void Sync()
    {
        var items = Launcher.Servers.Items.Take(_limit).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var at = -1;
            for (var j = i; j < Rows.Count; j++)
            {
                if (Rows[j].Address == items[i].Address)
                {
                    at = j;
                    break;
                }
            }
            if (at < 0)
            {
                Rows.Insert(i, new ServerRow(items[i], S, _pick, ShowMenu));
            }
            else
            {
                if (at != i)
                {
                    Rows.Move(at, i);
                }
                Rows[i].Update(items[i]);
            }
        }
        while (Rows.Count > items.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
        if (_focused is not null && !Rows.Contains(_focused))
        {
            Focused = null; // hidden, or pushed out of the nearest: Y means Friends again
        }
        RaiseAll();
        CommandManager.InvalidateRequerySuggested();
    }
}

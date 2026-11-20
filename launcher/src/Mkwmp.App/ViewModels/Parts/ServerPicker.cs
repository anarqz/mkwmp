// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>One server of the list, as its row shows it.</summary>
public sealed class ServerRow : ObservableObject
{
    private ListedServer _server;
    private readonly Strings _s;

    internal ServerRow(ListedServer server, Strings s, Action<ListedServer> pick)
    {
        _server = server;
        _s = s;
        PickCommand = new RelayCommand(() => pick(_server));
    }

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

    /// <summary>"23 ms", "no answer", or "…" while it is not measured yet.</summary>
    public string Latency => _server.Online switch
    {
        true when _server.LatencyMs is { } ms => string.Create(CultureInfo.InvariantCulture, $"{ms} ms"),
        false => _s["servers.no_answer"],
        _ => "…",
    };

    public bool Answers => _server.Online != false;

    public ICommand PickCommand { get; }

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
/// the server itself), sorted by it, a refresh button, and a new measure every few seconds while
/// its page is open (<see cref="Start"/> / <see cref="Stop"/>). Rows are updated in place, so the
/// focused one stays focused while the numbers move.
/// </summary>
public sealed class ServerPicker : ObservableObject
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    private readonly ShellViewModel _shell;
    private readonly Action<ListedServer> _pick;
    private readonly DispatcherTimer _timer;

    public ServerPicker(ShellViewModel shell, Action<ListedServer> pick)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _pick = pick ?? throw new ArgumentNullException(nameof(pick));
        RefreshCommand = RelayCommand.Async(() => Launcher.RefreshServersAsync(), () => HasList && !Busy);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Every };
        _timer.Tick += (_, _) => RelayCommand.Async(() => Launcher.MeasureServersAsync()).Execute(null);
        Sync();
    }

    public Strings S => _shell.S;

    private ILauncher Launcher => _shell.Launcher;

    public ObservableCollection<ServerRow> Rows { get; } = [];

    /// <summary>A beholder is set: there is a list to show (it may be empty or still loading).</summary>
    public bool HasList => Launcher.Servers.State != ServerListState.Off;

    public bool HasRows => Rows.Count > 0;

    public bool Busy => Launcher.Servers.Busy;

    /// <summary>What stands in for the rows: loading, nobody listed, or why the list could not be read.</summary>
    public string Empty => Rows.Count > 0 ? ""
        : Launcher.Servers.State switch
        {
            ServerListState.Loading => S["servers.loading"],
            ServerListState.Failed => Launcher.Servers.Note ?? S["servers.none"],
            _ => Busy ? S["servers.loading"] : S["servers.none"],
        };

    /// <summary>"The list could not be read right now" over a cached list.</summary>
    public string Note => Rows.Count > 0 ? Launcher.Servers.Note ?? "" : "";

    public ICommand RefreshCommand { get; }

    /// <summary>The page is open: read the list and measure now, then measure again every 15 s.</summary>
    public void Start()
    {
        if (!HasList || _timer.IsEnabled)
        {
            return;
        }
        _timer.Start();
        RelayCommand.Async(() => Launcher.RefreshServersAsync()).Execute(null);
    }

    public void Stop() => _timer.Stop();

    /// <summary>The launcher's list changed: the rows follow, each kept when its server is still listed.</summary>
    public void Sync()
    {
        var items = Launcher.Servers.Items;
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
                Rows.Insert(i, new ServerRow(items[i], S, _pick));
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
        RaiseAll();
        CommandManager.InvalidateRequerySuggested();
    }
}

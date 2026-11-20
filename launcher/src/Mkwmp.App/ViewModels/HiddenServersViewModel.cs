// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>A hidden server, as its row shows it: its name, its address, and "Show again".</summary>
public sealed class HiddenServerRow
{
    internal HiddenServerRow(HiddenServer server, Action<HiddenServer> show)
    {
        Id = server.Id;
        Name = server.Name.Length > 0 ? server.Name : ProfileForm.HostOf(server.Address.Length > 0 ? server.Address : server.Id);
        Detail = ProfileForm.HostOf(server.Address);
        ShowCommand = new RelayCommand(() => show(server));
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>"mkw.example.net".</summary>
    public string Detail { get; }

    public ICommand ShowCommand { get; }
}

/// <summary>
/// Settings › Online › Hidden servers: the servers the player took out of the server list on this
/// PC (Y on a row of the list), each with "Show again". A sub-page; it closes itself when the
/// last one is shown again. The list is local (client.toml): nothing of it reaches a server.
/// </summary>
public sealed class HiddenServersViewModel : PageViewModel
{
    public HiddenServersViewModel(ShellViewModel shell)
        : base(shell) => Sync();

    public ObservableCollection<HiddenServerRow> Rows { get; } = [];

    public string Title => S["servers.hidden.title"];

    public string Caption => S["servers.hidden.caption"];

    public string ShowLabel => S["servers.hidden.show"];

    public override IReadOnlyList<Hint> Hints => [new Hint(PadButton.B, S["hint.back"]), new Hint(PadButton.A, S["servers.hidden.show"])];

    /// <summary>The language changed: the same page again.</summary>
    public override PageViewModel? Recreate() => Launcher.Servers.Hidden.Count > 0 ? new HiddenServersViewModel(Shell) : null;

    public override void OnStateChanged()
    {
        Sync();
        base.OnStateChanged();
    }

    private void Show(HiddenServer server) => RelayCommand.Async(async () =>
    {
        await Launcher.ShowServerAsync(server.Id).ConfigureAwait(true);
        Sync();
        if (Rows.Count == 0 && ReferenceEquals(Shell.Page, this))
        {
            Shell.GoBack(); // nothing left to show: back to Settings › Online
        }
    }).Execute(null);

    /// <summary>The rows follow the launcher's list, each kept while its server is still hidden (the focus stays where it is).</summary>
    private void Sync()
    {
        var hidden = Launcher.Servers.Hidden;
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!hidden.Any(h => h.Id == Rows[i].Id))
            {
                Rows.RemoveAt(i);
            }
        }
        for (var i = 0; i < hidden.Count; i++)
        {
            if (i >= Rows.Count || Rows[i].Id != hidden[i].Id)
            {
                Rows.Insert(i, new HiddenServerRow(hidden[i], Show));
            }
        }
    }
}

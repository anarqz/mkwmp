// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Windows.Input;
using Mkwmp.Core;
using Mkwmp.Core.Overlay;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// A player of the current lobby, as their row shows them: the name their game shows, "You" for this PC's player, and
/// "Used 3 names" with the names when the same Player ID showed more than one in this online session. The same
/// instance while they stay in the lobby (the focus stays on it).
/// </summary>
public sealed class LobbyPlayerRow : ObservableObject
{
    private string _name = "";
    private string _warning = "";
    private string _names = "";
    private bool _isYou;

    internal LobbyPlayerRow(string id, Action<string> open)
    {
        Id = id;
        OpenCommand = new RelayCommand(() => open(Id));
    }

    /// <summary>The member's handle for this online session (<see cref="LobbyMember.Id"/>).</summary>
    public string Id { get; }

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public bool IsYou
    {
        get => _isYou;
        private set => Set(ref _isYou, value);
    }

    public bool Renamed => _warning.Length > 0;

    /// <summary>"Used 3 names"; "" for a player with one name.</summary>
    public string Warning
    {
        get => _warning;
        private set
        {
            if (Set(ref _warning, value))
            {
                Raise(nameof(Renamed));
            }
        }
    }

    /// <summary>Those names, oldest first; "" for a player with one name.</summary>
    public string Names
    {
        get => _names;
        private set => Set(ref _names, value);
    }

    public ICommand OpenCommand { get; }

    internal void Update(Strings s, LobbyMember member)
    {
        Name = member.Name.Length > 0 ? member.Name : s["lobby.no_name"];
        IsYou = member.Local;
        Warning = member.Renamed ? s.Format("lobby.used_names", ("n", member.Names.Count.ToString(s.Culture))) : "";
        Names = member.Renamed ? string.Join(" · ", member.Names.Select(n => n.Name)) : "";
    }
}

/// <summary>
/// Current Lobby (the friends panel, Settings › Profile, and the first thing the overlay shows while the game is in an
/// online lobby): who is in the game's lobby right now, this PC's player first ("You"). A player who showed more than
/// one name under the same Player ID since this online session began is flagged. A on a player opens them
/// (<see cref="LobbyPlayerViewModel"/>: Report). Nothing of it is stored: it is the launcher's memory of what the game
/// tells (docs/contracts/LAUNCHER_API.md, "The current lobby"), and it is empty again when the player leaves online.
/// (View: Views/Settings/LobbyPage.xaml.)
/// </summary>
public sealed class LobbyViewModel : PageViewModel
{
    public LobbyViewModel(ShellViewModel shell)
        : base(shell) => Sync();

    /// <summary>Opens the page from anywhere (the friends panel closes); once, when it is already the page.</summary>
    public static void Show(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        shell.IsFriendsOpen = false;
        if (shell.Page is not LobbyViewModel)
        {
            shell.Navigate(new LobbyViewModel(shell));
        }
    }

    public ObservableCollection<LobbyPlayerRow> Rows { get; } = [];

    public bool InLobby => Launcher.Lobby.InLobby;

    public bool IsEmpty => !InLobby;

    /// <summary>"Nextendo Network", or "MKWMP server · mkw.example.net"; "" out of a lobby.</summary>
    public string Network => InLobby ? LobbyRules.Network(Launcher.Lobby) : "";

    public string PlayersHeader => S.Format("lobby.players", ("n", Launcher.Lobby.Members.Count.ToString(S.Culture)));

    public string YouLabel => S["lobby.you"];

    /// <summary>The language changed: the same page again.</summary>
    public override PageViewModel? Recreate() => new LobbyViewModel(Shell);

    public override void OnEnter() => Sync();

    public override void OnStateChanged()
    {
        Sync();
        base.OnStateChanged();
    }

    /// <summary>In place: a player who stays keeps their row (and the focus on it).</summary>
    private void Sync()
    {
        var members = Launcher.Lobby.Members;
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (members.All(m => m.Id != Rows[i].Id))
            {
                Rows.RemoveAt(i);
            }
        }
        for (var i = 0; i < members.Count; i++)
        {
            var at = -1;
            for (var j = i; j < Rows.Count; j++)
            {
                if (Rows[j].Id == members[i].Id)
                {
                    at = j;
                    break;
                }
            }
            if (at < 0)
            {
                Rows.Insert(i, new LobbyPlayerRow(members[i].Id, Open));
            }
            else if (at != i)
            {
                Rows.Move(at, i);
            }
            Rows[i].Update(S, members[i]);
        }
    }

    private void Open(string id) => Shell.Navigate(new LobbyPlayerViewModel(Shell, id));
}

/// <summary>
/// One player of the current lobby, by their Player ID (the id the server gave them: it stays when they rename): the
/// names shown under it since this online session began, and Report. On an MKWMP server, "Report to the Server's Host"
/// opens Report Abuse with the name and the Player ID filled in (the player reads it, chooses the reason, and sends it
/// or not); on Nextendo Network, and always besides, "Copy Report" puts a plain-text report on the clipboard
/// (<see cref="LobbyReport"/>) and says who to send it to. Nothing is ever sent by the launcher itself, and nothing is
/// stored. The page says so when the player has left the lobby. (View: Views/Settings/LobbyPlayerPage.xaml.)
/// </summary>
public sealed class LobbyPlayerViewModel : PageViewModel
{
    private readonly string _id;
    private string _lastName = "";

    public LobbyPlayerViewModel(ShellViewModel shell, string id)
        : base(shell)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        CopyReportCommand = new RelayCommand(CopyReport, () => ShowReport);
        CopyIdCommand = new RelayCommand(CopyId, () => Found);
        HostReportCommand = new RelayCommand(HostReport, () => ShowHostReport);
        Sync();
    }

    private LobbyMember? Member => Launcher.Lobby.Member(_id);

    public ObservableCollection<string> Names { get; } = [];

    /// <summary>They left the lobby (or the player left online): nothing is kept of them.</summary>
    public bool Gone => Member is null;

    public bool Found => Member is not null;

    /// <summary>The name their game shows now (the last one seen, once they are gone).</summary>
    public string Title => Member is { } m ? (m.Name.Length > 0 ? m.Name : S["lobby.no_name"]) : _lastName.Length > 0 ? _lastName : S["lobby.title"];

    public bool IsYou => Member?.Local ?? false;

    public string YouLabel => S["lobby.you"];

    /// <summary>The Player ID: the id the server gave this player.</summary>
    public string Uid => Member?.Uid ?? "";

    /// <summary>"This player used 3 different names with the same Player ID."; "" for one name.</summary>
    public string Warning => Member is { Renamed: true } m ? S.Format("lobby.player.warn", ("n", m.Names.Count.ToString(S.Culture))) : "";

    public string NamesHeader => S.Format("lobby.player.names", ("n", (Member?.Names.Count ?? 0).ToString(S.Culture)));

    /// <summary>Nobody reports themselves.</summary>
    public bool ShowReport => Member is { Local: false };

    public string ReportCaption => S[Launcher.Lobby.OnNextendo ? "lobby.report.caption.nextendo" : "lobby.report.caption.mkwmp"];

    /// <summary>Report Abuse of the MKWMP server in use, filled in (<see cref="LobbyRules.CanReportToHost"/>).</summary>
    public bool ShowHostReport => Member is { } m && LobbyRules.CanReportToHost(Launcher, Launcher.Lobby, m);

    /// <summary>Copy Report is the page's first action where Report Abuse is not offered (Nextendo Network).</summary>
    public bool CopyIsDefault => !ShowHostReport;

    public ICommand CopyReportCommand { get; }

    public ICommand CopyIdCommand { get; }

    public ICommand HostReportCommand { get; }

    public override PageViewModel? Recreate() => new LobbyPlayerViewModel(Shell, _id);

    public override void OnEnter() => Sync();

    public override void OnStateChanged()
    {
        Sync();
        base.OnStateChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Sync()
    {
        var names = Member?.Names.Select(n => n.Name).ToList() ?? [];
        if (Member is { Name.Length: > 0 } member)
        {
            _lastName = member.Name;
        }
        for (var i = 0; i < names.Count; i++)
        {
            if (i >= Names.Count)
            {
                Names.Add(names[i]);
            }
            else if (Names[i] != names[i])
            {
                Names[i] = names[i];
            }
        }
        while (Names.Count > names.Count)
        {
            Names.RemoveAt(Names.Count - 1);
        }
    }

    private void CopyId()
    {
        if (Member is { } member)
        {
            Shell.ShowToast(S[ShellViewModel.CopyText(member.Uid) ? "lobby.player.id_copied" : "lobby.copy_failed"]);
        }
    }

    /// <summary>The report goes to the clipboard and nowhere else: the toast says who to send it to.</summary>
    private void CopyReport()
    {
        var lobby = Launcher.Lobby;
        if (Member is not { Local: false } member)
        {
            return;
        }
        if (!ShellViewModel.CopyText(LobbyReport.Text(lobby, member, Launcher.Version, DateTimeOffset.UtcNow)))
        {
            Shell.ShowToast(S["lobby.copy_failed"], "", ToastKind.Error);
            return;
        }
        Shell.ShowToast(S["lobby.report.copied"], lobby.OnNextendo
            ? S["lobby.report.copied.nextendo"]
            : S.Format("lobby.report.copied.mkwmp", ("server", lobby.Server.Length > 0 ? lobby.Server : S["lobby.backend.mkwmp"])), ToastKind.Success);
    }

    /// <summary>Report Abuse with the name and the Player ID filled in: the player reads it, chooses the reason, and sends it (or not).</summary>
    private void HostReport()
    {
        if (Member is { } member && ShowHostReport)
        {
            Shell.Navigate(new ReportViewModel(Shell, member.Name, LobbyReport.Brief(member, CoreStrings.ReportDetailsMax)));
        }
    }
}

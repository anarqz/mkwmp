// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>How the pages of Previous Races write a time, a network and a name.</summary>
internal static class RaceText
{
    /// <summary>"27/11/2026 21:04": the local date and time, as the language writes them.</summary>
    public static string When(Strings s, DateTimeOffset time) => time.ToLocalTime().ToString("g", s.Culture);

    /// <summary>"21:04": the local time of day.</summary>
    public static string Hour(Strings s, DateTimeOffset time) => time.ToLocalTime().ToString("t", s.Culture);

    /// <summary>"Nextendo Network", or "MKWMP · mkw.example.net".</summary>
    public static string Network(Strings s, RaceSession session) => session.OnNextendo
        ? s["nextendo.title"]
        : session.Server.Length > 0 ? s["races.backend.mkwmp"] + " · " + session.Server : s["races.backend.mkwmp"];

    /// <summary>The name, or "(no name)" for a player whose game showed none.</summary>
    public static string Name(Strings s, string name) => name.Length > 0 ? name : s["races.no_name"];

    public static string Count(Strings s, int n) => n.ToString(s.Culture);
}

/// <summary>An online session, as its row shows it: when, the network, how many races and players, and how many of them changed names.</summary>
public sealed class RaceSessionRow
{
    internal RaceSessionRow(Strings s, RaceSession session, Action<string> open, string? names = null)
    {
        Id = session.Id;
        Title = RaceText.When(s, session.Started);
        var counts = s.Format("races.row", ("races", RaceText.Count(s, session.Matches.Count)), ("players", RaceText.Count(s, session.Players.Count)));
        Detail = RaceText.Network(s, session) + " · " + counts + (names is { Length: > 0 } ? "\n" + names : "");
        Flag = session.Renamed > 0 ? s.Format("races.renamed", ("n", RaceText.Count(s, session.Renamed))) : "";
        OpenCommand = new RelayCommand(() => open(Id));
    }

    public string Id { get; }

    public string Title { get; }

    public string Detail { get; }

    /// <summary>"Name changes: 2"; "" when nobody changed names.</summary>
    public string Flag { get; }

    public ICommand OpenCommand { get; }
}

/// <summary>
/// Previous Races (the friends panel, Settings › Profile): the online sessions kept on this PC, newest first. Each
/// leads to its players (<see cref="RaceSessionViewModel"/>), and a player to every name they used under the same
/// Player ID (<see cref="RacePlayerViewModel"/>), which is what tells a player who renames between races. The history
/// is local (docs/contracts/LAUNCHER_API.md, "Previous races"): 30 days, never sent, and "Clear History" deletes it.
/// (View: Views/Settings/PreviousRacesPage.xaml.)
/// </summary>
public sealed class PreviousRacesViewModel : PageViewModel
{
    private int _revision = -1;
    private bool _loaded;
    private bool _loading;

    public PreviousRacesViewModel(ShellViewModel shell)
        : base(shell)
    {
        ClearCommand = RelayCommand.Async(ClearAsync, () => Rows.Count > 0);
    }

    /// <summary>Opens the page from anywhere (the friends panel closes); once, when it is already the page.</summary>
    public static void Show(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        shell.IsFriendsOpen = false;
        if (shell.Page is not PreviousRacesViewModel)
        {
            shell.Navigate(new PreviousRacesViewModel(shell));
        }
    }

    public ObservableCollection<RaceSessionRow> Rows { get; } = [];

    public bool Loading => !_loaded;

    public bool IsEmpty => _loaded && Rows.Count == 0;

    public bool HasRows => Rows.Count > 0;

    public ICommand ClearCommand { get; }

    /// <summary>The language changed: the same page again.</summary>
    public override PageViewModel? Recreate() => new PreviousRacesViewModel(Shell);

    public override void OnEnter() => Reload();

    public override void OnStateChanged()
    {
        Reload();
        base.OnStateChanged();
    }

    private void Reload()
    {
        if (_loading || _revision == Launcher.RaceHistoryRevision)
        {
            return;
        }
        _loading = true;
        RelayCommand.Async(async () =>
        {
            try
            {
                var revision = Launcher.RaceHistoryRevision;
                var sessions = await Launcher.RaceSessionsAsync().ConfigureAwait(true);
                _revision = revision;
                Sync(sessions);
            }
            finally
            {
                (_loading, _loaded) = (false, true);
                RaiseAll();
                CommandManager.InvalidateRequerySuggested();
            }
            if (_revision != Launcher.RaceHistoryRevision)
            {
                Reload(); // a roster came while the files were read
            }
        }).Execute(null);
    }

    /// <summary>The rows follow the stored sessions; a row whose text did not change keeps its instance (and the focus on it).</summary>
    private void Sync(IReadOnlyList<RaceSession> sessions)
    {
        var wanted = sessions.Select(s => new RaceSessionRow(S, s, Open)).ToList();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(w => w.Id == Rows[i].Id))
            {
                Rows.RemoveAt(i);
            }
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= Rows.Count || Rows[i].Id != wanted[i].Id)
            {
                Rows.Insert(i, wanted[i]);
            }
            else if ((Rows[i].Title, Rows[i].Detail, Rows[i].Flag) != (wanted[i].Title, wanted[i].Detail, wanted[i].Flag))
            {
                Rows[i] = wanted[i];
            }
        }
    }

    private void Open(string id) => Shell.Navigate(new RaceSessionViewModel(Shell, id));

    private async Task ClearAsync()
    {
        if (!await Shell.ConfirmAsync(S["races.clear.title"], S["races.clear.body"], S["races.clear"]).ConfigureAwait(true))
        {
            return;
        }
        await Launcher.ClearRaceHistoryAsync().ConfigureAwait(true);
        Shell.ShowToast(S["races.cleared"]);
        Reload();
    }
}

/// <summary>A player of a session, as their row shows them: the name seen last, "You" for this PC's player, and "Used 3 names" with the names when the same Player ID showed more than one.</summary>
public sealed class RacePlayerRow
{
    internal RacePlayerRow(Strings s, RacePlayer player, Action<string> open)
    {
        Uid = player.Uid;
        Name = RaceText.Name(s, player.Latest);
        IsYou = player.Local;
        Renamed = player.Renamed;
        Warning = player.Renamed ? s.Format("races.used_names", ("n", RaceText.Count(s, player.Names.Count))) : "";
        Names = player.Renamed ? string.Join(" · ", player.Names.Select(n => n.Name)) : "";
        OpenCommand = new RelayCommand(() => open(Uid));
    }

    public string Uid { get; }

    public string Name { get; }

    public bool IsYou { get; }

    public bool Renamed { get; }

    /// <summary>"Used 3 names"; "" for a player with one name.</summary>
    public string Warning { get; }

    /// <summary>Those names, oldest first; "" for a player with one name.</summary>
    public string Names { get; }

    public ICommand OpenCommand { get; }
}

/// <summary>
/// One online session of Previous Races: when its races started, and everyone met in it. The players who used more
/// than one name come first, flagged; this PC's player comes last, marked "You". A on a player opens them
/// (<see cref="RacePlayerViewModel"/>). (View: Views/Settings/RaceSessionPage.xaml.)
/// </summary>
public sealed class RaceSessionViewModel : PageViewModel
{
    private readonly string _id;
    private RaceSession? _session;
    private int _revision = -1;
    private bool _loaded;
    private bool _loading;

    public RaceSessionViewModel(ShellViewModel shell, string id)
        : base(shell) => _id = id ?? throw new ArgumentNullException(nameof(id));

    public ObservableCollection<RacePlayerRow> Rows { get; } = [];

    public bool Loading => !_loaded;

    /// <summary>The session is no longer stored (cleared, or older than 30 days).</summary>
    public bool Gone => _loaded && _session is null;

    public bool Found => _session is not null;

    /// <summary>"27/11/2026 21:04".</summary>
    public string Title => _session is { } s ? RaceText.When(S, s.Started) : S["races.title"];

    /// <summary>"Nextendo Network".</summary>
    public string Network => _session is { } s ? RaceText.Network(S, s) : "";

    public string RacesHeader => S.Format("races.session.races", ("n", RaceText.Count(S, _session?.Matches.Count ?? 0)));

    /// <summary>"21:07 · 21:12 · 21:18": when each race started; or that none did.</summary>
    public string RacesText => _session is { Matches.Count: > 0 } s
        ? string.Join(" · ", s.Matches.Select(m => RaceText.Hour(S, m.Time)))
        : S["races.session.no_races"];

    public string PlayersHeader => S.Format("races.session.players", ("n", RaceText.Count(S, _session?.Players.Count ?? 0)));

    public string YouLabel => S["races.you"];

    public override PageViewModel? Recreate() => new RaceSessionViewModel(Shell, _id);

    public override void OnEnter() => Reload();

    public override void OnStateChanged()
    {
        Reload();
        base.OnStateChanged();
    }

    private void Reload()
    {
        if (_loading || _revision == Launcher.RaceHistoryRevision)
        {
            return;
        }
        _loading = true;
        RelayCommand.Async(async () =>
        {
            try
            {
                var revision = Launcher.RaceHistoryRevision;
                _session = await Launcher.RaceSessionAsync(_id).ConfigureAwait(true);
                _revision = revision;
                Sync();
            }
            finally
            {
                (_loading, _loaded) = (false, true);
                RaiseAll();
            }
            if (_revision != Launcher.RaceHistoryRevision)
            {
                Reload(); // a roster came while the file was read
            }
        }).Execute(null);
    }

    private void Sync()
    {
        var players = _session?.Players ?? [];
        var wanted = players
            .OrderBy(p => p.Local)
            .ThenByDescending(p => p.Renamed)
            .ThenBy(p => p.Latest, StringComparer.Create(S.Culture, ignoreCase: true))
            .Select(p => new RacePlayerRow(S, p, Open))
            .ToList();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(w => w.Uid == Rows[i].Uid))
            {
                Rows.RemoveAt(i);
            }
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = -1;
            for (var j = i; j < Rows.Count; j++)
            {
                if (Rows[j].Uid == wanted[i].Uid)
                {
                    at = j;
                    break;
                }
            }
            if (at < 0)
            {
                Rows.Insert(i, wanted[i]);
                continue;
            }
            if (at != i)
            {
                Rows.Move(at, i);
            }
            if ((Rows[i].Name, Rows[i].Warning, Rows[i].Names, Rows[i].IsYou) != (wanted[i].Name, wanted[i].Warning, wanted[i].Names, wanted[i].IsYou))
            {
                Rows[i] = wanted[i];
            }
        }
    }

    private void Open(string uid) => Shell.Navigate(new RacePlayerViewModel(Shell, uid));
}

/// <summary>A name a player used: the name, and "First seen … · Last seen …".</summary>
public sealed record RaceNameRow(string Name, string Seen);

/// <summary>
/// One player of Previous Races, by their Player ID (the id the server gave them: it stays when they rename): every
/// name shown under it with when it was first and last seen, across all the stored sessions, and those sessions.
/// "Copy Report" puts a plain-text report on the clipboard (<see cref="RaceReport"/>) and says who to send it to:
/// Nextendo staff on Nextendo Network, the server's host on an MKWMP server, where Report Abuse can also be opened
/// with the name and the Player ID filled in. Nothing is ever sent by the launcher itself.
/// (View: Views/Settings/RacePlayerPage.xaml.)
/// </summary>
public sealed class RacePlayerViewModel : PageViewModel
{
    private readonly string _uid;
    private RacePlayerHistory? _player;
    private int _revision = -1;
    private bool _loaded;
    private bool _loading;

    public RacePlayerViewModel(ShellViewModel shell, string uid)
        : base(shell)
    {
        _uid = uid ?? throw new ArgumentNullException(nameof(uid));
        CopyReportCommand = new RelayCommand(CopyReport, () => _player is not null);
        CopyIdCommand = new RelayCommand(CopyId);
        HostReportCommand = new RelayCommand(HostReport, () => ShowHostReport);
    }

    public ObservableCollection<RaceNameRow> Names { get; } = [];

    public ObservableCollection<RaceSessionRow> Sessions { get; } = [];

    public bool Loading => !_loaded;

    /// <summary>No stored session holds this id any more.</summary>
    public bool Gone => _loaded && _player is null;

    public bool Found => _player is not null;

    /// <summary>The name seen last.</summary>
    public string Title => _player is { } p ? RaceText.Name(S, p.Latest) : S["races.title"];

    public bool IsYou => _player?.Local ?? false;

    public string YouLabel => S["races.you"];

    /// <summary>The Player ID: the id the server gave this player.</summary>
    public string Uid => _uid;

    /// <summary>"This player used 3 different names with the same Player ID."; "" for one name.</summary>
    public string Warning => _player is { Renamed: true } p ? S.Format("races.player.warn", ("n", RaceText.Count(S, p.Names.Count))) : "";

    public string NamesHeader => S.Format("races.player.names", ("n", RaceText.Count(S, _player?.Names.Count ?? 0)));

    public string SessionsHeader => S.Format("races.player.sessions", ("n", RaceText.Count(S, _player?.Sightings.Count ?? 0)));

    /// <summary>The network they were met on last: who a report goes to.</summary>
    private RaceSession? LastSession => _player is { Sightings.Count: > 0 } p ? p.Sightings[0].Session : null;

    private bool OnNextendo => LastSession?.OnNextendo ?? false;

    /// <summary>Nobody reports themselves.</summary>
    public bool ShowReport => Found && !IsYou;

    public string ReportCaption => S[OnNextendo ? "races.report.caption.nextendo" : "races.report.caption.mkwmp"];

    /// <summary>
    /// Report Abuse of the MKWMP server in use, filled in: only for a player met on that very server, by someone who
    /// may report there, and whose last name is one that server can know (a gamertag).
    /// </summary>
    public bool ShowHostReport => ShowReport && LastSession is { OnNextendo: false } session && _player is { } p
        && Launcher.OnlineEnabled && ReportViewModel.Allowed(Launcher) && Links.ValidName(p.Latest)
        && string.Equals(session.Server, ProfileForm.HostOf(Launcher.Settings.Server), StringComparison.OrdinalIgnoreCase);

    public ICommand CopyReportCommand { get; }

    public ICommand CopyIdCommand { get; }

    public ICommand HostReportCommand { get; }

    public override PageViewModel? Recreate() => new RacePlayerViewModel(Shell, _uid);

    public override void OnEnter() => Reload();

    public override void OnStateChanged()
    {
        Reload();
        base.OnStateChanged();
    }

    private void Reload()
    {
        if (_loading || _revision == Launcher.RaceHistoryRevision)
        {
            return;
        }
        _loading = true;
        RelayCommand.Async(async () =>
        {
            try
            {
                var revision = Launcher.RaceHistoryRevision;
                _player = await Launcher.RacePlayerAsync(_uid).ConfigureAwait(true);
                _revision = revision;
                Sync();
            }
            finally
            {
                (_loading, _loaded) = (false, true);
                RaiseAll();
                CommandManager.InvalidateRequerySuggested();
            }
            if (_revision != Launcher.RaceHistoryRevision)
            {
                Reload(); // a roster came while the files were read
            }
        }).Execute(null);
    }

    private void Sync()
    {
        var names = (_player?.Names ?? []).Select(n => new RaceNameRow(RaceText.Name(S, n.Name),
            S.Format("races.player.seen", ("first", RaceText.When(S, n.First)), ("last", RaceText.When(S, n.Last))))).ToList();
        Replace(Names, names);
        var sessions = (_player?.Sightings ?? []).Select(seen => new RaceSessionRow(S, seen.Session, OpenSession,
            string.Join(" · ", seen.Player.Names.Select(n => n.Name)))).ToList();
        Replace(Sessions, sessions, (a, b) => (a.Id, a.Title, a.Detail, a.Flag) == (b.Id, b.Title, b.Detail, b.Flag));
    }

    /// <summary>In place: a row that did not change keeps its instance (and the focus on it).</summary>
    private static void Replace<T>(ObservableCollection<T> rows, List<T> wanted, Func<T, T, bool>? same = null)
    {
        same ??= EqualityComparer<T>.Default.Equals;
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= rows.Count)
            {
                rows.Add(wanted[i]);
            }
            else if (!same(rows[i], wanted[i]))
            {
                rows[i] = wanted[i];
            }
        }
        while (rows.Count > wanted.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    private void OpenSession(string id) => Shell.Navigate(new RaceSessionViewModel(Shell, id));

    private void CopyId()
    {
        if (ShellViewModel.CopyText(_uid))
        {
            Shell.ShowToast(S["races.player.id_copied"]);
        }
        else
        {
            Shell.ShowToast(S["races.copy_failed"]);
        }
    }

    /// <summary>The report goes to the clipboard and nowhere else: the toast says who to send it to.</summary>
    private void CopyReport()
    {
        if (_player is not { } player)
        {
            return;
        }
        if (!ShellViewModel.CopyText(RaceReport.Text(player, Launcher.Version)))
        {
            Shell.ShowToast(S["races.copy_failed"]);
            return;
        }
        Shell.ShowToast(S["races.report.copied"], OnNextendo
            ? S["races.report.copied.nextendo"]
            : S.Format("races.report.copied.mkwmp", ("server", LastSession is { Server.Length: > 0 } s ? s.Server : S["races.backend.mkwmp"])));
    }

    /// <summary>Report Abuse with the name and the Player ID filled in: the player reads it, chooses the reason, and sends it (or not).</summary>
    private void HostReport()
    {
        if (_player is { } player && ShowHostReport)
        {
            Shell.Navigate(new ReportViewModel(Shell, player.Latest, RaceReport.Brief(player, CoreStrings.ReportDetailsMax)));
        }
    }
}

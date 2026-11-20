// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Mkwmp.App.Controls;
using Mkwmp.App.Input;
using Mkwmp.Core;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>A line of the friends panel's list. The same instance while its person stays in the list (the focus stays).</summary>
public abstract class FriendsRow : ObservableObject
{
    private bool _isDefault;

    protected FriendsRow(string key) => Key = key;

    /// <summary>What makes it the same line after the list changed ("friend:Luna").</summary>
    public string Key { get; }

    /// <summary>Gets the focus when the panel opens, or when the line that had it is gone.</summary>
    public bool IsDefault
    {
        get => _isDefault;
        set => Set(ref _isDefault, value);
    }
}

/// <summary>"REQUESTS · 2", "FRIENDS · 5 ONLINE", "SENT · 1".</summary>
public sealed class FriendsSection : FriendsRow
{
    private string _text = "";

    public FriendsSection(string key)
        : base("section:" + key)
    {
    }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }
}

/// <summary>Someone who wants to be a friend: Accept / Decline.</summary>
public sealed class FriendRequestRow : FriendsRow
{
    public FriendRequestRow(string name, string note, Action<FriendRequestRow> accept, Action<FriendRequestRow> decline)
        : base("in:" + name)
    {
        Name = name;
        Note = note;
        AcceptCommand = new RelayCommand(() => accept(this));
        DeclineCommand = new RelayCommand(() => decline(this));
    }

    public string Name { get; }

    public string Note { get; }

    public ICommand AcceptCommand { get; }

    public ICommand DeclineCommand { get; }
}

/// <summary>A friend: A opens the small menu (Remove friend).</summary>
public sealed class FriendRow : FriendsRow
{
    private FriendState _state;
    private string _stateText = "";

    public FriendRow(string name, Action<FriendRow> open)
        : base("friend:" + name)
    {
        Name = name;
        OpenCommand = new RelayCommand(() => open(this));
    }

    public string Name { get; }

    public ICommand OpenCommand { get; }

    public FriendState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(Presence));
            }
        }
    }

    public AvatarPresence Presence => _state switch
    {
        FriendState.Playing => AvatarPresence.Playing,
        FriendState.Online => AvatarPresence.Online,
        _ => AvatarPresence.Offline,
    };

    /// <summary>"Racing" / "Online" / "Offline".</summary>
    public string StateText
    {
        get => _stateText;
        set => Set(ref _stateText, value);
    }
}

/// <summary>A request the player sent: Cancel.</summary>
public sealed class FriendSentRow : FriendsRow
{
    public FriendSentRow(string name, string note, Action<FriendSentRow> cancel)
        : base("out:" + name)
    {
        Name = name;
        Note = note;
        CancelCommand = new RelayCommand(() => cancel(this));
    }

    public string Name { get; }

    public string Note { get; }

    public ICommand CancelCommand { get; }
}

/// <summary>
/// The friends panel that slides in from the right on any page (Y / RB / F): the Rust launcher's
/// FRIENDS card. Add a friend by gamertag, one message line, then REQUESTS (Accept / Decline),
/// FRIENDS (A: Remove friend, after a confirmation) and SENT (Cancel).
/// </summary>
public sealed class FriendsPanelViewModel : LayerViewModel
{
    private readonly ShellViewModel _shell;
    private string _entry = "";
    private string? _localNote;
    private string? _focusName;

    public FriendsPanelViewModel(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        AddCommand = new RelayCommand(Add);
        Build();
    }

    public Strings S => _shell.S;

    private ILauncher Launcher => _shell.Launcher;

    private FriendsView Friends => Launcher.Friends;

    /// <summary>A valid gamertag and its PIN: without them there is no friends list.</summary>
    public bool SignedIn => Launcher.Profile.SignedIn;

    /// <summary>"5 of 8 online"; nothing before the list is there.</summary>
    public string Summary => SignedIn && Friends.Loaded && Friends.Friends.Count > 0
        ? S.Format("friends.summary",
            ("online", Friends.OnlineCount.ToString(CultureInfo.CurrentCulture)),
            ("total", Friends.Friends.Count.ToString(CultureInfo.CurrentCulture)))
        : "";

    // ── add a friend ──

    /// <summary>The "add a friend" field. Gamertags have no spaces.</summary>
    public string Entry
    {
        get => _entry;
        set
        {
            var clean = string.Concat((value ?? "").Where(c => !char.IsWhiteSpace(c)));
            var changed = _entry != clean;
            _entry = clean;
            if (changed)
            {
                _localNote = null;
                Raise(nameof(NoteText));
                Raise(nameof(NoteTone));
                Raise(nameof(ShowPlaceholder));
            }
            if (changed || clean != value)
            {
                Raise();
            }
        }
    }

    public bool ShowPlaceholder => _entry.Length == 0;

    public ICommand AddCommand { get; }

    /// <summary>3-16 of A-Z a-z 0-9 _ - (the server's rule).</summary>
    public static bool ValidName(string name) =>
        name is { Length: >= 3 and <= 16 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private void Add()
    {
        var entry = _entry.Trim();
        if (!SignedIn || Friends.Busy)
        {
            return;
        }
        if (!ValidName(entry))
        {
            _localNote = CoreStrings.NameRule;
            Raise(nameof(NoteText));
            Raise(nameof(NoteTone));
            return;
        }
        Entry = "";
        Run(() => Launcher.AddFriendAsync(entry));
    }

    /// <summary>Enter in the field, or OK on the on-screen keyboard: adds when the gamertag is a valid one.</summary>
    public void Submit()
    {
        if (_entry.Length > 0)
        {
            Add();
        }
    }

    /// <summary>Exactly one message line, as in the Rust launcher: not signed in, the last outcome, loading, or what friends are.</summary>
    public string NoteText => !SignedIn ? CoreStrings.RegisterToAddFriends
        : _localNote ?? Friends.Note
        ?? (!Friends.Loaded ? CoreStrings.LoadingFriends : CoreStrings.FriendsAreOnServer);

    /// <summary>bad / good / muted.</summary>
    public string NoteTone => !SignedIn ? "muted"
        : _localNote is not null ? "bad"
        : Friends.Note is not null ? Friends.NoteIsError ? "bad" : "good"
        : "muted";

    public bool IsLoading => SignedIn && !Friends.Loaded;

    public bool IsEmpty => SignedIn && Friends.Loaded && Friends.Friends.Count == 0 && Friends.Incoming.Count == 0 && Friends.Outgoing.Count == 0;

    // ── the list ──

    /// <summary>Sections and their lines, changed in place.</summary>
    public ObservableCollection<FriendsRow> Rows { get; } = [];

    public override IReadOnlyList<Hint> Hints => [new Hint(PadButton.B, S["hint.close"]), new Hint(PadButton.A, S["hint.ok"])];

    /// <summary>The line that gets the focus when the panel opens next (a friend's avatar of the top bar was chosen).</summary>
    public void FocusOn(string? name)
    {
        _focusName = name;
        ApplyDefault();
    }

    public override void OnOpened() => ApplyDefault();

    public override void OnClosed()
    {
        _focusName = null;
        _localNote = null;
        Raise(nameof(NoteText));
        Raise(nameof(NoteTone));
    }

    public override void OnStateChanged()
    {
        Build();
        ApplyDefault();
        base.OnStateChanged();
    }

    private void Build()
    {
        var friends = Friends;
        var wanted = new List<(string Key, Func<FriendsRow> Create, Action<FriendsRow> Update)>();
        void Section(string key, string text) =>
            wanted.Add(("section:" + key, () => new FriendsSection(key), row => ((FriendsSection)row).Text = text));
        static string Count(int n) => n.ToString(CultureInfo.CurrentCulture);

        if (SignedIn && friends.Loaded)
        {
            if (friends.Incoming.Count > 0)
            {
                Section("requests", S.Format("friends.section.requests", ("n", Count(friends.Incoming.Count))));
                foreach (var request in friends.Incoming)
                {
                    wanted.Add(("in:" + request.Name, () => new FriendRequestRow(request.Name, S["friends.wants"], Accept, Decline), _ => { }));
                }
            }
            if (friends.Friends.Count > 0)
            {
                Section("friends", S.Format("friends.section.friends", ("n", Count(friends.OnlineCount))));
                foreach (var friend in friends.Friends.OrderBy(f => f.State).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    wanted.Add(("friend:" + friend.Name, () => new FriendRow(friend.Name, OpenFriend), row =>
                    {
                        var line = (FriendRow)row;
                        line.State = friend.State;
                        line.StateText = StateText(friend.State);
                    }));
                }
            }
            if (friends.Outgoing.Count > 0)
            {
                Section("sent", S.Format("friends.section.sent", ("n", Count(friends.Outgoing.Count))));
                foreach (var request in friends.Outgoing)
                {
                    wanted.Add(("out:" + request.Name, () => new FriendSentRow(request.Name, S["friends.sent"], CancelRequest), _ => { }));
                }
            }
        }

        // in place: a line that stays keeps its instance (and the focus on it)
        for (var i = 0; i < wanted.Count; i++)
        {
            var (key, create, update) = wanted[i];
            var at = -1;
            for (var j = i; j < Rows.Count; j++)
            {
                if (Rows[j].Key == key)
                {
                    at = j;
                    break;
                }
            }
            if (at < 0)
            {
                Rows.Insert(i, create());
            }
            else if (at != i)
            {
                Rows.Move(at, i);
            }
            update(Rows[i]);
        }
        while (Rows.Count > wanted.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    private string StateText(FriendState state) => S[state switch
    {
        FriendState.Playing => "friends.playing",
        FriendState.Online => "friends.online",
        _ => "friends.offline",
    }];

    /// <summary>One default line at most: the asked friend, else the first request, else none (the add field is first).</summary>
    private void ApplyDefault()
    {
        FriendsRow? chosen = null;
        if (_focusName is { } name)
        {
            chosen = Rows.FirstOrDefault(r => r is FriendRow friend && string.Equals(friend.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? Rows.FirstOrDefault(r => r is FriendRequestRow request && string.Equals(request.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        chosen ??= Rows.FirstOrDefault(r => r is FriendRequestRow);
        foreach (var row in Rows)
        {
            row.IsDefault = ReferenceEquals(row, chosen);
        }
    }

    /// <summary>The line after this one (else the one before) takes the focus when this one goes.</summary>
    private void FocusNextTo(FriendsRow leaving)
    {
        var at = Rows.IndexOf(leaving);
        var next = Rows.Skip(at + 1).FirstOrDefault(r => r is not FriendsSection)
            ?? Rows.Take(Math.Max(0, at)).LastOrDefault(r => r is not FriendsSection);
        _focusName = next switch
        {
            FriendRow friend => friend.Name,
            FriendRequestRow request => request.Name,
            _ => null,
        };
        foreach (var row in Rows)
        {
            row.IsDefault = ReferenceEquals(row, next);
        }
    }

    // ── actions ──

    private static void Run(Func<Task> action) => RelayCommand.Async(action).Execute(null);

    private void Accept(FriendRequestRow row)
    {
        if (!Friends.Busy)
        {
            FocusNextTo(row);
            Run(() => Launcher.AcceptFriendAsync(row.Name));
        }
    }

    private void Decline(FriendRequestRow row)
    {
        if (!Friends.Busy)
        {
            FocusNextTo(row);
            Run(() => Launcher.RemoveFriendAsync(row.Name, FriendRemoval.Reject));
        }
    }

    private void CancelRequest(FriendSentRow row)
    {
        if (!Friends.Busy)
        {
            FocusNextTo(row);
            Run(() => Launcher.RemoveFriendAsync(row.Name, FriendRemoval.Cancel));
        }
    }

    /// <summary>A on a friend: the small menu.</summary>
    private void OpenFriend(FriendRow row)
    {
        var modal = new ModalViewModel { Title = row.Name, Body = row.StateText };
        modal.Actions.Add(new ModalAction(S["friends.remove"], () =>
        {
            _shell.CloseModal(modal);
            Run(() => RemoveAsync(row));
        }));
        modal.Bottom = new ModalAction(S["common.close"], () => _shell.CloseModal(modal));
        _shell.ShowModal(modal);
    }

    private async Task RemoveAsync(FriendRow row)
    {
        var sure = await _shell.ConfirmAsync(
            S["friends.remove.title"],
            S.Format("friends.remove.body", ("name", row.Name)),
            S["friends.remove.yes"]).ConfigureAwait(true);
        if (sure && !Friends.Busy)
        {
            FocusNextTo(row);
            await Launcher.RemoveFriendAsync(row.Name, FriendRemoval.Unfriend).ConfigureAwait(true);
        }
    }
}

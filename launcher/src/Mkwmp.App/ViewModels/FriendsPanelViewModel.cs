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
    private string? _picture;
    private string _color = "";

    protected FriendsRow(string key) => Key = key;

    /// <summary>On Nextendo: the person's profile picture (a file), when it is here; null: the disc with their initials.</summary>
    public string? Picture
    {
        get => _picture;
        set => Set(ref _picture, value);
    }

    /// <summary>On Nextendo: the colour behind their picture, "#rrggbb"; "": the colour of the name.</summary>
    public string Color
    {
        get => _color;
        set => Set(ref _color, value);
    }

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
    /// <param name="pid">On Nextendo: who (names are not unique there); 0 on an MKWMP server, where the name is who.</param>
    public FriendRequestRow(string name, string note, Action<FriendRequestRow> accept, Action<FriendRequestRow> decline, long pid = 0)
        : base(pid != 0 ? "nxin:" + pid.ToString(CultureInfo.InvariantCulture) : "in:" + name)
    {
        Pid = pid;
        Name = name;
        Note = note;
        AcceptCommand = new RelayCommand(() => accept(this));
        DeclineCommand = new RelayCommand(() => decline(this));
    }

    public long Pid { get; }

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

/// <summary>A friend on Nextendo Network: A opens the small menu (favourite, remove, block). Told apart by pid: names there are not unique.</summary>
public sealed class NextendoFriendRow : FriendsRow
{
    private string _name = "";
    private FriendState _state;
    private string _stateText = "";
    private string _friendCode = "";
    private bool _favorite;

    public NextendoFriendRow(long pid, Action<NextendoFriendRow> open)
        : base("nx:" + pid.ToString(CultureInfo.InvariantCulture))
    {
        Pid = pid;
        OpenCommand = new RelayCommand(() => open(this));
    }

    public long Pid { get; }

    public ICommand OpenCommand { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

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

    /// <summary>"Playing MKW" / "Online" / "Offline", under the name.</summary>
    public string StateText
    {
        get => _stateText;
        set => Set(ref _stateText, value);
    }

    public string FriendCode
    {
        get => _friendCode;
        set => Set(ref _friendCode, value);
    }

    /// <summary>A star beside the name.</summary>
    public bool Favorite
    {
        get => _favorite;
        set => Set(ref _favorite, value);
    }
}

/// <summary>Someone on Nextendo who is not a friend (yet): asked by the player (no button: Nextendo cannot take a request back), or met in a race (Add).</summary>
public sealed class NextendoPersonRow : FriendsRow
{
    public NextendoPersonRow(string key, NextendoPerson who, string name, string note, string? action, Action<NextendoPersonRow> act)
        : base(key)
    {
        Who = who;
        Name = name;
        Note = note;
        ActionLabel = action ?? "";
        ActCommand = new RelayCommand(() => act(this));
    }

    public NextendoPerson Who { get; }

    public string Name { get; }

    /// <summary>"request sent", or their friend code.</summary>
    public string Note { get; }

    /// <summary>"Add"; "" for a line with no button.</summary>
    public string ActionLabel { get; }

    public bool HasAction => ActionLabel.Length > 0;

    public ICommand ActCommand { get; }
}

/// <summary>
/// The friends panel that slides in from the right on any page (Y / RB / F): the Rust launcher's
/// FRIENDS card. Add a friend by gamertag, one message line, then REQUESTS (Accept / Decline),
/// FRIENDS (A: Remove friend, after a confirmation) and SENT (Cancel). Without a server
/// (<see cref="Locked"/>) it is only the locked state: friends live on a server.
/// <para>
/// While PLAY goes to Nextendo Network with a linked account (<see cref="OnNextendo"/>) the same panel shows that
/// account's Nextendo friends (<see cref="ILauncher.NextendoFriends"/>): the player's own friend code with Copy, add
/// by friend code, REQUESTS, FRIENDS (who plays and who is online first, favourites before the others; A: favourite,
/// remove, block), SENT and PLAYERS YOU MET (Add). On an MKWMP server it is exactly as it always was.
/// </para>
/// </summary>
public sealed class FriendsPanelViewModel : LayerViewModel
{
    private readonly ShellViewModel _shell;
    private string _entry = "";
    private string? _localNote;
    private bool _localNoteIsGood;
    private string? _focusName;

    public FriendsPanelViewModel(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        AddCommand = new RelayCommand(Add);
        ConfigureOnlineCommand = new RelayCommand(shell.ConfigureOnline);
        CopyCodeCommand = new RelayCommand(CopyCode);
        Build();
    }

    public Strings S => _shell.S;

    private ILauncher Launcher => _shell.Launcher;

    private FriendsView Friends => Launcher.Friends;

    private NextendoFriendsView Nx => Launcher.NextendoFriends;

    /// <summary>PLAY goes to Nextendo Network with a linked account: the panel shows that account's Nextendo friends.</summary>
    public bool OnNextendo => Nx.Enabled;

    /// <summary>An action is under way (one at a time).</summary>
    private bool Busy => OnNextendo ? Nx.Busy : Friends.Busy;

    /// <summary>No server is set (and no Nextendo account plays): no list, no add field, only "Configure online" (on Nextendo: "Sign in with Nextendo").</summary>
    public bool Locked => !Launcher.OnlineEnabled && !OnNextendo;

    public bool Unlocked => !Locked;

    /// <summary>The locked state's button: Settings › Online (the panel closes).</summary>
    public ICommand ConfigureOnlineCommand { get; }

    /// <summary>A valid gamertag and its PIN (on Nextendo: the linked account): without them there is no friends list.</summary>
    public bool SignedIn => OnNextendo || (!Locked && Launcher.Profile.SignedIn);

    /// <summary>"5 of 8 online"; nothing before the list is there.</summary>
    public string Summary => OnNextendo
        ? Nx is { Loaded: true, Friends.Count: > 0 }
            ? S.Format("friends.summary",
                ("online", Nx.OnlineCount.ToString(CultureInfo.CurrentCulture)),
                ("total", Nx.Friends.Count.ToString(CultureInfo.CurrentCulture)))
            : ""
        : SignedIn && Friends.Loaded && Friends.Friends.Count > 0
        ? S.Format("friends.summary",
            ("online", Friends.OnlineCount.ToString(CultureInfo.CurrentCulture)),
            ("total", Friends.Friends.Count.ToString(CultureInfo.CurrentCulture)))
        : "";

    // ── on Nextendo: the player's own friend code ──

    /// <summary>The linked account's friend code ("" on an MKWMP server, or when Nextendo gave none).</summary>
    public string MyCode => OnNextendo ? Launcher.Nextendo.FriendCode : "";

    public bool ShowMyCode => MyCode.Length > 0;

    /// <summary>Copies <see cref="MyCode"/>: what a friend types to add the player.</summary>
    public ICommand CopyCodeCommand { get; }

    private void CopyCode()
    {
        if (MyCode.Length == 0)
        {
            return;
        }
        _localNoteIsGood = ShellViewModel.CopyText(MyCode);
        _localNote = S[_localNoteIsGood ? "friends.nx.copied" : "friends.nx.copy_failed"];
        Raise(nameof(NoteText));
        Raise(nameof(NoteTone));
    }

    // ── add a friend ──

    /// <summary>"Add a friend by gamertag"; on Nextendo, by friend code.</summary>
    public string AddHint => S[OnNextendo ? "friends.nx.add_hint" : "friends.add_hint"];

    /// <summary>The on-screen keyboard's title for the field.</summary>
    public string AddTitle => S[OnNextendo ? "friends.nx.add_title" : "friends.add_title"];

    /// <summary>A gamertag is 16 at most; a friend code is typed with its dashes, or pasted with spaces around it.</summary>
    public int EntryMax => OnNextendo ? NextendoFriendCode.MaxTyped : 16;

    /// <summary>The on-screen keyboard's keys: a gamertag's, or digits (the twelve of a friend code; "SW" and the dashes are added).</summary>
    public KeyboardCharset EntryCharset => OnNextendo ? KeyboardCharset.Digits : KeyboardCharset.Gamertag;

    /// <summary>The "add a friend" field. Gamertags have no spaces; a friend code keeps what was typed (it is read leniently).</summary>
    public string Entry
    {
        get => _entry;
        set
        {
            var clean = OnNextendo ? (value ?? "").TrimStart() : string.Concat((value ?? "").Where(c => !char.IsWhiteSpace(c)));
            var changed = _entry != clean;
            _entry = clean;
            if (changed)
            {
                (_localNote, _localNoteIsGood) = (null, false);
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
        if (!SignedIn || Busy)
        {
            return;
        }
        if (OnNextendo)
        {
            if (NextendoFriendCode.Normalize(entry) is not { } code)
            {
                (_localNote, _localNoteIsGood) = (CoreStrings.NextendoFriendCodeRule, false);
                Raise(nameof(NoteText));
                Raise(nameof(NoteTone));
                return;
            }
            Entry = "";
            Run(() => Launcher.AddNextendoFriendAsync(code));
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
    public string NoteText => OnNextendo
        ? _localNote ?? Nx.Note ?? (!Nx.Loaded ? CoreStrings.LoadingFriends : CoreStrings.NextendoFriendsHint)
        : !SignedIn ? CoreStrings.RegisterToAddFriends
        : _localNote ?? Friends.Note
        ?? (!Friends.Loaded ? CoreStrings.LoadingFriends : CoreStrings.FriendsAreOnServer);

    /// <summary>bad / good / muted.</summary>
    public string NoteTone => _localNote is not null && OnNextendo ? _localNoteIsGood ? "good" : "bad"
        : OnNextendo ? Nx.Note is not null ? Nx.NoteIsError ? "bad" : "good" : "muted"
        : !SignedIn ? "muted"
        : _localNote is not null ? "bad"
        : Friends.Note is not null ? Friends.NoteIsError ? "bad" : "good"
        : "muted";

    public bool IsLoading => OnNextendo ? !Nx.Loaded && Nx.Note is null : SignedIn && !Friends.Loaded;

    public bool IsEmpty => OnNextendo
        ? Nx is { Loaded: true, Friends.Count: 0, Incoming.Count: 0, Sent.Count: 0, Recent.Count: 0 }
        : SignedIn && Friends.Loaded && Friends.Friends.Count == 0 && Friends.Incoming.Count == 0 && Friends.Outgoing.Count == 0;

    /// <summary>"No friends yet. Add someone by their gamertag above."; on Nextendo, by their friend code.</summary>
    public string EmptyText => S[OnNextendo ? "friends.nx.empty" : "friends.empty"];

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

    public override void OnOpened()
    {
        ApplyDefault();
        if (OnNextendo)
        {
            Run(Launcher.RefreshNextendoFriendsAsync); // the list now, with the players met lately
        }
    }

    public override void OnClosed()
    {
        _focusName = null;
        (_localNote, _localNoteIsGood) = (null, false);
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

        if (OnNextendo)
        {
            BuildNextendo(wanted, Section);
        }
        else if (SignedIn && friends.Loaded)
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

    /// <summary>The lines of a Nextendo account's friends: REQUESTS, FRIENDS, SENT, PLAYERS YOU MET.</summary>
    private void BuildNextendo(List<(string Key, Func<FriendsRow> Create, Action<FriendsRow> Update)> wanted, Action<string, string> section)
    {
        var nx = Nx;
        if (!nx.Loaded)
        {
            return;
        }
        static string Count(int n) => n.ToString(CultureInfo.CurrentCulture);
        static string Id(long pid) => pid.ToString(CultureInfo.InvariantCulture);
        if (nx.Incoming.Count > 0)
        {
            section("requests", S.Format("friends.section.requests", ("n", Count(nx.Incoming.Count))));
            foreach (var request in nx.Incoming)
            {
                wanted.Add(("nxin:" + Id(request.Pid), () => new FriendRequestRow(NameOf(request), S["friends.wants"], Accept, Decline, request.Pid), row => Draw(row, request.Pid)));
            }
        }
        if (nx.Friends.Count > 0)
        {
            section("friends", S.Format("friends.section.friends", ("n", Count(nx.OnlineCount))));
            foreach (var friend in nx.Ordered(StringComparer.CurrentCultureIgnoreCase))
            {
                wanted.Add(("nx:" + Id(friend.Pid), () => new NextendoFriendRow(friend.Pid, OpenNextendoFriend), row =>
                {
                    var line = (NextendoFriendRow)row;
                    line.Name = friend.Name.Length > 0 ? friend.Name : CoreStrings.NextendoSomeone;
                    line.State = friend.State;
                    line.StateText = NextendoStateText(friend);
                    line.FriendCode = friend.FriendCode;
                    line.Favorite = friend.Favorite;
                    Draw(line, friend.Pid);
                }));
            }
        }
        if (nx.Sent.Count > 0)
        {
            section("sent", S.Format("friends.section.sent", ("n", Count(nx.Sent.Count))));
            foreach (var asked in nx.Sent)
            {
                wanted.Add(("nxout:" + Id(asked.Pid), () => new NextendoPersonRow("nxout:" + Id(asked.Pid), asked, NameOf(asked), S["friends.sent"], null, _ => { }), row => Draw(row, asked.Pid)));
            }
        }
        if (nx.Recent.Count > 0)
        {
            section("met", S.Format("friends.nx.section.met", ("n", Count(nx.Recent.Count))));
            foreach (var met in nx.Recent)
            {
                wanted.Add(("nxmet:" + Id(met.Pid), () => new NextendoPersonRow("nxmet:" + Id(met.Pid), met, NameOf(met), met.FriendCode, S["friends.add"], AddMet), row => Draw(row, met.Pid)));
            }
        }
    }

    /// <summary>The person's Nextendo picture and colour on their line (the picture arrives a moment after the list).</summary>
    private void Draw(FriendsRow row, long pid)
    {
        var look = Launcher.NextendoAvatarOf(pid);
        (row.Picture, row.Color) = (look.Path, look.Color);
    }

    private static string NameOf(NextendoPerson who) => who.Name.Length > 0 ? who.Name : who.FriendCode.Length > 0 ? who.FriendCode : CoreStrings.NextendoSomeone;

    /// <summary>"Playing MKW · Online", "Playing", "Online", "Offline"; "Account closed" for one Nextendo closed.</summary>
    private string NextendoStateText(NextendoFriend friend)
    {
        if (friend.Banned)
        {
            return S["friends.nx.banned"];
        }
        if (friend.State != FriendState.Playing)
        {
            return S[friend.State == FriendState.Online ? "friends.online" : "friends.offline"];
        }
        var playing = friend.Game.Length > 0 ? S.Format("friends.nx.playing_game", ("game", friend.Game)) : S["friends.nx.playing"];
        return friend.Detail.Length > 0 ? playing + " · " + friend.Detail : playing;
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
            chosen = Rows.FirstOrDefault(r => r is NextendoFriendRow nx && string.Equals(nx.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? Rows.FirstOrDefault(r => r is FriendRow friend && string.Equals(friend.Name, name, StringComparison.OrdinalIgnoreCase))
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
            NextendoFriendRow nx => nx.Name,
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
        if (!Busy)
        {
            FocusNextTo(row);
            Run(() => row.Pid != 0 ? Launcher.AcceptNextendoFriendAsync(row.Pid) : Launcher.AcceptFriendAsync(row.Name));
        }
    }

    private void Decline(FriendRequestRow row)
    {
        if (!Busy)
        {
            FocusNextTo(row);
            Run(() => row.Pid != 0 ? Launcher.DeclineNextendoFriendAsync(row.Pid) : Launcher.RemoveFriendAsync(row.Name, FriendRemoval.Reject));
        }
    }

    // ── on Nextendo ──

    /// <summary>"Add" on someone met in a race: a request to their friend code.</summary>
    private void AddMet(NextendoPersonRow row)
    {
        if (!Busy && row.Who.FriendCode.Length > 0)
        {
            FocusNextTo(row);
            Run(() => Launcher.AddNextendoFriendAsync(row.Who.FriendCode));
        }
    }

    /// <summary>A on a Nextendo friend: the small menu (favourite, remove, block).</summary>
    private void OpenNextendoFriend(NextendoFriendRow row)
    {
        var modal = new ModalViewModel { Title = row.Name, Body = row.FriendCode.Length > 0 ? row.StateText + "\n" + row.FriendCode : row.StateText };
        modal.Actions.Add(new ModalAction(S[row.Favorite ? "friends.nx.unfavorite" : "friends.nx.favorite"], () =>
        {
            _shell.CloseModal(modal);
            if (!Busy)
            {
                _focusName = row.Name; // it moves in the list: the focus goes with it
                Run(() => Launcher.FavoriteNextendoFriendAsync(row.Pid, !row.Favorite));
            }
        }));
        modal.Actions.Add(new ModalAction(S["friends.remove"], () =>
        {
            _shell.CloseModal(modal);
            Run(() => LeaveAsync(row, block: false));
        }));
        modal.Actions.Add(new ModalAction(S["friends.nx.block"], () =>
        {
            _shell.CloseModal(modal);
            Run(() => LeaveAsync(row, block: true));
        }));
        modal.Bottom = new ModalAction(S["common.close"], () => _shell.CloseModal(modal));
        _shell.ShowModal(modal);
    }

    /// <summary>Remove a Nextendo friend, or block them: both after a confirmation.</summary>
    private async Task LeaveAsync(NextendoFriendRow row, bool block)
    {
        var sure = await _shell.ConfirmAsync(
            S[block ? "friends.nx.block.title" : "friends.remove.title"],
            S.Format(block ? "friends.nx.block.body" : "friends.remove.body", ("name", row.Name)),
            S[block ? "friends.nx.block" : "friends.remove.yes"]).ConfigureAwait(true);
        if (sure && !Busy)
        {
            FocusNextTo(row);
            await (block ? Launcher.BlockNextendoPlayerAsync(row.Pid) : Launcher.RemoveNextendoFriendAsync(row.Pid)).ConfigureAwait(true);
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

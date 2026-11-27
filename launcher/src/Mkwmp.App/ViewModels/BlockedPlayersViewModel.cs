// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>A player blocked on Nextendo, as their row shows them: the name, the friend code, and "Unblock".</summary>
public sealed class BlockedPlayerRow
{
    internal BlockedPlayerRow(NextendoPerson who, Action<NextendoPerson> unblock)
    {
        Pid = who.Pid;
        Name = who.Name.Length > 0 ? who.Name : who.FriendCode.Length > 0 ? who.FriendCode : CoreStrings.NextendoSomeone;
        Detail = who.Name.Length > 0 ? who.FriendCode : "";
        UnblockCommand = new RelayCommand(() => unblock(who));
    }

    public long Pid { get; }

    public string Name { get; }

    /// <summary>Their friend code, under the name ("" when the name is all Nextendo gave, or the code is the name).</summary>
    public string Detail { get; }

    public ICommand UnblockCommand { get; }
}

/// <summary>
/// Settings › Nextendo › Blocked Players: the players the linked Nextendo account blocked (from the friends panel, or
/// anywhere else that account is used), each with "Unblock". A sub-page; the list is Nextendo's, asked for when the
/// page opens. A blocked player is no friend and cannot send a friend request.
/// </summary>
public sealed class BlockedPlayersViewModel : PageViewModel
{
    public BlockedPlayersViewModel(ShellViewModel shell)
        : base(shell) => Sync();

    public ObservableCollection<BlockedPlayerRow> Rows { get; } = [];

    public string Title => S["nextendo.blocked"];

    public string Caption => S["nextendo.blocked.caption"];

    public string UnblockLabel => S["nextendo.blocked.unblock"];

    /// <summary>"Nobody is blocked."</summary>
    public string EmptyText => S["nextendo.blocked.empty"];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>What the last unblock answered (or why the list does not come).</summary>
    public string Note => Launcher.NextendoFriends.Note ?? "";

    public bool NoteIsError => Launcher.NextendoFriends.NoteIsError;

    public override IReadOnlyList<Hint> Hints => IsEmpty
        ? [new Hint(PadButton.B, S["hint.back"])]
        : [new Hint(PadButton.B, S["hint.back"]), new Hint(PadButton.A, S["nextendo.blocked.unblock"])];

    /// <summary>The language changed: the same page again.</summary>
    public override PageViewModel? Recreate() => Launcher.NextendoFriends.Enabled ? new BlockedPlayersViewModel(Shell) : null;

    public override void OnEnter() => RelayCommand.Async(Launcher.RefreshNextendoFriendsAsync).Execute(null);

    public override void OnStateChanged()
    {
        var before = IsEmpty;
        Sync();
        base.OnStateChanged();
        if (before != IsEmpty)
        {
            RefreshHints();
        }
    }

    private void Unblock(NextendoPerson who)
    {
        if (!Launcher.NextendoFriends.Busy)
        {
            RelayCommand.Async(() => Launcher.UnblockNextendoPlayerAsync(who.Pid)).Execute(null);
        }
    }

    /// <summary>The rows follow Nextendo's list, each kept while its player is still blocked (the focus stays where it is).</summary>
    private void Sync()
    {
        var blocked = Launcher.NextendoFriends.Blocked;
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!blocked.Any(b => b.Pid == Rows[i].Pid))
            {
                Rows.RemoveAt(i);
            }
        }
        for (var i = 0; i < blocked.Count; i++)
        {
            if (i >= Rows.Count || Rows[i].Pid != blocked[i].Pid)
            {
                Rows.Insert(i, new BlockedPlayerRow(blocked[i], Unblock));
            }
        }
    }
}

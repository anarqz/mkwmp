// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.App.Controls;
using Mkwmp.App.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>A line of the friends panel.</summary>
public sealed record FriendLine(string Name, AvatarPresence Presence, string State, bool IsRequest);

/// <summary>
/// The friends panel that slides in from the right on any page (Y / RB / F). STUB of the design
/// system: it lists requests and friends. The friends agent owns this file and
/// Views/FriendsPanel.xaml(.cs).
/// </summary>
public sealed class FriendsPanelViewModel : LayerViewModel
{
    private readonly ShellViewModel _shell;
    private IReadOnlyList<FriendLine> _lines = [];

    public FriendsPanelViewModel(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Build();
    }

    public Strings S => _shell.S;

    public string Summary => S.Format("friends.summary",
        ("online", _shell.Launcher.Friends.OnlineCount.ToString(CultureInfo.CurrentCulture)),
        ("total", _shell.Launcher.Friends.Friends.Count.ToString(CultureInfo.CurrentCulture)));

    /// <summary>Requests first, then playing, online, offline. The same list instance while nothing changed (the focus stays).</summary>
    public IReadOnlyList<FriendLine> Lines => _lines;

    public override IReadOnlyList<Hint> Hints => [new Hint(PadButton.B, S["hint.close"]), new Hint(PadButton.A, S["hint.ok"])];

    public override void OnStateChanged()
    {
        Build();
        base.OnStateChanged();
    }

    private void Build()
    {
        var friends = _shell.Launcher.Friends;
        var lines = friends.Incoming.Select(r => new FriendLine(r.Name, AvatarPresence.None, S["friends.wants"], true))
            .Concat(friends.Friends.OrderBy(f => f.State).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Select(f => new FriendLine(
                f.Name,
                f.State switch { FriendState.Playing => AvatarPresence.Playing, FriendState.Online => AvatarPresence.Online, _ => AvatarPresence.Offline },
                S[f.State switch { FriendState.Playing => "friends.playing", FriendState.Online => "friends.online", _ => "friends.offline" }],
                false)))
            .ToList();
        if (!lines.SequenceEqual(_lines))
        {
            _lines = lines;
        }
    }
}

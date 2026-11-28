// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core;
using Mkwmp.Core.Overlay;

namespace Mkwmp.App.Design;

/// <summary>
/// The demo's current lobby and in-game toasts: six made-up players in an online lobby (on Nextendo Network or on the
/// demo's MKWMP server, after where the demo plays), in memory only. One kept the same Player ID under three names
/// since the session began ("Used 3 names"), another renamed once; the demo's own player is first ("You"). The real
/// <see cref="ToastBridge"/> and <see cref="GameOverlay"/> serve them as the game would be told
/// (<c>--toasts-json</c>, <c>--overlay-view-json</c>: the mod's test fixtures).
/// </summary>
public sealed partial class DesignLauncher
{
    /// <summary>The handle of the demo's player who used three names (the harness opens them: <c>--page lobby/player</c>).</summary>
    public const string DemoRenamedPlayer = "p5";

    private bool _demoLobby = true;

    public ToastBridge ToastBridge { get; } = new(TimeProvider.System);

    public GameOverlay GameOverlay { get; } = new(TimeProvider.System);

    public LobbyView Lobby => _demoLobby ? DemoLobby(NextendoActive) : LobbyView.Empty;

    /// <summary>For screenshots: in the demo's lobby, or out of any.</summary>
    public void PretendLobby(bool inLobby) => Change(() => _demoLobby = inLobby);

    private static LobbyView DemoLobby(bool nextendo)
    {
        static string Uid(int n) => $"{n:x8}-4c1e-5a2b-9d3f-{n * 7919L:x12}";
        var start = DateTimeOffset.UtcNow.AddMinutes(-25);
        LobbyMember Member(int n, bool local, params string[] names) => new("p" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Uid(10 + n), names[^1], local, [.. names.Select((name, i) => new LobbyName(name, start.AddMinutes(i * 7)))]);
        return new LobbyView(true, nextendo, nextendo ? "nextendo.network" : "mkw.example.net",
        [
            Member(1, true, "Alex"),
            Member(2, false, "Luna"),
            Member(3, false, "Mateus"),
            Member(4, false, "Sofia", "Sofi"),
            Member(5, false, "TurboShell", "xX_Drift_Xx", "NotTurbo"),
            Member(6, false, "Rin"),
        ]);
    }

    /// <summary>One toast of each look: a friend's news, an answer, a warning and an error (as the window and the game draw them).</summary>
    public void ShowDemoToasts()
    {
        Toast[] toasts =
        [
            new("Luna", "is racing online now", ToastKind.FriendPlaying),
            new("Report copied", "Paste it in a message to Nextendo staff.", ToastKind.Success) { Always = true },
            new("TurboShell changed their name", "Now xX_Drift_Xx, with the same Player ID.", ToastKind.Warning),
            new("Could not copy to the clipboard", "", ToastKind.Error),
        ];
        foreach (var toast in toasts)
        {
            ToastBridge.Offer(toast); // a launcher without a window: the game draws it (nothing once a window attached)
            Toasted?.Invoke(this, toast);
        }
    }
}

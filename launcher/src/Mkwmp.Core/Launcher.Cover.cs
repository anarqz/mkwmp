// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// The game's cover for the Play card (<see cref="ILauncher.GameCover"/>): the target makes it once
/// from the player's own copy of the game (<see cref="IGameTarget.CoverArt"/>) into
/// <c>cache\</c> beside the launcher, off every other thread; the window shows the file. It is
/// asked for again only when the install, or the copy its setup used, is another one. The picture
/// stays on this PC: it is in no report, no log and no request.
/// </summary>
public sealed partial class Launcher
{
    /// <summary>The folder of what the launcher can make again (beside the exe: the launcher is portable).</summary>
    public const string CacheFolderName = "cache";

    /// <summary>An install without a cover is asked again this often: its game may have been set up meanwhile (also by the player, in the game's own setup).</summary>
    private static readonly TimeSpan CoverRetry = TimeSpan.FromSeconds(30);

    private readonly Lock _coverGate = new();
    private volatile string? _gameCover;
    private string? _coverAsked;
    private long _coverAskedAt;
    private bool _coverWorking;

    public string? GameCover => _gameCover;

    /// <summary>
    /// After a look at the game folder (every two seconds): asks for the cover when this install, or the copy of the
    /// game its setup used, was not asked about yet; and now and then again while it has none.
    /// </summary>
    private void FollowCover(GameInstall? install, GameSetupStatus? setup)
    {
        var asked = install is null ? "" : $"{install.TargetId}|{install.Root}|{install.DataRoot}|{setup?.DumpPath}|{setup?.Needed is null}";
        lock (_coverGate)
        {
            var again = install is not null && _gameCover is null && _clock.GetElapsedTime(_coverAskedAt) >= CoverRetry;
            if (_coverWorking || (asked == _coverAsked && !again))
            {
                return;
            }
            _coverAsked = asked;
            _coverAskedAt = _clock.GetTimestamp();
            if (install is null)
            {
                if (_gameCover is null)
                {
                    return;
                }
                _gameCover = null;
            }
            else
            {
                _coverWorking = true;
            }
        }
        if (install is null)
        {
            RaiseChanged();
            return;
        }
        var target = _targets.Get(install.TargetId);
        var cache = Path.Combine(LauncherFolder, CacheFolderName);
        _ = Task.Run(() =>
        {
            string? cover = null;
            try
            {
                cover = target.CoverArt(install, cache);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException
                or IndexOutOfRangeException or OverflowException)
            {
                Note($"cover: {e.GetType().Name}"); // a damaged copy: the card keeps its own art
            }
            bool changed;
            lock (_coverGate)
            {
                changed = _gameCover != cover;
                _gameCover = cover;
                _coverWorking = false;
            }
            if (changed)
            {
                RaiseChanged();
            }
        });
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// The game's own settings (video, sound, controls...): whatever the selected
/// <see cref="IGameTarget"/> declares, read from and written to the game's files through it.
/// </summary>
public sealed partial class Launcher
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();
    private static readonly IReadOnlySet<string> NoIds = new HashSet<string>();

    /// <summary>One write to the game's settings at a time.</summary>
    private readonly Lock _gameSettingsWrite = new();
    private volatile GameSettingsView? _gameSettingsView;
    private string? _gameSettingsError;

    public GameSettingsView GameSettings => _gameSettingsView ?? RefreshGameSettings().View;

    /// <summary>
    /// Reads the settings again (the player changes them in the game too; the game starts and
    /// stops). True when the view is another one: the caller then raises <see cref="Changed"/>.
    /// </summary>
    private (GameSettingsView View, bool Changed) RefreshGameSettings()
    {
        var s = Settings;
        var target = _targets.Get(s.Target);
        var view = BuildGameSettings(target, GameFinder.Detect(target, s.Game));
        view = view with { Schema = WithOnlineLocks(target, view.Schema) };
        lock (_gate)
        {
            if (_gameSettingsError is { } error)
            {
                view = view with { Note = error, NoteIsError = true };
            }
            if (_gameSettingsView is { } old && ReferenceEquals(old.Schema, view.Schema) && old.Editable == view.Editable && old.Note == view.Note
                && old.NoteIsError == view.NoteIsError && old.Changed.SetEquals(view.Changed) && old.Values.Count == view.Values.Count
                && old.Values.All(p => view.Values.TryGetValue(p.Key, out var v) && v == p.Value))
            {
                return (old, false);
            }
            _gameSettingsView = view;
            return (view, true);
        }
    }

    private static GameSettingsView BuildGameSettings(IGameTarget target, GameInstall? install)
    {
        if (install is null)
        {
            return new GameSettingsView(null, NoValues, NoIds, false, Strings.SetGameFolder(target.FolderHint), false);
        }
        try
        {
            if (target.GameSettings(install) is not { } schema)
            {
                var game = install.Version is { } version ? $"{target.DisplayName} {version}" : target.DisplayName;
                return new GameSettingsView(null, NoValues, NoIds, false, Strings.GameSettingsNone(game), false);
            }
            var set = target.ReadGameSettings(install);
            var running = target.IsRunning(install);
            return new GameSettingsView(
                schema,
                schema.Effective(set),
                new HashSet<string>(set.Keys.Where(id => schema.Find(id) is not null), StringComparer.OrdinalIgnoreCase),
                !running,
                running ? Strings.GameSettingsCloseGame : null,
                false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new GameSettingsView(null, NoValues, NoIds, false, e.Message, true);
        }
    }

    private GameSettingsSchema? _lockSource;
    private bool _lockOnline;
    private GameSettingsSchema? _lockResult;

    private bool OnlineOn => ((ILauncher)this).OnlineEnabled;

    /// <summary>
    /// The target's settings that every player of an online race must share (mkw-recomp: the logic
    /// rate, 60 Hz) show as locked while a server is chosen. The same object is returned while
    /// nothing changed, so the two-second refresh stays quiet.
    /// </summary>
    private GameSettingsSchema? WithOnlineLocks(IGameTarget target, GameSettingsSchema? schema)
    {
        if (schema is null)
        {
            return null;
        }
        var online = OnlineOn;
        lock (_gate)
        {
            if (ReferenceEquals(schema, _lockSource) && online == _lockOnline && _lockResult is not null)
            {
                return _lockResult;
            }
        }
        var owned = target.OnlineOwnedSettings;
        var result = !online || owned.Count == 0 ? schema : schema with
        {
            Categories = [.. schema.Categories.Select(c => c with
            {
                Settings = [.. c.Settings.Select(x => x.Locked is null && owned.FirstOrDefault(o => string.Equals(o.Id, x.Id, StringComparison.OrdinalIgnoreCase)) is { Id: not null } o
                    ? x with { Locked = o.Why }
                    : x)],
            })],
        };
        lock (_gate)
        {
            (_lockSource, _lockOnline, _lockResult) = (schema, online, result);
        }
        return result;
    }

    /// <summary>A change to an online-owned setting is refused while online (a reset to the default is fine when that is the owned value).</summary>
    private void RefuseOnlineOwned(IGameTarget target, IReadOnlyDictionary<string, string?> edits)
    {
        if (!OnlineOn)
        {
            return;
        }
        foreach (var (id, value, why) in target.OnlineOwnedSettings)
        {
            if (edits.TryGetValue(id, out var wanted) && wanted is not null && wanted != value)
            {
                throw new LauncherException(why);
            }
        }
    }

    /// <summary>Before an online session: the online-owned settings get their value in the game's file.</summary>
    private void EnforceOnlineOwned(IGameTarget target, GameInstall install)
    {
        var owned = target.OnlineOwnedSettings;
        if (owned.Count == 0 || target.IsRunning(install))
        {
            return;
        }
        try
        {
            var current = target.ReadGameSettings(install);
            var edits = owned.Where(o => current.TryGetValue(o.Id, out var v) && v != o.Value)
                .ToDictionary(o => o.Id, o => (string?)o.Value, StringComparer.OrdinalIgnoreCase);
            if (edits.Count > 0)
            {
                target.WriteGameSettings(install, edits);
                Note($"game settings: {string.Join(", ", edits.Select(e => $"{e.Key} = {e.Value}"))} for online play");
            }
        }
        catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
        {
            Note($"game settings: online values not written: {e.Message}");
        }
    }

    /// <summary>For <see cref="RefreshGame"/>: the settings follow the game folder's two-second look.</summary>
    private void FollowGameSettings()
    {
        if (_gameSettingsView is not null && RefreshGameSettings().Changed)
        {
            RaiseChanged();
        }
    }

    public Task SetGameSettingAsync(string id, string value)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(value);
        return ChangeGameSettingsAsync(schema => schema.Changes(id, value));
    }

    public Task ResetGameSettingAsync(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return ChangeGameSettingsAsync(schema => schema.Changes(id, null));
    }

    public Task ResetGameSettingsAsync() => ChangeGameSettingsAsync(
        schema => schema.Settings.Where(s => s.Locked is null).ToDictionary(s => s.Id, _ => (string?)null, StringComparer.OrdinalIgnoreCase));

    /// <summary>Off the caller's thread (the window's): the look at the game's processes and the file write.</summary>
    private Task ChangeGameSettingsAsync(Func<GameSettingsSchema, IReadOnlyDictionary<string, string?>> changes) => Task.Run(() =>
    {
        lock (_gameSettingsWrite)
        {
            string? error = null;
            try
            {
                var s = Settings;
                var target = _targets.Get(s.Target);
                if (GameFinder.Detect(target, s.Game) is not { } install)
                {
                    throw new LauncherException(Strings.SetGameFolder(target.FolderHint));
                }
                if (target.GameSettings(install) is not { } schema)
                {
                    throw new LauncherException(Strings.GameSettingsNone(target.DisplayName));
                }
                var edits = changes(WithOnlineLocks(target, schema)!);
                RefuseOnlineOwned(target, edits);
                target.WriteGameSettings(install, edits);
            }
            catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
            {
                error = e.Message;
                Note($"game settings: {e.Message}");
            }
            lock (_gate)
            {
                _gameSettingsError = error;
            }
            RefreshGameSettings();
        }
        // Always: a refused change must put the window's row back to the value in effect.
        RaiseChanged();
    });
}

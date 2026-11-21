// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// The game's runtime, installed by the launcher: the selected target's pinned build
/// (<see cref="IGameTarget.Runtime"/>), through <see cref="RuntimeInstaller"/>. The launcher never
/// looks for a newer release than the pinned one, and never installs over an install that is
/// not MKWMP's build.
/// </summary>
public sealed partial class Launcher
{
    private readonly Lock _runtimeStart = new();

    /// <summary>The install in progress (under <see cref="_runtimeStart"/>).</summary>
    private CancellationTokenSource? _runtimeCancel;

    // ---- under _gate ----
    private RuntimeProgress? _runtimeProgress;
    private string? _runtimeBusyFolder;
    private (string Text, bool IsError)? _runtimeNote;
    /// <summary>Whether the game folder carries the installer's marker, looked up once per folder.</summary>
    /// <remarks>And whether its executable runs on this processor (<see cref="RuntimeManifest.Suits"/>: hashed once, and only on one without AVX2).</remarks>
    private (string Root, bool Managed, bool Suits)? _runtimeManaged;

    private volatile RuntimeView _runtimeView = RuntimeView.None;

    public RuntimeView Runtime => _runtimeView;

    /// <summary>The runtime this launcher installs for the selected target, if it has one.</summary>
    private RuntimeManifest? RuntimeManifestFor(IGameTarget target) => _options.Runtime ?? target.Runtime;

    /// <summary>
    /// Portable: everything the launcher keeps lives beside its exe (the user's rule, 2026-11-20). The
    /// runtime installs into <c>&lt;launcher&gt;\mkw-recomp</c> unless the player picks a folder, and its
    /// download waits in <c>&lt;launcher&gt;\downloads</c>.
    /// </summary>
    private string DataFolder => LauncherFolder;

    private string DefaultRuntimeFolder(RuntimeManifest manifest) => _options.RuntimeFolder ?? Path.Combine(DataFolder, manifest.FolderName);

    private RuntimeInstaller NewRuntimeInstaller(IGameTarget target, RuntimeManifest manifest) => new(
        manifest,
        _http,
        _userAgent,
        _options.RuntimeCache ?? Path.Combine(DataFolder, "downloads"),
        new RuntimeHost
        {
            Identify = folder => target.Detect(folder)?.Version,
            IsRunning = folder => target.Detect(folder) is { } install && target.IsRunning(install),
            KeepsExecutable = folder => target.Detect(folder)?.DevSha256 is not null,
            FreeSpace = _options.RuntimeFreeSpace ?? RuntimeInstaller.DriveFreeSpace,
            Clock = _clock,
        });

    /// <summary>The view, from the state (under the lock).</summary>
    private RuntimeView RuntimeNow()
    {
        var target = _targets.Get(_config.Settings.Target);
        if (RuntimeManifestFor(target) is not { } manifest)
        {
            return RuntimeView.None;
        }
        var install = _install is { } found && found.TargetId == target.Id ? found : null;
        var (ours, suits) = (false, true);
        if (install is not null)
        {
            if (_runtimeManaged is not { } known || known.Root != install.Root)
            {
                _runtimeManaged = known = (install.Root, RuntimeInstaller.ManagedVersion(install.Root) is not null, install.Version != manifest.Version || manifest.Suits(install.Root));
            }
            ours = known.Managed || manifest.SameLineage(install.Version);
            suits = known.Suits;
        }
        var folder = _runtimeBusyFolder ?? (ours ? install!.Root : DefaultRuntimeFolder(manifest));
        // The pinned version in the other build, when that one does not run here (a folder brought from a
        // processor with AVX2 to one without): updated in place to this processor's build.
        // A developer's build accepted as the pinned one (GameInstall.DevSha256) is never offered an update: it would replace it.
        var idle = install is null ? RuntimeState.NotInstalled
            : install.DevSha256 is not null ? RuntimeState.Installed
            : ours && (install.Version != manifest.Version || !suits) ? RuntimeState.UpdateAvailable
            : RuntimeState.Installed;
        var state = _runtimeProgress?.Phase switch
        {
            RuntimePhase.Downloading => RuntimeState.Downloading,
            RuntimePhase.Verifying => RuntimeState.Verifying,
            RuntimePhase.Extracting => RuntimeState.Extracting,
            RuntimePhase.Installing => RuntimeState.Installing,
            _ => _runtimeNote is { IsError: true } ? RuntimeState.Failed : idle,
        };
        var p = _runtimeProgress ?? default;
        return new RuntimeView(
            state,
            install?.Version,
            manifest.Version,
            folder,
            install is not null && !ours,
            p.Fraction,
            p.Done,
            p.Total,
            p.BytesPerSecond,
            p.Remaining,
            manifest.Size,
            manifest.Size + manifest.InstalledSize + manifest.SetupSpace,
            _runtimeNote?.Text,
            manifest.Credit.Localized())
        {
            PinnedVariant = manifest.VariantText,
            DevSha256 = install?.DevSha256,
        };
    }

    public async Task InstallRuntimeAsync(string? folder, CancellationToken cancel)
    {
        var target = _targets.Get(Settings.Target);
        if (RuntimeManifestFor(target) is not { } manifest)
        {
            Mutate(() => _runtimeNote = (Strings.RuntimeNone, true));
            return;
        }
        CancellationTokenSource both;
        lock (_runtimeStart)
        {
            if (_runtimeCancel is not null)
            {
                return; // one at a time: the window shows the one that runs
            }
            both = _runtimeCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel, _life.Token);
        }
        try
        {
            var where = folder is { Length: > 0 } ? folder : Runtime.Folder ?? DefaultRuntimeFolder(manifest);
            var installer = NewRuntimeInstaller(target, manifest);
            if (_seat is not null)
            {
                throw new LauncherException(Strings.PressStopFirst);
            }
            if (SetupRunning)
            {
                throw new LauncherException(Strings.SetupRunning); // its tools are the runtime's files
            }
            Note($"{manifest.Name} {manifest.Version}: installing into {where} ({manifest.Repository} {manifest.Tag}, {manifest.Asset})");
            Mutate(() =>
            {
                _runtimeNote = null;
                _runtimeBusyFolder = where;
                _runtimeProgress = new RuntimeProgress(RuntimePhase.Downloading, installer.Downloaded, manifest.Size, 0, null);
            });
            var shown = _clock.GetTimestamp();
            var progress = new RuntimeReport(p =>
            {
                // A few times a second is plenty for a progress bar; a new phase and its end always show.
                var now = _clock.GetTimestamp();
                lock (_gate)
                {
                    if (_runtimeProgress is { } last && last.Phase == p.Phase && p.Done < p.Total && _clock.GetElapsedTime(shown, now) < TimeSpan.FromMilliseconds(100))
                    {
                        return;
                    }
                }
                shown = now;
                Mutate(() => _runtimeProgress = p);
            });
            var result = await Task.Run(() => installer.InstallAsync(where, null, progress, both.Token), both.Token).ConfigureAwait(false);
            Note($"{manifest.Name} {result.Version} in {result.Folder}: {result.Added} added, {result.Replaced} replaced, {result.Kept} kept"
                + (result.Backup is null ? "" : $", backup in {result.Backup}"));
            // It becomes the game folder; the look at it is the same one any chosen folder gets.
            await SaveSettingsAsync(Settings with { Game = result.Folder, Target = target.Id }).ConfigureAwait(false);
            lock (_gate)
            {
                _runtimeManaged = null;
            }
            // The settings the launcher keeps for a runtime it installs (mkw-recomp: its own updater off, its start screen skipped).
            await Task.Run(
                () =>
                {
                    if (target.Detect(result.Folder) is { } installed)
                    {
                        target.ApplyOwnedSettings(installed);
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
            await Task.Run(RefreshGame, CancellationToken.None).ConfigureAwait(false);
            var label = result.Version == manifest.Version ? manifest.Label : result.Version;
            var said = result.Updated ? Strings.RuntimeUpdated(manifest.Name, label) : Strings.RuntimeInstalled(manifest.Name, label);
            Mutate(() => (_runtimeProgress, _runtimeBusyFolder, _runtimeNote) = (null, null, (said, false)));
        }
        catch (OperationCanceledException)
        {
            Note($"{manifest.Name}: install stopped");
            Mutate(() => (_runtimeProgress, _runtimeBusyFolder, _runtimeNote) = (null, null, (Strings.RuntimePaused, false)));
        }
        catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
        {
            Note($"{manifest.Name}: install failed: {e.Message}");
            Mutate(() => (_runtimeProgress, _runtimeBusyFolder, _runtimeNote) = (null, null, (e.Message, true)));
        }
        finally
        {
            lock (_runtimeStart)
            {
                _runtimeCancel = null;
            }
            both.Dispose();
        }
    }

    public Task CancelRuntimeInstall()
    {
        lock (_runtimeStart)
        {
            if (_runtimeCancel is { } running)
            {
                running.Cancel();
                return Task.CompletedTask;
            }
        }
        Mutate(() => _runtimeNote = null);
        return Task.CompletedTask;
    }

    /// <summary>Progress reported on the working thread itself (Progress&lt;T&gt; would post to a context and reorder).</summary>
    private sealed class RuntimeReport(Action<RuntimeProgress> report) : IProgress<RuntimeProgress>
    {
        public void Report(RuntimeProgress value) => report(value);
    }
}

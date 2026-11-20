// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// mkw-recomp (the static recompilation of Mario Kart World): the folder with mkw.exe. MKWMP gets
/// in either as the game patch the launcher keeps beside mkw.exe (winmm.dll, the probe, with
/// mkwmp-voice.dll and the real winmm.dll as winmm_host.dll) or, on runtimes that load mods, as
/// the MKWMP mod (<c>mods/mkwmp</c>), which then wins: the launcher takes its own patch out. Both
/// read <c>mkwmp.toml</c> beside mkw.exe and call the launcher's <c>/local/v1</c>.
/// </summary>
public sealed class MkwRecompTarget : IGameTarget
{
    public const string TargetId = "mkw-recomp";

    private readonly string _version;
    private readonly Action<string> _note;
    private IntegrationPackage? _lastPackage;

    /// <param name="launcherVersion">This launcher's version: what "Patches installed (v…)" names.</param>
    /// <param name="note">Where to tell what was done (the session log).</param>
    public MkwRecompTarget(string launcherVersion, Action<string>? note = null)
    {
        _version = launcherVersion;
        _note = note ?? (_ => { });
    }

    /// <summary>Tests: whether the game "runs", instead of looking for a real mkw.exe process.</summary>
    internal Func<bool>? RunningOverride { get; set; }

    public string Id => TargetId;

    public string DisplayName => "mkw-recomp";

    public string FolderHint => "the one with mkw.exe";

    public TargetCapabilities Capabilities =>
        TargetCapabilities.Friends | TargetCapabilities.VoiceChat | TargetCapabilities.Camera | TargetCapabilities.FriendRooms | TargetCapabilities.Launch;

    public IReadOnlyList<PackageAsset> PackageAssets { get; } =
    [
        new(Probe.ProbeDll, Required: true, "game patch"),
        new(Probe.Voice, Required: false, "voice chat"),
    ];

    public bool KeepsOwnIntegration => Probe.DevProbe;

    /// <summary>The mkw-recomp build the launcher installs: one pinned release of MKWMP's fork.</summary>
    public RuntimeManifest? Runtime => RecompRuntime.Pinned;

    public bool NeedsPackage(GameInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return Probe.DevProbe || Recomp.ActiveMod(install.Root) is null;
    }

    // ---- the game's setup, run by the launcher (RecompSetup) ---------------------------------

    /// <summary>Tests: the process started instead of the install's Python, for the given prepare.py arguments.</summary>
    internal Func<IReadOnlyList<string>, ProcessStartInfo>? SetupCommandOverride { get; set; }

    /// <summary>Tests: free bytes on a path's drive.</summary>
    internal Func<string, long?>? SetupFreeSpace { get; set; }

    public GameSetupStatus? SetupStatus(GameInstall install) => RecompSetup.Status(install);

    public IReadOnlyList<string> ApplyOwnedSettings(GameInstall install)
    {
        var written = RecompSettings.EnforceOwned(this, install);
        foreach (var line in written)
        {
            _note($"mkw.ini: {line} (kept by MKWMP)");
        }
        return written;
    }

    public async Task<GameSetupResult> RunSetupAsync(GameInstall install, GameSetupRequest request, IProgress<GameSetupProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(request);
        // Before the setup: its own start screen is never the one to offer an update or to open in the launcher's place.
        ApplyOwnedSettings(install);
        var result = await RecompSetup.RunAsync(
            install,
            request,
            new RecompSetupHost
            {
                IsRunning = () => IsRunning(install),
                FreeSpace = SetupFreeSpace ?? RuntimeInstaller.DriveFreeSpace,
                Command = SetupCommandOverride,
            },
            progress,
            cancel).ConfigureAwait(false);
        if (result.Ok)
        {
            ApplyOwnedSettings(install); // prepare.py rewrites mkw.ini for the game's data: checked once more
        }
        return result;
    }

    public GameInstall? Detect(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }
            var root = Path.GetFullPath(folder);
            var exe = Path.Combine(root, Recomp.Exe);
            if (!File.Exists(exe))
            {
                return null;
            }
            return new GameInstall(TargetId, root, exe, Recomp.Identify(root).Version, Recomp.GameVersion(root), Recomp.DataDir(root));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The usual places a player extracts a download to, and where Steam keeps games (a non-Steam shortcut often points into a library).</summary>
    public IEnumerable<string> SearchRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.Combine(profile, "Downloads");
        yield return Path.Combine(profile, "Downloads", "GAMES");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (RuntimeInstaller.DataFolder is { } installs)
        {
            yield return installs; // where the launcher installs mkw-recomp itself
        }
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            drives = [];
        }
        foreach (var drive in drives.Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            yield return Path.Combine(drive.RootDirectory.FullName, "Games");
        }
        foreach (var library in SteamLibraries())
        {
            yield return Path.Combine(library, "steamapps", "common");
        }
    }

    /// <summary>Steam's library folders, read from its files only (Steam itself is never started or asked).</summary>
    internal static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        try
        {
            if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string path)
            {
                roots.Add(path.Replace('/', '\\'));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // no Steam for this user
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (var root in roots.ToArray())
        {
            try
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    roots.AddRange(LibraryPaths(File.ReadAllText(vdf)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // unreadable: only the root itself
            }
        }
        return roots.Where(r => r.Length > 0 && seen.Add(r.TrimEnd('\\', '/')));
    }

    /// <summary><c>"path"  "D:\\SteamLibrary"</c> lines of Steam's libraryfolders.vdf.</summary>
    internal static IEnumerable<string> LibraryPaths(string vdf)
    {
        foreach (var line in vdf.Split('\n'))
        {
            var parts = line.Split('"').Where(s => s.Trim().Length > 0).ToArray();
            if (parts.Length >= 2 && parts[0] == "path")
            {
                yield return parts[1].Replace(@"\\", @"\", StringComparison.Ordinal);
            }
        }
    }

    public TargetCheck Check(GameInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        var game = install.Root;
        var id = Recomp.Identify(game);
        if (id.Kind == RecompKind.Missing)
        {
            return new TargetCheck(TargetStatus.Missing, id.Problem, id.Explain, null, null);
        }
        if (!id.Ok)
        {
            return new TargetCheck(TargetStatus.Unsupported, id.Problem, id.Explain, new Uri(Recomp.Download), $"GET {Recomp.Recommended}");
        }
        if (Recomp.GameProblemFor(Recomp.GameVersion(game)) is { } version)
        {
            return new TargetCheck(TargetStatus.Unsupported, version, null, null, null);
        }
        // An install the launcher sets up itself has no "open mkw-recomp once": SetupStatus says what to run instead.
        if (!RecompSetup.Drives(install) && Recomp.RebuildProblemFor(id, Recomp.GameId(game)) is { } rebuild)
        {
            return new TargetCheck(TargetStatus.NeedsRebuild, rebuild, null, null, null);
        }
        return new TargetCheck(TargetStatus.Ready, null, id.Explain, null, null);
    }

    private static Expected? ExpectedOf(IntegrationPackage? package) =>
        package is not null && package.Sha256.TryGetValue(Probe.ProbeDll, out var probe)
            ? new Expected(package.Version, probe, package.Sha256.GetValueOrDefault(Probe.Voice))
            : null;

    public IntegrationReport Inspect(GameInstall install, IntegrationPackage? package)
    {
        ArgumentNullException.ThrowIfNull(install);
        var game = install.Root;
        if (package is not null && package.Files.Count > 0)
        {
            _lastPackage = package;
        }
        if (!Probe.DevProbe && Recomp.ActiveMod(game) is { } dir)
        {
            var lines = new List<string> { $"mods/{Recomp.Mod}: enabled ({dir})" };
            if (File.Exists(Path.Combine(game, Probe.Marker)) || File.Exists(Path.Combine(game, Probe.Voice)))
            {
                lines.Add("the launcher's game patch is still beside mkw.exe: close the game, it goes on the next PLAY");
            }
            return new IntegrationReport(IntegrationState.Mod, Probe.Summary(IntegrationState.Mod, _version), lines);
        }
        return Probe.Check(game, ExpectedOf(package), _version);
    }

    public Task<IntegrationReport> InstallAsync(GameInstall install, IntegrationPackage package, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(package);
        return Task.Run(
            () =>
            {
                var game = install.Root;
                var running = IsRunning(install);
                if (!Probe.DevProbe && Recomp.ActiveMod(game) is { } dir)
                {
                    // The mod wins: no launcher game patch beside mkw.exe.
                    _note($"game patch: MKWMP runs as a mod ({dir})");
                    foreach (var done in Probe.RemoveForMod(game, running))
                    {
                        _note($"game patch: {done} (the mod carries it)");
                    }
                }
                else
                {
                    InstallBundle(game, package, running);
                }
                SyncWineOverride(game);
                return Inspect(install, package);
            },
            cancel);
    }

    /// <summary>The package's file, checked once more against the signed hash: the copy on disk may have changed since it was verified.</summary>
    private static byte[]? Verified(IntegrationPackage package, string name, string label)
    {
        if (!package.Files.TryGetValue(name, out var path) || !package.Sha256.TryGetValue(name, out var want))
        {
            return null;
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            return Updater.Sha256Hex(bytes) == want ? bytes : throw new LauncherException($"{name}: checksum mismatch");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"installing the {label} failed: {e.Message}", e);
        }
    }

    private void InstallBundle(string game, IntegrationPackage package, bool running)
    {
        var tag = $"v{package.Version}";
        var probe = Verified(package, Probe.ProbeDll, "game patch") ?? throw new LauncherException("the release lists no game patch");
        try
        {
            switch (Probe.InstallProbe(game, probe, running, out var backup))
            {
                case ProbeAction.Installed:
                    _note($"installed the game patch {tag}");
                    if (backup is not null)
                    {
                        _note($"your previous winmm.dll is saved as {backup}");
                    }
                    break;
                case ProbeAction.GameRunning:
                    throw new LauncherException("close the game first: a game patch update is ready");
                default:
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"installing the game patch failed: {e.Message}", e);
        }
        if (Verified(package, Probe.Voice, "voice chat") is { } voice)
        {
            try
            {
                switch (Probe.InstallFile(game, Probe.Voice, voice, running))
                {
                    case ProbeAction.Installed:
                        _note($"installed voice chat {tag}");
                        break;
                    case ProbeAction.GameRunning:
                        throw new LauncherException("close the game first: a voice chat update is ready");
                    default:
                        break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException($"installing voice chat failed: {e.Message}", e);
            }
        }
        if (Probe.EnsureHost(game) is { } message)
        {
            _note($"winmm_host.dll: {message}");
        }
    }

    public Task UninstallAsync(GameInstall install, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(install);
        return Task.Run(
            () =>
            {
                foreach (var line in Probe.Uninstall(install.Root, IsRunning(install)))
                {
                    _note($"uninstall: {line}");
                }
                SyncWineOverride(install.Root);
            },
            cancel);
    }

    public void WriteSession(GameInstall install, SessionFile session)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            // Always the current server's: a stale file from another server would make the game reject this one's lobby.
            Probe.WriteLobbyCa(install.Root, session.LobbyCaPem);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"writing {Probe.LobbyCa}: {e.Message}", e);
        }
        try
        {
            Probe.WriteSessionText(install.Root, Probe.SessionText(session));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"writing mkwmp.toml: {e.Message}", e);
        }
    }

    public void ForgetTicket(GameInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        try
        {
            Probe.ForgetTicket(install.Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(e.Message, e);
        }
    }

    /// <summary>
    /// Starts the game in its folder: the player's command when set (through Proton on the Steam
    /// Deck, where the launcher itself is the Steam shortcut), else <c>mkw.exe --play</c>.
    /// </summary>
    public IGameProcess? Launch(GameInstall install, LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(options);
        SyncWineOverride(install.Root);
        var command = options.CustomCommand?.Trim() ?? "";
        var start = command.Length == 0
            ? new ProcessStartInfo(install.Executable, "--play")
            // /S: cmd takes off exactly the outer quotes, so the command arrives as the player wrote it.
            : new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } cmd ? cmd : "cmd.exe", $"/S /C \"{command}\"") { CreateNoWindow = true };
        start.WorkingDirectory = install.Root;
        start.UseShellExecute = false;
        try
        {
            return System.Diagnostics.Process.Start(start) is { } process ? new GameProcess(process) : throw new LauncherException("starting mkw.exe: no process");
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            throw new LauncherException($"starting mkw.exe: {e.Message}", e);
        }
    }

    /// <summary>
    /// Under Wine/Proton the probe (winmm.dll beside mkw.exe) loads only with the per-app DLL
    /// override <c>winmm=native,builtin</c> for mkw.exe (Infinity Launcher found the environment
    /// variable alone never reaches the child): set while our probe is there and the mod does not
    /// carry the patch, removed otherwise. On Windows nothing happens.
    /// </summary>
    private void SyncWineOverride(string game)
    {
        if (!Platform.WineHost.Detected)
        {
            return;
        }
        var want = File.Exists(Path.Combine(game, Probe.ProbeDll)) && (Probe.DevProbe || Recomp.ActiveMod(game) is null);
        _note($"wine: winmm override for mkw.exe: {Platform.WineHost.EnsureDllOverride("mkw.exe", "winmm", want)}");
    }

    /// <summary>The running mkw.exe processes of this Windows session (or Wine prefix).</summary>
    public IReadOnlyList<int> ProcessIds(GameInstall install)
    {
        if (RunningOverride is not null)
        {
            return [];
        }
        try
        {
            var found = System.Diagnostics.Process.GetProcessesByName("mkw");
            var ids = found.Select(p => p.Id).ToList();
            foreach (var p in found)
            {
                p.Dispose();
            }
            return ids;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Is mkw.exe running (also when Steam or the player started it)?</summary>
    public bool IsRunning(GameInstall install)
    {
        if (RunningOverride is { } test)
        {
            return test();
        }
        try
        {
            var found = System.Diagnostics.Process.GetProcessesByName("mkw");
            foreach (var p in found)
            {
                p.Dispose();
            }
            if (found.Length > 0)
            {
                return true;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // the Linux process list below may still tell
        }
        // Under Wine / Proton the process list is this prefix's only: a game Steam started from its own shortcut
        // (another prefix) shows in Linux's list (as the Rust launcher looked on Linux).
        return Platform.WineHost.LinuxRoot is { } root && ProcHasGame(Path.Combine(root, "proc"));
    }

    /// <summary>
    /// Whether a process in Linux's <c>/proc</c> (<paramref name="proc"/>, as Wine maps it) was
    /// started with an argument that names mkw.exe: <c>&lt;pid&gt;/cmdline</c> holds the arguments,
    /// each ended by a NUL (Wine's own process for the game, or Proton's wrapper around it).
    /// </summary>
    internal static bool ProcHasGame(string proc)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(proc))
            {
                if (!Path.GetFileName(dir).All(char.IsAsciiDigit))
                {
                    continue;
                }
                try
                {
                    var arguments = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "cmdline"))).Split('\0');
                    if (arguments.Any(a => a.Split('/', '\\')[^1].Equals(Recomp.Exe, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the process ended meanwhile, or is another user's
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no /proc to read
        }
        return false;
    }

    public IEnumerable<string> NativeLibraryFolders(GameInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        yield return Path.Combine(install.DataRoot ?? install.Root, "mods", Recomp.Mod, "plugin");
        if (install.DataRoot is { } data && !string.Equals(data, install.Root, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(install.Root, "mods", Recomp.Mod, "plugin");
        }
        yield return install.Root;
    }

    public IEnumerable<SupportItem> SupportItems(GameInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        var game = install.Root;
        yield return new SupportItem("mkwmp.log", Path.Combine(game, "mkwmp.log"), null);
        // The game process's own probe log when the recomp start screen holds mkwmp.log.
        yield return new SupportItem("mkwmp-game.log", Path.Combine(game, "mkwmp-game.log"), null);
        // mkw-recomp's own log lives in its data folder (0.1.12 can move it).
        var data = Recomp.DataDir(game);
        yield return new SupportItem("mkw.log", Path.Combine(data, "logs", "mkw.log"), null);
        yield return new SupportItem("mkw.1.log", Path.Combine(data, "logs", "mkw.1.log"), null);
        string? toml = null;
        try
        {
            var path = Path.Combine(game, Probe.SessionToml);
            toml = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // not readable: left out
        }
        if (toml is not null)
        {
            yield return new SupportItem(Probe.SessionToml, null, SupportBundle.RedactToml(toml));
        }
        // Against the package last seen (the zip's signed list when there is one): no network here.
        var report = Inspect(install, _lastPackage);
        yield return new SupportItem(
            SupportBundle.SystemFile,
            null,
            $"game folder: {game}\nmkw-recomp data folder: {data}\nmkw-recomp: {Recomp.Identify(game)}\n{Recomp.GameLine(game)}\n"
            + $"{DllLine(game, Probe.ProbeDll)}\n{DllLine(game, Probe.Voice)}\nwinmm_host.dll: {(File.Exists(Path.Combine(game, Probe.Host)) ? "present" : "missing")}\n"
            + $"patch check: {report.Summary}\n  {string.Join("\n  ", report.Lines)}\n");
    }

    private static string DllLine(string game, string name)
    {
        var path = Path.Combine(game, name);
        if (!File.Exists(path))
        {
            return $"{name}: missing";
        }
        string sha;
        try
        {
            sha = Updater.Sha256Hex(File.ReadAllBytes(path))[..8];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            sha = "?";
        }
        return $"{name}: present, sha256 {sha}";
    }

    private sealed class GameProcess(System.Diagnostics.Process process) : IGameProcess
    {
        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                    return true;
                }
            }
        }

        public int? ExitCode
        {
            get
            {
                try
                {
                    return process.HasExited ? process.ExitCode : null;
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                    return null;
                }
            }
        }

        public void Dispose() => process.Dispose();
    }
}

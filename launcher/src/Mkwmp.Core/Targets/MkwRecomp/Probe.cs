// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Buffers.Binary;
using System.Text;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core.Targets.MkwRecomp;

internal enum ProbeAction
{
    UpToDate,

    /// <summary>Wrote the file.</summary>
    Installed,

    /// <summary>The game is running; the file is left alone.</summary>
    GameRunning,
}

/// <summary>The hashes this launcher's game patch must have (from the release zip's <c>game/</c> folder, or this launcher's own signed SHA256SUMS).</summary>
internal sealed record Expected(string Version, string Probe, string? Voice);

/// <summary>
/// Keeping the game patch in place: <c>&lt;game&gt;/winmm.dll</c> is this release's probe, and
/// <c>&lt;game&gt;/winmm_host.dll</c> is the real winmm.dll the probe forwards to (the system's on
/// Windows, Proton's under SteamOS; never shipped by us).
/// </summary>
internal static class Probe
{
    public const string ProbeDll = "winmm.dll";
    public const string Host = "winmm_host.dll";

    /// <summary>The voice engine the probe loads (native/voice), next to winmm.dll.</summary>
    public const string Voice = "mkwmp-voice.dll";
    public const string Marker = ".mkwmp-probe";
    public const string Backup = "winmm.dll.bak";
    public const string SessionToml = "mkwmp.toml";

    /// <summary>The lobby certificate the probe trusts, next to mkwmp.toml (the probe's default <c>lobby_ca</c> name). Without it the probe trusts its built-in certificate.</summary>
    public const string LobbyCa = "mkwmp-lobby-ca.pem";

    /// <summary><c>MKWMP_DEV_PROBE=1</c>: keep the game folder's own probe (a local build): no patch install on PLAY, and PLAY accepts any mkwmp probe there.</summary>
    public const string DevProbeEnv = "MKWMP_DEV_PROBE";

    /// <summary>Files only mkwmp puts in the game folder (removed by <see cref="Uninstall"/>), besides winmm.dll, which is removed only when it is ours.</summary>
    private static readonly string[] Ours =
    [
        Host, Voice, Marker, SessionToml, "mkwmp.toml.tmp", LobbyCa, "mkwmp.log", "winmm.dll.mkwmp-new", "mkwmp-voice.dll.mkwmp-new",
    ];

    public static bool DevProbe => Environment.GetEnvironmentVariable(DevProbeEnv) == "1";

    private static byte[]? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? HashOf(string path) => Read(path) is { } b ? Updater.Sha256Hex(b) : null;

    private static void Replace(string path, string tmp, ReadOnlySpan<byte> bytes)
    {
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Puts <paramref name="bytes"/> (already verified) at <c>&lt;game&gt;/winmm.dll</c> unless it is
    /// there. A winmm.dll we did not write before is kept as winmm.dll.bak (its path comes back
    /// in <paramref name="backup"/>): some players have another mod's winmm.dll there.
    /// </summary>
    public static ProbeAction InstallProbe(string game, ReadOnlySpan<byte> bytes, bool running, out string? backup)
    {
        backup = null;
        var path = Path.Combine(game, ProbeDll);
        var current = Read(path);
        var want = Updater.Sha256Hex(bytes);
        if (current is not null && Updater.Sha256Hex(current) == want)
        {
            return ProbeAction.UpToDate;
        }
        if (running)
        {
            return ProbeAction.GameRunning;
        }
        var marker = Path.Combine(game, Marker);
        if (current is not null && ReadMarker(marker) != Updater.Sha256Hex(current))
        {
            var bak = Path.Combine(game, Backup);
            if (!File.Exists(bak))
            {
                File.WriteAllBytes(bak, current);
                backup = bak;
            }
        }
        Replace(path, Path.Combine(game, "winmm.dll.mkwmp-new"), bytes);
        File.WriteAllText(marker, want);
        return ProbeAction.Installed;
    }

    private static string? ReadMarker(string marker)
    {
        try
        {
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Puts <paramref name="bytes"/> (already verified) at <c>&lt;game&gt;/&lt;name&gt;</c> unless it is there: a file only mkwmp ships (the voice engine), so nothing is backed up.</summary>
    public static ProbeAction InstallFile(string game, string name, ReadOnlySpan<byte> bytes, bool running)
    {
        var path = Path.Combine(game, name);
        if (HashOf(path) == Updater.Sha256Hex(bytes))
        {
            return ProbeAction.UpToDate;
        }
        if (running)
        {
            return ProbeAction.GameRunning;
        }
        Replace(path, Path.Combine(game, $"{name}.mkwmp-new"), bytes);
        return ProbeAction.Installed;
    }

    public static string Summary(IntegrationState state, string version) => state switch
    {
        IntegrationState.Installed => $"Patches installed (v{version})",
        IntegrationState.Unverified => "Patches installed (not verified: offline)",
        IntegrationState.Missing => "Not installed: press INSTALL",
        IntegrationState.Outdated => "Outdated (other version): press INSTALL to update",
        IntegrationState.Damaged => "Modified or damaged: press INSTALL to repair",
        IntegrationState.HostMissing => "winmm_host.dll missing: press INSTALL",
        IntegrationState.Dev => $"Dev probe kept ({DevProbeEnv}=1)",
        _ => "MKWMP runs as a mod in this game",
    };

    /// <summary>Is <c>&lt;game&gt;/winmm.dll</c> the probe we wrote (its hash is in <c>.mkwmp-probe</c>)?</summary>
    private static bool ProbeIsOurs(string game) =>
        Read(Path.Combine(game, ProbeDll)) is { } current && ReadMarker(Path.Combine(game, Marker)) is { } mark && mark == Updater.Sha256Hex(current);

    /// <summary>Is this an mkwmp probe, whoever copied it (it logs "mkwmp probe &lt;version&gt;")?</summary>
    internal static bool LooksLikeProbe(ReadOnlySpan<byte> bytes) => bytes.StartsWith("MZ"u8) && bytes.IndexOf("mkwmp probe "u8) >= 0;

    /// <summary>A real winmm.dll: a PE whose export table names timeBeginPeriod and timeEndPeriod (what the probe forwards to).</summary>
    internal static bool LooksLikeWinmm(ReadOnlySpan<byte> bytes) =>
        PeExports(bytes) is { } names && names.Contains("timeBeginPeriod") && names.Contains("timeEndPeriod");

    /// <summary>
    /// The exported names of a PE file (null: not a PE, or malformed). The export table is read,
    /// not the bytes searched: our own probe carries those two names as strings too.
    /// </summary>
    internal static List<string>? PeExports(ReadOnlySpan<byte> b)
    {
        static int? U16(ReadOnlySpan<byte> d, long o) => o >= 0 && o + 2 <= d.Length ? BinaryPrimitives.ReadUInt16LittleEndian(d[(int)o..]) : null;
        static long? U32(ReadOnlySpan<byte> d, long o) => o >= 0 && o + 4 <= d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(d[(int)o..]) : null;

        if (!b.StartsWith("MZ"u8) || U32(b, 0x3c) is not { } pe || pe + 4 > b.Length || !b[(int)pe..].StartsWith("PE\0\0"u8))
        {
            return null;
        }
        var coff = pe + 4;
        var opt = coff + 20;
        if (U16(b, coff + 2) is not { } sections || U16(b, coff + 16) is not { } optSize || U16(b, opt) is not { } magic)
        {
            return null;
        }
        long dirs;
        switch (magic)
        {
            case 0x10b: dirs = opt + 96; break; // PE32
            case 0x20b: dirs = opt + 112; break; // PE32+
            default: return null;
        }
        if (U32(b, dirs) is not { } exportRva)
        {
            return null;
        }
        if (exportRva == 0)
        {
            return [];
        }
        var table = opt + optSize;
        long? ToOffset(ReadOnlySpan<byte> d, long rva)
        {
            for (var i = 0; i < Math.Min(sections, 96); i++)
            {
                var h = table + (i * 40);
                if (U32(d, h + 8) is not { } vsize || U32(d, h + 12) is not { } va || U32(d, h + 16) is not { } rawSize || U32(d, h + 20) is not { } raw)
                {
                    continue;
                }
                if (rva >= va && rva < va + Math.Max(vsize, rawSize))
                {
                    return raw + (rva - va);
                }
            }
            return null;
        }
        if (ToOffset(b, exportRva) is not { } exp || U32(b, exp + 24) is not { } rawCount || U32(b, exp + 32) is not { } namesRva || ToOffset(b, namesRva) is not { } names)
        {
            return null;
        }
        var count = (int)Math.Min(rawCount, 10_000);
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            if (U32(b, names + (i * 4)) is not { } nameRva || ToOffset(b, nameRva) is not { } at || at >= b.Length)
            {
                return null;
            }
            var tail = b[(int)at..];
            var end = tail[..Math.Min(256, tail.Length)].IndexOf((byte)0);
            if (end < 0)
            {
                return null;
            }
            list.Add(Encoding.UTF8.GetString(tail[..end]));
        }
        return list;
    }

    /// <summary>Checks the game folder against <paramref name="expected"/> (null: compare with nothing, only that our own files are complete).</summary>
    public static IntegrationReport Check(string game, Expected? expected, string launcherVersion) => Check(game, expected, launcherVersion, DevProbe);

    internal static IntegrationReport Check(string game, Expected? expected, string launcherVersion, bool devProbe)
    {
        IntegrationReport Report(IntegrationState state, List<string> lines) => new(state, Summary(state, launcherVersion), lines);

        var files = new List<string>();
        var probeBytes = Read(Path.Combine(game, ProbeDll));
        var probe = probeBytes is null ? null : Updater.Sha256Hex(probeBytes);
        var voice = HashOf(Path.Combine(game, Voice));
        var host = Read(Path.Combine(game, Host));
        var ours = ProbeIsOurs(game) || (probeBytes is not null && LooksLikeProbe(probeBytes));
        if (probe is null && voice is null && !Ours.Where(f => f != Host).Any(f => File.Exists(Path.Combine(game, f))))
        {
            return Report(IntegrationState.Missing, ["no mkwmp file in the game folder"]);
        }
        var (outdated, damaged) = (false, false);
        if (probe is null)
        {
            files.Add("winmm.dll: missing");
            damaged = true;
        }
        else if (expected is not null && probe == expected.Probe)
        {
            files.Add($"winmm.dll: v{expected.Version}, verified");
        }
        else if (expected is not null && ours)
        {
            files.Add("winmm.dll: another mkwmp version");
            outdated = true;
        }
        else if (expected is null && ours)
        {
            files.Add("winmm.dll: mkwmp's (not verified)");
        }
        else
        {
            files.Add("winmm.dll: not mkwmp's, or changed");
            damaged = true;
        }

        if (voice is null)
        {
            // A release without voice chat expects none.
            if (expected is not { Voice: null })
            {
                files.Add("mkwmp-voice.dll: missing");
                damaged = true;
            }
        }
        else if (expected is null)
        {
            files.Add("mkwmp-voice.dll: present (not verified)");
        }
        else if (expected.Voice == voice)
        {
            files.Add($"mkwmp-voice.dll: v{expected.Version}, verified");
        }
        else
        {
            files.Add("mkwmp-voice.dll: another version, or changed");
            outdated = true;
        }

        bool hostOk;
        if (host is null)
        {
            files.Add("winmm_host.dll: missing");
            hostOk = false;
        }
        else if (Updater.Sha256Hex(host) == probe || !LooksLikeWinmm(host))
        {
            files.Add("winmm_host.dll: not the system's winmm.dll");
            damaged = true;
            hostOk = false;
        }
        else
        {
            files.Add("winmm_host.dll: the system's winmm.dll");
            hostOk = true;
        }
        if (expected is not null)
        {
            foreach (var other in new[] { Backup, "winmm.dll.mkwmp-new", Host })
            {
                if (HashOf(Path.Combine(game, other)) == expected.Probe)
                {
                    files.Add($"note: this version's probe is also at {other}");
                }
            }
        }
        var dev = devProbe && hostOk && probeBytes is not null && LooksLikeProbe(probeBytes);
        if (dev)
        {
            files.Add($"note: {DevProbeEnv}=1 keeps this winmm.dll");
        }
        var state = dev && (damaged || outdated || expected is null) ? IntegrationState.Dev
            : damaged ? IntegrationState.Damaged
            : outdated ? IntegrationState.Outdated
            : !hostOk ? IntegrationState.HostMissing
            : expected is not null ? IntegrationState.Installed
            : IntegrationState.Unverified;
        return Report(state, files);
    }

    private static void Remove(string game, string name, List<string> done)
    {
        var path = Path.Combine(game, name);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                done.Add($"removed {name}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"remove {path}: {e.Message}", e);
        }
    }

    private static void RestoreBackup(string game, List<string> done)
    {
        var bak = Path.Combine(game, Backup);
        if (!File.Exists(bak))
        {
            return;
        }
        try
        {
            File.Move(bak, Path.Combine(game, ProbeDll));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"restore {bak}: {e.Message}", e);
        }
        done.Add("put your previous winmm.dll back");
    }

    /// <summary>
    /// Takes the game folder back to how it was before mkwmp: our winmm.dll goes (a winmm.dll.bak
    /// we saved is put back), and every file only we write. What it did, one line per file.
    /// </summary>
    public static List<string> Uninstall(string game, bool running)
    {
        if (running)
        {
            throw new LauncherException("Close the game first");
        }
        if (!File.Exists(Path.Combine(game, Recomp.Exe)))
        {
            throw new LauncherException("Set the game folder (the one with mkw.exe)");
        }
        var done = new List<string>();
        if (ProbeIsOurs(game))
        {
            Remove(game, ProbeDll, done);
            RestoreBackup(game, done);
        }
        else if (File.Exists(Path.Combine(game, ProbeDll)))
        {
            done.Add("left winmm.dll alone (not mkwmp's)");
        }
        foreach (var f in Ours)
        {
            Remove(game, f, done);
        }
        return done;
    }

    /// <summary>
    /// The MKWMP mod is loaded: takes out the launcher's game patch beside mkw.exe so the two
    /// never run together. Only our winmm.dll goes (a winmm.dll.bak we saved is put back), with
    /// the files that serve it: the voice library and winmm_host.dll. mkwmp.toml, the lobby CA
    /// and mkwmp.log stay: the mod reads the launcher's mkwmp.toml. Nothing to do: an empty list.
    /// </summary>
    public static List<string> RemoveForMod(string game, bool running)
    {
        var ours = ProbeIsOurs(game) || (Read(Path.Combine(game, ProbeDll)) is { } b && LooksLikeProbe(b));
        string[] extra = [Voice, Host, Marker, "winmm.dll.mkwmp-new", "mkwmp-voice.dll.mkwmp-new"];
        if (!ours && !extra.Any(f => File.Exists(Path.Combine(game, f))))
        {
            return [];
        }
        if (running)
        {
            throw new LauncherException("close the game first: MKWMP runs as a mod, and the launcher's game patch must go");
        }
        var done = new List<string>();
        if (ours)
        {
            Remove(game, ProbeDll, done);
            RestoreBackup(game, done);
        }
        foreach (var f in extra)
        {
            Remove(game, f, done);
        }
        return done;
    }

    /// <summary>Where the real winmm.dll can be copied from on this machine (under Proton, System32 is Proton's own).</summary>
    public static string? HostSource()
    {
        var root = Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } r ? r : @"C:\Windows";
        var path = Path.Combine(root, "System32", "winmm.dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Makes sure winmm_host.dll exists. A message when it had to act or could not.</summary>
    public static string? EnsureHost(string game)
    {
        var dest = Path.Combine(game, Host);
        if (File.Exists(dest))
        {
            return null;
        }
        if (HostSource() is not { } src)
        {
            return $@"copy C:\Windows\System32\winmm.dll to {dest}";
        }
        try
        {
            File.Copy(src, dest);
            return $"copied {src} to {dest}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"could not copy {src} to {dest}: {e.Message}";
        }
    }

    // ---- mkwmp.toml -----------------------------------------------------------------------

    /// <summary>What the probe reads (native/probe/src/config.c): the lobby, the name, how to reach this launcher's local API, and the ticket once seated.</summary>
    public static string SessionText(SessionFile s)
    {
        var b = new StringBuilder("# Generated by the mkwmp launcher. Do not edit; edit client.toml.\n");
        b.Append(System.Globalization.CultureInfo.InvariantCulture, $"server = \"{s.Server}\"\n");
        b.Append(System.Globalization.CultureInfo.InvariantCulture, $"name = \"{s.Name}\"\n");
        b.Append(System.Globalization.CultureInfo.InvariantCulture, $"launcher = \"{s.Launcher}\"\n");
        b.Append(System.Globalization.CultureInfo.InvariantCulture, $"launcher_token = \"{s.LauncherToken}\"\n");
        // for the mod to start the launcher as its overlay host when the game runs without it; a path the format cannot hold is left out
        if (s.LauncherExe is { Length: > 0 and < 300 } exe && !exe.Contains('"', StringComparison.Ordinal) && !exe.Contains('\n', StringComparison.Ordinal))
        {
            b.Append(System.Globalization.CultureInfo.InvariantCulture, $"launcher_exe = \"{exe.Replace('\\', '/')}\"\n");
        }
        b.Append(System.Globalization.CultureInfo.InvariantCulture, $"voice_port = {s.VoicePort}\n");
        if (s.Ticket is not null)
        {
            b.Append(System.Globalization.CultureInfo.InvariantCulture, $"ticket = \"{s.Ticket}\"\n");
        }
        if (s.Slot != 1)
        {
            b.Append(System.Globalization.CultureInfo.InvariantCulture, $"slot = {s.Slot}\n");
        }
        return b.ToString();
    }

    /// <summary>Writes <c>&lt;game&gt;/mkwmp.toml</c> atomically, so the probe never reads half a file.</summary>
    public static void WriteSessionText(string game, string contents)
    {
        var tmp = Path.Combine(game, "mkwmp.toml.tmp");
        File.WriteAllText(tmp, contents, new UTF8Encoding(false));
        File.Move(tmp, Path.Combine(game, SessionToml), overwrite: true);
    }

    /// <summary>Drops the <c>ticket</c> line from <c>&lt;game&gt;/mkwmp.toml</c>, if there is one (the profile it belongs to is gone).</summary>
    public static void ForgetTicket(string game)
    {
        var path = Path.Combine(game, SessionToml);
        if (!File.Exists(path))
        {
            return;
        }
        var text = File.ReadAllText(path);
        var lines = text.Split('\n').ToList();
        if (lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1); // what follows the final newline is not a line
        }
        var kept = string.Concat(lines.Select(l => l.TrimEnd('\r')).Where(l => !l.TrimStart().StartsWith("ticket", StringComparison.Ordinal)).Select(l => l + "\n"));
        if (kept != text)
        {
            WriteSessionText(game, kept);
        }
    }

    /// <summary>Writes the server's lobby certificate for the probe, or removes the file when the server has none: a stale one from another server would make the game reject this one's lobby.</summary>
    public static void WriteLobbyCa(string game, string? pem)
    {
        var path = Path.Combine(game, LobbyCa);
        if (pem is null)
        {
            File.Delete(path);
            return;
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, pem, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

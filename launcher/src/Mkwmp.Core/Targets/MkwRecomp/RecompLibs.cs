// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>An optional library the game's settings ask for: the feature of libs.tsv, its file beside mkw.exe, its size, and what the player calls it.</summary>
public sealed record GameLibrary(string Feature, string File, long Size, string Name);

/// <summary>
/// The optional third-party libraries mkw-recomp 0.1.18 downloads when its own menu picks them (runtime/ui/menu/
/// settings_tabs.cpp <c>libs_sync</c>, ui/launcher/libs_fetch.cpp): NVIDIA DLSS and its frame generation (on an NVIDIA
/// card only, its licence's condition), AMD FSR 3.1 and its frame generation, RenHDrum. The game does it only while its
/// Graphics or Controls tab is drawn, so a pick made in the launcher would leave them missing: the launcher does the
/// same after it writes such a pick. It runs the install's own <c>setup/src/tools/prepare/libs.py fetch</c> with the
/// install's own Python, so the files, their official URLs and the sha256 they are checked against are mkw-recomp's
/// (<c>libs.tsv</c>), never the launcher's; a file already there is kept, whatever its version. None of these ships in
/// any release: NVIDIA's licence forbids distributing DLSS's files on their own.
/// </summary>
public static class RecompLibs
{
    /// <summary>Where libs.py and its table are in an install (the zip's <c>setup/src</c>).</summary>
    private static string Tool(GameInstall install, string name) => Path.Combine(install.Root, "setup", "src", "tools", "prepare", name);

    /// <summary>libs.tsv's <c>os</c>: MKWMP runs the Windows build, under Proton too (where libs_sync runs as well: it is _WIN32 code).</summary>
    private const string Os = "windows";

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["dlss"] = "NVIDIA DLSS", ["dlssg"] = "NVIDIA DLSS", ["fsr3"] = "AMD FSR 3.1", ["fsr3fg"] = "AMD FSR 3.1", ["renhdrum"] = "RenHDrum",
    };

    /// <summary>
    /// The features these settings need, as libs_sync picks them: <c>temporal</c> dlss (NVIDIA) or fsr3; with frame
    /// generation on, <c>fg_engine</c> dlss (dlssg, NVIDIA) or fsr3 (fsr3 and fsr3fg); <c>renhdrum</c> on.
    /// </summary>
    internal static IReadOnlyList<string> Wanted(Func<string, string?> value, bool nvidia)
    {
        var want = new List<string>();
        var temporal = value("temporal");
        var engine = value("fg_engine");
        var framegen = value("framegen") is { } fg && fg != "off";
        if (nvidia && temporal == "dlss")
        {
            want.Add("dlss");
        }
        if (temporal == "fsr3")
        {
            want.Add("fsr3");
        }
        if (framegen && nvidia && engine == "dlss")
        {
            want.Add("dlssg");
        }
        if (framegen && engine == "fsr3")
        {
            want.AddRange(["fsr3", "fsr3fg"]);
        }
        if (value("renhdrum") == "1")
        {
            want.Add("renhdrum");
        }
        return [.. want.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>libs.tsv's rows for Windows (feature, file, bytes); empty when the install has no table (before 0.1.18) or it cannot be read.</summary>
    internal static IReadOnlyList<(string Feature, string File, long Size)> Table(string tsv)
    {
        var rows = new List<(string, string, long)>();
        var lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines[0].Split('\t') is not ["feature", "os", "file", "bytes", ..])
        {
            return rows;
        }
        foreach (var line in lines.Skip(1))
        {
            // A file name is a plain name: anything else is not put beside mkw.exe by this launcher's look.
            if (line.Split('\t') is [var feature, Os, var file, var bytes, ..] && file.Length > 0 && Path.GetFileName(file) == file
                && long.TryParse(bytes, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                rows.Add((feature, file, size));
            }
        }
        return rows;
    }

    /// <summary>
    /// The libraries this install's settings ask for and its folder lacks; empty for a runtime without libs.py, when the
    /// launcher does not know the install's settings, or when nothing is missing. Reads mkw.ini and a small table.
    /// </summary>
    public static IReadOnlyList<GameLibrary> Missing(GameInstall install) => Missing(install, NvidiaPresent());

    internal static IReadOnlyList<GameLibrary> Missing(GameInstall install, bool nvidia)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (install.TargetId != MkwRecompTarget.TargetId || RecompSettings.Schema(install) is not { } schema)
        {
            return [];
        }
        string tsv;
        try
        {
            var path = Tool(install, "libs.tsv");
            if (!System.IO.File.Exists(path) || !System.IO.File.Exists(Tool(install, "libs.py")))
            {
                return [];
            }
            tsv = System.IO.File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var effective = schema.Effective(RecompSettings.Read(install));
        var wanted = Wanted(id => effective.GetValueOrDefault(id), nvidia);
        return [.. Table(tsv)
            .Where(row => wanted.Contains(row.Feature) && !System.IO.File.Exists(Path.Combine(install.Root, row.File)))
            .Select(row => new GameLibrary(row.Feature, row.File, row.Size, Names.GetValueOrDefault(row.Feature) ?? row.Feature))];
    }

    /// <summary>Their names for the player, once each ("NVIDIA DLSS, RenHDrum").</summary>
    public static string NamesOf(IEnumerable<GameLibrary> libraries) => string.Join(", ", libraries.Select(l => l.Name).Distinct(StringComparer.Ordinal));

    /// <summary>The process that fetches <paramref name="features"/> beside mkw.exe: the install's Python, -I, libs.py fetch.</summary>
    internal static ProcessStartInfo Command(GameInstall install, IEnumerable<string> features)
    {
        var start = new ProcessStartInfo(RecompSetup.Python(install))
        {
            WorkingDirectory = install.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // bytes as they come: Python writes a pipe in the system's code page (RecompSetup.Decode reads them)
            StandardOutputEncoding = Encoding.Latin1,
            StandardErrorEncoding = Encoding.Latin1,
        };
        foreach (var arg in new[] { "-I", Tool(install, "libs.py"), "fetch", "--features", string.Join(',', features.Distinct(StringComparer.Ordinal)), "--dir", install.Root, "--os", Os })
        {
            start.ArgumentList.Add(arg);
        }
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }
        return start;
    }

    /// <summary>One line of libs.py's events: the bytes done of the total ("progress"), or its error's message ("error &lt;command&gt; &lt;message&gt;").</summary>
    internal static (long Done, long Total)? Progress(string line) =>
        line.Split(' ') is ["progress", var d, var t] && long.TryParse(d, CultureInfo.InvariantCulture, out var done) && long.TryParse(t, CultureInfo.InvariantCulture, out var total)
            ? (done, total) : null;

    internal static string? Error(string line) =>
        line.StartsWith("error ", StringComparison.Ordinal) && line.IndexOf(' ', 6) is var at and > 0 ? line[(at + 1)..].Trim() : null;

    /// <summary>
    /// Downloads <paramref name="libraries"/> beside mkw.exe, reporting the share done (0 to 1). Null when every file
    /// came, else why not, for the player. libs.py writes each file through a .part renamed only once its size and sha256
    /// match, so a stop leaves nothing half there. Cancelling ends the process.
    /// </summary>
    public static async Task<string?> FetchAsync(GameInstall install, IReadOnlyList<GameLibrary> libraries, IProgress<double>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(libraries);
        if (libraries.Count == 0)
        {
            return null;
        }
        if (!System.IO.File.Exists(RecompSetup.Python(install)))
        {
            return Strings.GameLibsNoTools;
        }
        string? error = null;
        var tail = new StringBuilder();
        using var process = new Process { StartInfo = Command(install, libraries.Select(l => l.Feature)) };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return e.Message;
        }
        var errors = process.StandardError.ReadToEndAsync(cancel);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancel).ConfigureAwait(false) is { } raw)
            {
                var line = RecompSetup.Decode(raw);
                if (Progress(line) is { Total: > 0 } p)
                {
                    progress?.Report(Math.Clamp((double)p.Done / p.Total, 0, 1));
                }
                error = Error(line) ?? error;
            }
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            tail.Append(RecompSetup.Decode(await errors.ConfigureAwait(false)));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it ended by itself
            }
            throw;
        }
        if (process.ExitCode == 0)
        {
            progress?.Report(1);
            return null;
        }
        var last = tail.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return error ?? last ?? $"exit {process.ExitCode}";
    }

    // ---- an NVIDIA card in this PC ---------------------------------------------------------

    /// <summary>
    /// Whether a display adapter of this PC is NVIDIA's (PCI vendor 10DE in its device id; Wine reports the real card's).
    /// The game asks the same of the Vulkan device it runs on; the launcher has no Vulkan, so any NVIDIA adapter counts,
    /// and a PC whose adapters cannot be listed counts as one, as libs_sync does before its list is made (the game still
    /// says when DLSS cannot run).
    /// </summary>
    internal static bool NvidiaPresent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        try
        {
            var any = false;
            for (uint i = 0; i < 16; i++)
            {
                var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevicesW(null, i, ref device, 0))
                {
                    break;
                }
                any = true;
                if (device.DeviceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return !any;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

#pragma warning disable SYSLIB1054 // plain DllImport, as the rest of the core
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice info, uint flags);
#pragma warning restore SYSLIB1054
}

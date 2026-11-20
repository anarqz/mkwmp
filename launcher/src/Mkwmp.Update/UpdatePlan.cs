// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text.Json;

namespace Mkwmp.Update;

/// <summary>
/// What the launcher hands to the helper: one downloaded release, staged in
/// <c>&lt;launcher folder&gt;/.mkwmp-selfupdate/&lt;version&gt;/</c> (the same volume as the files it replaces, so
/// every move is a rename):
/// <code>
/// plan.json            this record
/// SHA256SUMS(.sig)     as downloaded: the helper verifies them again, trusting nothing in the plan
/// package.zip          the release's zip (Kind "zip"), or
/// launcher.exe         the release's single executable (Kind "exe": a release from before the zip carried the helper)
/// worker.exe           the helper that runs: the new release's own, copied out of the verified zip
/// incoming/  backup/   made by the install; backup/ is what a rollback puts back
/// </code>
/// </summary>
public sealed record UpdatePlan
{
    /// <summary>Inside the launcher's folder. Never part of a release, never replaced by one.</summary>
    public const string StoreFolder = ".mkwmp-selfupdate";
    public const string FileName = "plan.json";
    public const string SumsName = "SHA256SUMS";
    public const string SignatureName = "SHA256SUMS.sig";
    public const string ZipName = "package.zip";
    public const string ExeName = "launcher.exe";
    public const string NotesName = "notes.md";
    /// <summary>The helper as a release ships it, next to the launcher.</summary>
    public const string HelperName = "mkwmp-updater.exe";
    /// <summary>The copy of the helper that runs (so the installed one can be replaced like any other file).</summary>
    public const string WorkerName = "worker.exe";
    /// <summary>The launcher's name inside a release zip.</summary>
    public const string LauncherName = "mkwmp-launcher.exe";
    public const string KindZip = "zip";
    public const string KindExe = "exe";
    /// <summary>The paths the last installed zip brought, one per line: a later release that drops a file removes it.</summary>
    public const string InstalledName = "installed.txt";

    /// <summary>The launcher's folder (absolute).</summary>
    public required string Root { get; init; }

    /// <summary>The release being installed (<c>1.5.0</c>): the staging folder's name and the version SHA256SUMS must name.</summary>
    public required string Version { get; init; }

    /// <summary>The version being replaced (for the log and the window).</summary>
    public string FromVersion { get; init; } = "";

    /// <summary><see cref="KindZip"/> or <see cref="KindExe"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The package's name in SHA256SUMS (<c>mkwmp-1.5.0-windows-x64.zip</c> or <c>mkwmp-launcher-x86_64-windows.exe</c>).</summary>
    public required string Asset { get; init; }

    /// <summary>
    /// The launcher's file name in <see cref="Root"/>: what is started afterwards, and what the release's
    /// <see cref="LauncherName"/> is installed as (a player may have kept the download's name).
    /// </summary>
    public string Launcher { get; init; } = LauncherName;

    /// <summary>The launcher's arguments, as it was started: the new one gets the same.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string Transaction => TransactionFolder(Root, Version);

    public string PackagePath => Path.Combine(Transaction, Kind == KindExe ? ExeName : ZipName);

    public string SumsPath => Path.Combine(Transaction, SumsName);

    public string SignaturePath => Path.Combine(Transaction, SignatureName);

    public string PlanPath => Path.Combine(Transaction, FileName);

    public string LauncherPath => Path.Combine(Root, Launcher);

    /// <summary>Next to the launcher's session logs.</summary>
    public string LogPath => Path.Combine(Root, "logs", "updater.log");

    public static string Store(string root) => Path.Combine(root, StoreFolder);

    public static string TransactionFolder(string root, string version)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Path.Combine(root, StoreFolder, ValidVersion(version) ? version : throw new UpdateException($"\"{version}\" is not a version"));
    }

    /// <summary>A version that is safe as a folder name: digits, letters, <c>. - +</c>, starting with a digit.</summary>
    public static bool ValidVersion(string? version) =>
        version is { Length: > 0 and <= 64 } && char.IsAsciiDigit(version[0]) && !version.Contains("..", StringComparison.Ordinal)
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    /// <summary>A plain file name (the launcher's own): no folder, no drive, no reserved character.</summary>
    public static bool ValidFileName(string? name) =>
        name is { Length: > 0 and <= 128 } && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 && name is not ("." or "..") && !name.EndsWith('.') && !name.EndsWith(' ');

    /// <summary>Throws <see cref="UpdateException"/> for a plan no launcher would write.</summary>
    public UpdatePlan Validated()
    {
        if (string.IsNullOrWhiteSpace(Root) || !Path.IsPathFullyQualified(Root))
        {
            throw new UpdateException("the update plan names no launcher folder");
        }
        if (!ValidVersion(Version))
        {
            throw new UpdateException($"\"{Version}\" is not a version");
        }
        if (Kind is not (KindZip or KindExe))
        {
            throw new UpdateException($"unknown update package kind \"{Kind}\"");
        }
        if (string.IsNullOrWhiteSpace(Asset) || !ValidFileName(Launcher) || !Launcher.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("the update plan names no package or no launcher");
        }
        if (Arguments.Count > 32 || Arguments.Any(a => a is null || a.Length > 4096))
        {
            throw new UpdateException("the update plan's arguments are not a command line");
        }
        return this;
    }

    public void Write()
    {
        Validated();
        Directory.CreateDirectory(Transaction);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("root", Root);
            json.WriteString("version", Version);
            json.WriteString("from", FromVersion);
            json.WriteString("kind", Kind);
            json.WriteString("asset", Asset);
            json.WriteString("launcher", Launcher);
            json.WriteStartArray("arguments");
            foreach (var argument in Arguments)
            {
                json.WriteStringValue(argument);
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        var temp = PlanPath + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        File.Move(temp, PlanPath, overwrite: true);
    }

    /// <summary>
    /// Reads a plan and checks that it is the one of the folder it lies in (a plan cannot point the
    /// helper at another folder's package). Throws <see cref="UpdateException"/>.
    /// </summary>
    public static UpdatePlan Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        UpdatePlan plan;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 256 * 1024)
            {
                throw new UpdateException($"no update plan at {path}");
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new UpdateException("the update plan is not an object");
            }
            var arguments = new List<string>();
            if (root.TryGetProperty("arguments", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    arguments.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new UpdateException("the update plan's arguments are not text"));
                }
            }
            plan = new UpdatePlan
            {
                Root = Text(root, "root"),
                Version = Text(root, "version"),
                FromVersion = Text(root, "from"),
                Kind = Text(root, "kind"),
                Asset = Text(root, "asset"),
                Launcher = Text(root, "launcher"),
                Arguments = arguments,
            }.Validated();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new UpdateException($"the update plan cannot be read: {e.Message}", e);
        }
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(plan.PlanPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("the update plan is not in its launcher's staging folder");
        }
        return plan;
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}

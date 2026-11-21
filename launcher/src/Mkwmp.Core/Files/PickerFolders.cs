// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Files;

/// <summary>
/// Where the file picker was last used, per purpose (<c>[picker]</c> in client.toml:
/// <c>game_copy = "D:\\Dumps"</c>). In the settings file like everything else: the launcher is
/// portable and keeps nothing in the user's profile. Immutable, with value equality (it is part of
/// <see cref="LauncherSettings"/>, which is compared to know whether to save).
/// </summary>
public sealed class PickerFolders : IEquatable<PickerFolders>
{
    /// <summary>More purposes than the launcher has: a hand-edited file cannot grow it without end.</summary>
    public const int MaxEntries = 16;
    public const int MaxPath = 1024;

    private readonly SortedDictionary<string, string> _folders;

    private PickerFolders(SortedDictionary<string, string> folders) => _folders = folders;

    public static PickerFolders Empty { get; } = new(new SortedDictionary<string, string>(StringComparer.Ordinal));

    /// <summary>The purposes and their folders, in the order of the purposes' names.</summary>
    public IReadOnlyCollection<KeyValuePair<string, string>> All => _folders;

    /// <summary>The folder last used for a purpose, or "".</summary>
    public string this[string purpose] => purpose is not null && _folders.TryGetValue(purpose, out var folder) ? folder : "";

    /// <summary>A purpose is a bare TOML key: lower-case letters, digits and <c>_</c>.</summary>
    public static bool ValidPurpose(string? purpose) =>
        purpose is { Length: > 0 and <= 32 } && purpose.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    /// <summary>The same with that folder for the purpose ("" forgets it). Nothing changes for a purpose or a path that cannot be kept.</summary>
    public PickerFolders With(string purpose, string? folder)
    {
        var path = (folder ?? "").Trim();
        if (!ValidPurpose(purpose) || path.Length > MaxPath || this[purpose] == path)
        {
            return this;
        }
        if (path.Length > 0 && !_folders.ContainsKey(purpose) && _folders.Count >= MaxEntries)
        {
            return this;
        }
        var next = new SortedDictionary<string, string>(_folders, StringComparer.Ordinal);
        if (path.Length == 0)
        {
            next.Remove(purpose);
        }
        else
        {
            next[purpose] = path;
        }
        return new PickerFolders(next);
    }

    public bool Equals(PickerFolders? other) => other is not null && _folders.SequenceEqual(other._folders);

    public override bool Equals(object? obj) => Equals(obj as PickerFolders);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (purpose, folder) in _folders)
        {
            hash.Add(purpose, StringComparer.Ordinal);
            hash.Add(folder, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}

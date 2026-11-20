// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.Concurrent;
using System.Text.Json;

namespace Mkwmp.Core;

/// <summary>
/// The launcher's catalogs (<c>Mkwmp.App/i18n/&lt;locale&gt;.json</c>, flat <c>key → text</c>). Every
/// one of them is built into this assembly, so a launcher that is alone in its folder (an old
/// launcher that updated itself into this one has only the exe) still speaks the player's
/// language. The files beside the exe (<c>i18n/</c>) are read on top: a key they have wins, a
/// key they lack, a missing file or a broken one falls to what is built in.
/// </summary>
public static class Catalogs
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Embedded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The catalog built into the launcher for a language of <see cref="Locales.All"/>; empty for any other.</summary>
    public static IReadOnlyDictionary<string, string> BuiltIn(string locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        return Embedded.GetOrAdd(locale, static id =>
        {
            if (Locales.Find(id) is not { } known)
            {
                return new Dictionary<string, string>();
            }
            using var stream = typeof(Catalogs).Assembly.GetManifestResourceStream($"Mkwmp.Core.{known.Id}.json");
            try
            {
                return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>();
            }
        });
    }

    /// <summary>
    /// A language's catalog as the window uses it: the built-in one, with the keys of
    /// <c>&lt;directory&gt;/&lt;locale&gt;.json</c> on top when that file is there and reads. Never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(string? directory, string locale)
    {
        var builtIn = BuiltIn(locale);
        if (directory is null || Locales.Find(locale) is not { } known)
        {
            return builtIn;
        }
        Dictionary<string, string>? loose;
        try
        {
            var path = Path.Combine(directory, known.Id + ".json");
            if (!File.Exists(path))
            {
                return builtIn;
            }
            loose = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return builtIn;
        }
        if (loose is null || loose.Count == 0)
        {
            return builtIn;
        }
        foreach (var (key, text) in builtIn)
        {
            if (!loose.TryGetValue(key, out var own) || string.IsNullOrEmpty(own))
            {
                loose[key] = text;
            }
        }
        return loose;
    }
}

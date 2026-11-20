// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text.Json;

namespace Mkwmp.Update;

/// <summary>
/// The helper's few strings. They come from the launcher's own catalogs (<c>i18n/&lt;locale&gt;.json</c>, keys
/// <c>updater.*</c>), so every language the launcher has is the helper's too; English and pt-BR are also
/// built in, for an install whose catalogs are older than the helper or missing.
/// Order: the locale's catalog, the built-in text of the locale, the English catalog, built-in English.
/// </summary>
public sealed class UpdaterStrings
{
    public const string Title = "updater.title";
    public const string Waiting = "updater.waiting";
    public const string Verifying = "updater.verifying";
    public const string Replacing = "updater.replacing";
    public const string Starting = "updater.starting";
    public const string FailedTitle = "updater.failed_title";
    public const string FailedBody = "updater.failed_body";
    public const string FailedBroken = "updater.failed_broken";
    public const string Version = "updater.version";

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        [Title] = "Installing the update…",
        [Waiting] = "Waiting for MKWMP to close",
        [Verifying] = "Checking the download",
        [Replacing] = "Replacing files",
        [Starting] = "Starting MKWMP",
        [FailedTitle] = "The update was not installed",
        [FailedBody] = "MKWMP was not changed and opens again as it was. Details: logs\\updater.log",
        [FailedBroken] = "Some files could not be put back. Download MKWMP again; details: logs\\updater.log",
        [Version] = "Version {version}",
    };

    private static readonly Dictionary<string, string> Portuguese = new(StringComparer.Ordinal)
    {
        [Title] = "Instalando a atualização…",
        [Waiting] = "Aguardando o MKWMP fechar",
        [Verifying] = "Verificando o download",
        [Replacing] = "Substituindo arquivos",
        [Starting] = "Iniciando o MKWMP",
        [FailedTitle] = "A atualização não foi instalada",
        [FailedBody] = "O MKWMP não foi alterado e abre de novo como estava. Detalhes: logs\\updater.log",
        [FailedBroken] = "Alguns arquivos não puderam ser restaurados. Baixe o MKWMP de novo; detalhes: logs\\updater.log",
        [Version] = "Versão {version}",
    };

    private readonly IReadOnlyDictionary<string, string>[] _layers;

    private UpdaterStrings(string locale, params IReadOnlyDictionary<string, string>[] layers)
    {
        Locale = locale;
        _layers = layers;
    }

    public string Locale { get; }

    /// <summary>The built-in keys (what a catalog should carry).</summary>
    public static IReadOnlyCollection<string> Keys => English.Keys;

    /// <summary>Never throws: a missing or broken catalog only removes a layer.</summary>
    public static UpdaterStrings Load(string? directory, string locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        IReadOnlyDictionary<string, string> builtIn = locale.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? Portuguese : new Dictionary<string, string>();
        if (directory is null || !UpdaterArgs.ValidLocale(locale))
        {
            return new UpdaterStrings(locale, builtIn, English);
        }
        return new UpdaterStrings(locale, Read(Path.Combine(directory, locale + ".json")), builtIn, Read(Path.Combine(directory, "en.json")), English);
    }

    public string this[string key]
    {
        get
        {
            foreach (var layer in _layers)
            {
                if (layer.TryGetValue(key, out var text) && text.Length > 0)
                {
                    return text;
                }
            }
            return key;
        }
    }

    public string Format(string key, string name, string value) => this[key].Replace("{" + name + "}", value, StringComparison.Ordinal);

    public string For(UpdateStep step) => this[step switch
    {
        UpdateStep.Waiting => Waiting,
        UpdateStep.Verifying => Verifying,
        UpdateStep.Replacing => Replacing,
        UpdateStep.Starting => Starting,
        _ => FailedBody,
    }];

    /// <summary>The <c>updater.*</c> entries of a flat <c>key → text</c> catalog.</summary>
    private static Dictionary<string, string> Read(string path)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4 * 1024 * 1024)
            {
                return found;
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return found;
            }
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Name.StartsWith("updater.", StringComparison.Ordinal) && entry.Value.ValueKind == JsonValueKind.String)
                {
                    found[entry.Name] = entry.Value.GetString()!;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // the built-in text is used
        }
        return found;
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mkwmp.Core.Overlay;

/// <summary>
/// JSON as the mod reads it (docs/contracts/LAUNCHER_API.md "In-game toasts", "In-game overlay"): every character as
/// plain UTF-8. Only what JSON itself must escape is escaped (<c>\"</c>, <c>\\</c>, and a control character as
/// <c>\u00XX</c>): a name with an emoji arrives as the emoji, not as two <c>\uD83C\uDFCE</c> escapes.
/// </summary>
internal static class PlainJson
{
    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => Unescape(JsonSerializer.Serialize(value, Relaxed));

    /// <summary>The encoder writes a character outside the basic plane as a pair of surrogate escapes: here it is the character again.</summary>
    internal static string Unescape(string json)
    {
        if (!json.Contains("\\uD", StringComparison.OrdinalIgnoreCase))
        {
            return json;
        }
        var b = new StringBuilder(json.Length);
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (c != '\\' || i + 1 >= json.Length)
            {
                b.Append(c);
                continue;
            }
            // an escape is a backslash and what follows it: taken whole, so "\\uD83C" (a backslash, then text) is left alone
            if (json[i + 1] == 'u' && i + 11 < json.Length && json[i + 6] == '\\' && json[i + 7] == 'u'
                && ushort.TryParse(json.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high)
                && ushort.TryParse(json.AsSpan(i + 8, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low)
                && char.IsHighSurrogate((char)high) && char.IsLowSurrogate((char)low))
            {
                b.Append((char)high).Append((char)low);
                i += 11;
                continue;
            }
            b.Append(c).Append(json[i + 1]);
            i++;
        }
        return b.ToString();
    }
}
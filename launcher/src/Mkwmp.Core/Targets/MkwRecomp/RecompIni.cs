// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// mkw-recomp's mkw.ini, read and changed the way the game does it (runtime/core/base/cfg.c,
/// read_ini and cfg_save_to): <c>key = value</c> lines, <c>#</c> or <c>;</c> starts a comment
/// line, <c>[sections]</c> only group (a key means the same anywhere), keys compare without case
/// and with an optional <c>MKW_</c> prefix, a value may be in double quotes, an empty value counts
/// as unset and the last line of a key wins. A change touches only that key's line: every other
/// byte of the file (comments, unknown keys, order, line endings, a BOM) stays as it is.
/// <para>
/// The file is handled as bytes, one char per byte (Latin-1), so a line in any encoding survives;
/// values are UTF-8, as the game writes them.
/// </para>
/// </summary>
internal static class RecompIni
{
    private const string Prefix = "MKW_";
    private const string Bom = "ï»¿";

    public static string Decode(byte[] file) => Encoding.Latin1.GetString(file);

    public static byte[] Encode(string text) => Encoding.Latin1.GetBytes(text);

    private static string FromUtf8(string bytes) => Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(bytes));

    private static string ToUtf8(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));

    /// <summary>Upper case, without the <c>MKW_</c> prefix: how the game names a key.</summary>
    public static string Key(string key)
    {
        var upper = key.Trim().ToUpperInvariant();
        return upper.StartsWith(Prefix, StringComparison.Ordinal) ? upper[Prefix.Length..] : upper;
    }

    private static string Trim(string s) => s.Trim(' ', '\t', '\r');

    private static string Unquote(string v) => v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;

    /// <summary>A line without its ending, a first line's BOM and the blanks around it.</summary>
    private static string LineText(string line, bool first)
    {
        if (first && line.StartsWith(Bom, StringComparison.Ordinal))
        {
            line = line[Bom.Length..];
        }
        return Trim(line.TrimEnd('\n', '\r'));
    }

    /// <summary>The key of a <c>key = value</c> line, or of a commented <c># key = value</c> example; null when the line is not one.</summary>
    private static string? LineKey(string text, bool comment)
    {
        if (comment)
        {
            if (text.Length == 0 || text[0] is not ('#' or ';'))
            {
                return null;
            }
            text = text[1..].TrimStart(' ', '\t');
        }
        if (text.Length == 0 || text[0] is '#' or ';' or '[')
        {
            return null;
        }
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            return null;
        }
        var name = text[..eq].TrimEnd(' ', '\t');
        return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? Key(name) : null;
    }

    private static string? LineSection(string text)
    {
        if (text.Length == 0 || text[0] != '[')
        {
            return null;
        }
        var end = text.IndexOf(']', StringComparison.Ordinal);
        return end < 0 ? null : Trim(text[1..end]).ToLowerInvariant();
    }

    /// <summary>The file as lines, each with its ending; a text that ends with a newline has no last empty line.</summary>
    private static List<string> Lines(string text)
    {
        var lines = new List<string>();
        var at = 0;
        while (at < text.Length)
        {
            var nl = text.IndexOf('\n', at);
            var end = nl < 0 ? text.Length : nl + 1;
            lines.Add(text[at..end]);
            at = end;
        }
        return lines;
    }

    /// <summary>Every key with a value (the game's name of the key to its value, unquoted): what the game holds after reading the file.</summary>
    public static Dictionary<string, string> Values(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = Lines(text);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = LineText(lines[i], i == 0);
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] is '#' or ';' or '[' || eq < 0)
            {
                continue;
            }
            var key = Key(Trim(line[..eq]));
            var value = Unquote(Trim(line[(eq + 1)..]));
            if (key.Length > 0 && value.Length > 0)
            {
                values[key] = FromUtf8(value); // "key =" leaves the setting at its default; a later line wins
            }
        }
        return values;
    }

    /// <summary>
    /// The text with these keys changed, as the game's own save writes it. A value replaces the
    /// key's last line (its indentation and spelling of the key kept); a key the file lacks goes
    /// under its section (<paramref name="sectionOf"/>), after the commented example line of the
    /// key when the section has one, else after the section's last line; a missing section is added
    /// at the end. A null value removes every line of the key (commented examples stay). New lines
    /// end as the file's do (CRLF when it has any).
    /// </summary>
    public static string Apply(string text, IReadOnlyList<KeyValuePair<string, string?>> changes, Func<string, string?> sectionOf)
    {
        var lines = Lines(text);
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var texts = lines.Select((l, i) => LineText(l, i == 0)).ToArray();
        var keys = texts.Select(t => LineKey(t, comment: false)).ToArray();
        var drop = new bool[lines.Count];
        var replace = new int[lines.Count];
        Array.Fill(replace, -1);
        const int Clean = -2, NewSection = -1, Written = -3;
        var after = new int[changes.Count];
        for (var e = 0; e < changes.Count; e++)
        {
            after[e] = Clean;
            var key = Key(changes[e].Key);
            if (changes[e].Value is null)
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    drop[i] |= keys[i] == key;
                }
                continue;
            }
            var last = Array.LastIndexOf(keys, key);
            if (last >= 0)
            {
                replace[last] = e;
                continue;
            }
            var want = sectionOf(key);
            var (inside, example, tail) = (false, -1, -1);
            for (var i = 0; i < lines.Count && want is not null; i++)
            {
                if (LineSection(texts[i]) is { } section)
                {
                    inside = section == want;
                    if (inside)
                    {
                        tail = i;
                    }
                    continue;
                }
                if (!inside)
                {
                    continue;
                }
                if (texts[i].Length > 0)
                {
                    tail = i;
                }
                if (LineKey(texts[i], comment: true) == key)
                {
                    example = i;
                }
            }
            after[e] = example >= 0 ? example : tail;
        }

        var o = new StringBuilder(text.Length + 64);
        string Entry(int e) => $"{Key(changes[e].Key).ToLowerInvariant()} = {ToUtf8(changes[e].Value!)}{eol}";
        void EndLine()
        {
            if (o.Length > 0 && o[^1] != '\n')
            {
                o.Append(eol);
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (drop[i])
            {
                // the line leaves the file
            }
            else if (replace[i] >= 0)
            {
                var line = lines[i];
                if (i == 0 && line.StartsWith(Bom, StringComparison.Ordinal))
                {
                    o.Append(Bom);
                    line = line[Bom.Length..];
                }
                o.Append(line[..line.IndexOf('=', StringComparison.Ordinal)].TrimEnd(' ', '\t')).Append(" = ").Append(ToUtf8(changes[replace[i]].Value!));
                if (line.EndsWith('\n'))
                {
                    o.Append(eol);
                }
            }
            else
            {
                o.Append(lines[i]);
            }
            for (var e = 0; e < changes.Count; e++)
            {
                if (after[e] == i)
                {
                    EndLine();
                    o.Append(Entry(e));
                }
            }
        }
        // keys whose section the file lacks: one new section each, at the end
        for (var e = 0; e < changes.Count; e++)
        {
            if (after[e] != NewSection)
            {
                continue;
            }
            var want = sectionOf(Key(changes[e].Key));
            EndLine();
            if (want is not null)
            {
                if (o.Length > 0)
                {
                    o.Append(eol);
                }
                o.Append('[').Append(want).Append(']').Append(eol);
            }
            o.Append(Entry(e));
            for (var f = e + 1; f < changes.Count && want is not null; f++)
            {
                if (after[f] == NewSection && sectionOf(Key(changes[f].Key)) == want)
                {
                    o.Append(Entry(f));
                    after[f] = Written;
                }
            }
        }
        return o.ToString();
    }
}

// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Numerics;

namespace Mkwmp.Core.Updates;

/// <summary>
/// A semantic version (<c>1.4.0</c>, <c>0.0.1-rc.2</c>), ordered by the semver rules: a release
/// is newer than its own pre-releases, so the launcher never "updates" from 1.0.0 to 1.0.0-rc.2.
/// <see cref="System.Version"/> knows neither pre-releases nor that order.
/// </summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    private readonly string _text;

    private SemVer(BigInteger major, BigInteger minor, BigInteger patch, string[] pre, string text)
    {
        (Major, Minor, Patch, Pre, _text) = (major, minor, patch, pre, text);
    }

    public BigInteger Major { get; }
    public BigInteger Minor { get; }
    public BigInteger Patch { get; }
    private string[] Pre { get; }

    public static SemVer? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var core = text;
        var plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            if (!Identifiers(core[(plus + 1)..], numericNoLeadingZero: false))
            {
                return null;
            }
            core = core[..plus];
        }
        string[] pre = [];
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            var tail = core[(dash + 1)..];
            if (!Identifiers(tail, numericNoLeadingZero: true))
            {
                return null;
            }
            pre = tail.Split('.');
            core = core[..dash];
        }
        var parts = core.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0')))
        {
            return null;
        }
        return new SemVer(Number(parts[0]), Number(parts[1]), Number(parts[2]), pre, text);
    }

    private static BigInteger Number(string digits) => BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    private static bool Identifiers(string text, bool numericNoLeadingZero) => text.Split('.').All(id =>
        id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && !(numericNoLeadingZero && id.Length > 1 && id[0] == '0' && id.All(char.IsAsciiDigit)));

    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }
        var c = Major.CompareTo(other.Major);
        c = c != 0 ? c : Minor.CompareTo(other.Minor);
        c = c != 0 ? c : Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }
        if (Pre.Length == 0 || other.Pre.Length == 0)
        {
            return Pre.Length == other.Pre.Length ? 0 : (Pre.Length == 0 ? 1 : -1);
        }
        for (var i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            var (a, b) = (Pre[i], other.Pre[i]);
            var (an, bn) = (a.All(char.IsAsciiDigit), b.All(char.IsAsciiDigit));
            c = (an, bn) switch
            {
                (true, true) => Number(a).CompareTo(Number(b)),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };
            if (c != 0)
            {
                return c;
            }
        }
        return Pre.Length.CompareTo(other.Pre.Length);
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as SemVer);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Pre));

    public override string ToString() => _text;

    public static bool operator ==(SemVer? left, SemVer? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemVer? left, SemVer? right) => !(left == right);

    public static bool operator <(SemVer? left, SemVer? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(SemVer? left, SemVer? right) => left is null || left.CompareTo(right) <= 0;

    public static bool operator >(SemVer? left, SemVer? right) => left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(SemVer? left, SemVer? right) => left is null ? right is null : left.CompareTo(right) >= 0;
}

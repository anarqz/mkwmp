// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Mkwmp.Update;

/// <summary>An update that cannot be installed; the message is for the log and the player.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException()
    {
    }

    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>A rollback could not put every file back: the install is mixed.</summary>
    public bool Incomplete { get; init; }
}

/// <summary>Ed25519: true when <paramref name="signature"/> (64 bytes) is <paramref name="publicKey"/>'s (32 bytes) over <paramref name="message"/>.</summary>
public delegate bool Ed25519Verifier(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature);

/// <summary>
/// A release's <c>SHA256SUMS</c> and its detached signature (tools/release/sign_sums.py):
/// <code>
/// # mkwmp 1.5.0
/// &lt;sha256 hex&gt;  mkwmp-1.5.0-windows-x64.zip
/// </code>
/// <c>SHA256SUMS.sig</c> is Ed25519 (base64url, no padding) over the exact bytes of the list, by a
/// release key (<c>kid:base64url</c>).
/// </summary>
public static class ReleaseSums
{
    /// <summary>The public release keys. The private half never leaves the release machine.</summary>
    public static IReadOnlyList<string> ReleaseKeys { get; } = ["release1:Zuvb-AuF9DcXEzXRSu49EFrb2ilCQnJl1e4ApQJI9Dw"];

    public const int MaxListBytes = 1024 * 1024;

    /// <summary>
    /// The verified list (asset name → lowercase sha256 hex). Throws <see cref="UpdateException"/> unless the
    /// signature is one of <paramref name="keys"/>' and the list's first line names <paramref name="version"/>
    /// (a validly signed list of another release must not pass under this one's name).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Verify(ReadOnlySpan<byte> sums, string signature, IEnumerable<string> keys, string version, Ed25519Verifier verifier)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(verifier);
        if (sums.Length > MaxListBytes)
        {
            throw new UpdateException("SHA256SUMS is too large");
        }
        byte[] sig;
        try
        {
            sig = Base64Url.DecodeFromChars(signature.Trim());
        }
        catch (FormatException)
        {
            throw new UpdateException("malformed signature");
        }
        if (sig.Length != 64)
        {
            throw new UpdateException("malformed signature");
        }
        var trusted = false;
        foreach (var entry in keys)
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }
            try
            {
                var raw = Base64Url.DecodeFromChars(entry.AsSpan(colon + 1));
                if (raw.Length == 32 && verifier(raw, sums, sig))
                {
                    trusted = true;
                    break;
                }
            }
            catch (FormatException)
            {
                // not a key: skipped
            }
        }
        if (!trusted)
        {
            throw new UpdateException("SHA256SUMS signature does not verify");
        }
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(sums);
        }
        catch (ArgumentException)
        {
            throw new UpdateException("SHA256SUMS is not text");
        }
        var lines = text.Split('\n');
        var header = lines[0].TrimEnd('\r');
        if (header.Trim() != $"# mkwmp {version}")
        {
            throw new UpdateException($"SHA256SUMS is for \"{header}\", not {version}");
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }
            var split = line.IndexOf("  ", StringComparison.Ordinal);
            if (split != 64 || !line.AsSpan(0, 64).ContainsOnlyHex())
            {
                throw new UpdateException("malformed SHA256SUMS line");
            }
            map[line[(split + 2)..].Trim()] = line[..split].ToLowerInvariant();
        }
        return map;
    }

    /// <summary>Lowercase SHA-256 of a file, read as a stream (a release zip is tens of megabytes).</summary>
    public static string Sha256OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }
}

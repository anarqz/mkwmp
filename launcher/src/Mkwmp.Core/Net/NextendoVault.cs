// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;
using System.Text;

namespace Mkwmp.Core.Net;

/// <summary>
/// Where the Nextendo sign-in (the refresh token) is kept: one file beside client.toml, protected with the Windows
/// data protection API for the current user (docs/contracts/NEXTENDO_API.md §2). Another user of the PC, or the file
/// copied to another PC, reads nothing from it. It is never in client.toml, a log, a report or mkwmp.toml.
/// </summary>
internal sealed unsafe class NextendoVault(string path)
{
    public const string FileName = "nextendo.dat";

    private const uint UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN: never a prompt
    private static readonly byte[] Purpose = "mkwmp/nextendo/refresh-token/v1"u8.ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public uint Size;
        public byte* Data;
    }

    [DllImport("crypt32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CryptProtectData(Blob* dataIn, char* description, Blob* entropy, void* reserved, void* prompt, uint flags, Blob* dataOut);

    [DllImport("crypt32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CryptUnprotectData(Blob* dataIn, char** description, Blob* entropy, void* reserved, void* prompt, uint flags, Blob* dataOut);

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint memory);

    /// <summary>The file beside a client.toml.</summary>
    public static NextendoVault Beside(string configPath) =>
        new(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(configPath)) ?? ".", FileName));

    public string Path { get; } = path;

    public bool Exists => File.Exists(Path);

    /// <summary>Protected for the current user; null when Windows refused.</summary>
    internal static byte[]? Protect(ReadOnlySpan<byte> plain) => Transform(plain, protect: true);

    /// <summary>What <see cref="Protect"/> hid; null when it is not this user's, or not such a blob at all.</summary>
    internal static byte[]? Unprotect(ReadOnlySpan<byte> hidden) => Transform(hidden, protect: false);

    private static byte[]? Transform(ReadOnlySpan<byte> input, bool protect)
    {
        if (input.IsEmpty)
        {
            return null;
        }
        fixed (byte* data = input)
        fixed (byte* purpose = Purpose)
        {
            var dataIn = new Blob { Size = (uint)input.Length, Data = data };
            var entropy = new Blob { Size = (uint)Purpose.Length, Data = purpose };
            var dataOut = default(Blob);
            var ok = protect
                ? CryptProtectData(&dataIn, null, &entropy, null, null, UiForbidden, &dataOut)
                : CryptUnprotectData(&dataIn, null, &entropy, null, null, UiForbidden, &dataOut);
            if (ok == 0 || dataOut.Data is null)
            {
                return null;
            }
            try
            {
                return new ReadOnlySpan<byte>(dataOut.Data, (int)dataOut.Size).ToArray();
            }
            finally
            {
                new Span<byte>(dataOut.Data, (int)dataOut.Size).Clear();
                LocalFree((nint)dataOut.Data);
            }
        }
    }

    /// <summary>The kept sign-in; null when there is none or it cannot be read here (then the account is linked again).</summary>
    public string? Load()
    {
        try
        {
            if (!File.Exists(Path) || Unprotect(File.ReadAllBytes(Path)) is not { Length: > 0 } plain)
            {
                return null;
            }
            var token = Encoding.UTF8.GetString(plain);
            Array.Clear(plain);
            return token;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Keeps the sign-in (through a temporary file and a rename). False when it could not be protected or written.</summary>
    public bool Save(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        var plain = Encoding.UTF8.GetBytes(refreshToken);
        try
        {
            if (Protect(plain) is not { } hidden)
            {
                return false;
            }
            var tmp = Path + ".tmp";
            File.WriteAllBytes(tmp, hidden);
            File.Move(tmp, Path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>Forgets the sign-in.</summary>
    public void Clear()
    {
        try
        {
            File.Delete(Path);
            File.Delete(Path + ".tmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // gone at the next unlink; without client.toml's [nextendo] it is never read
        }
    }
}

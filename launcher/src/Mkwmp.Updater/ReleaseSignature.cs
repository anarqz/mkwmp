// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Mkwmp.UpdateHelper;

/// <summary>The helper's Ed25519 check (BouncyCastle, as in Infinity Launcher's updater): no native library beside the exe.</summary>
internal static class ReleaseSignature
{
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != Ed25519.PublicKeySize || signature.Length != Ed25519.SignatureSize)
        {
            return false;
        }
        var key = publicKey.ToArray();
        return Ed25519.ValidatePublicKeyFull(key, 0) && Ed25519.Verify(signature.ToArray(), 0, key, 0, message.ToArray(), 0, message.Length);
    }
}

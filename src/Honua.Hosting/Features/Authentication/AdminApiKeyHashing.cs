// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Infrastructure.Authentication;

// New and rotated keys are stored as HMAC-SHA256 under a per-key random salt. Unsalted SHA-256
// records written by earlier releases are still verified and are rewritten on first use.
internal static class AdminApiKeyHashing
{
    internal const int SaltSizeInBytes = 32;

    internal static byte[] HashLegacy(string keyMaterial) => SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));

    internal static (byte[] Hash, byte[] Salt) HashSalted(string keyMaterial)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeInBytes);
        return (HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(keyMaterial)), salt);
    }

    internal static bool Verify(string keyMaterial, byte[] expectedHash, byte[]? salt)
    {
        if (expectedHash.Length != SHA256.HashSizeInBytes || (salt is not null && salt.Length != SaltSizeInBytes))
        {
            return false;
        }

        var actualHash = salt is null
            ? HashLegacy(keyMaterial)
            : HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(keyMaterial));
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Infrastructure.Authentication;

internal static class AdminApiKeyHashing
{
    internal static byte[] HashLegacy(string keyMaterial) => SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));

    internal static bool Verify(string keyMaterial, byte[] expectedHash, byte[]? salt)
    {
        if (expectedHash.Length != SHA256.HashSizeInBytes || (salt is not null && salt.Length != 32))
        {
            return false;
        }

        var actualHash = salt is null
            ? HashLegacy(keyMaterial)
            : HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(keyMaterial));
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}

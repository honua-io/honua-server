// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;
using Org.BouncyCastle.Crypto.Parameters;

namespace Honua.LoadTests;

internal static class CapacitySoakLicense
{
    internal static string WriteLicenseAndReturnTrustedKey(string path)
    {
        var seed = RandomNumberGenerator.GetBytes(Ed25519PrivateKeyParameters.KeySize);
        try
        {
            var license = LicenseTestSupport.CreateSignedLicenseWithOptions(
                HonuaEdition.Pro,
                new SignedLicenseTestOptions
                {
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(2),
                    KeyId = "soak",
                    SigningSeed = seed
                });
            File.WriteAllBytes(path, license.LicenseData);
            return license.PublicKeySetting;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }
}

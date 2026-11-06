// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Honua.LicenseMint;

internal sealed class LicenseSigningKeyPair
{
    private LicenseSigningKeyPair(byte[] privateSeed, byte[] publicKey)
    {
        PrivateSeed = privateSeed;
        PublicKey = publicKey;
    }

    // This 32-byte Ed25519 seed is the licensing trust root and must not be logged, committed, or shipped.
    public byte[] PrivateSeed { get; }

    public byte[] PublicKey { get; }

    public static LicenseSigningKeyPair Generate()
    {
        var seed = new byte[Ed25519PrivateKeyParameters.KeySize];
        new SecureRandom().NextBytes(seed);
        return FromPrivateSeed(seed);
    }

    public static LicenseSigningKeyPair FromPrivateSeed(ReadOnlySpan<byte> privateSeed)
    {
        if (privateSeed.Length != Ed25519PrivateKeyParameters.KeySize)
        {
            throw new ArgumentException(
                $"Ed25519 private seed must be {Ed25519PrivateKeyParameters.KeySize} bytes.",
                nameof(privateSeed));
        }

        var seedCopy = privateSeed.ToArray();
        var privateKey = new Ed25519PrivateKeyParameters(seedCopy, 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        return new LicenseSigningKeyPair(seedCopy, publicKey);
    }

    public string PublicKeyTrustedKeySetting() => "base64url:" + Base64Url.Encode(PublicKey);

    public string PublicKeyBase64Url() => Base64Url.Encode(PublicKey);

    public string PrivateSeedBase64Url() => Base64Url.Encode(PrivateSeed);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Honua.LicenseMint;

/// <summary>
/// An Ed25519 signing key pair used to mint and verify Honua license envelopes.
/// </summary>
/// <remarks>
/// The private key (the 32-byte seed) is the trust root for the entire licensing
/// system. It MUST be held only by Honua's hosted mint host and never committed,
/// distributed to customers, or embedded in a published artifact. The public key
/// is what operators configure as a trusted verification key under
/// <c>Licensing:TrustedKeys:&lt;keyId&gt;</c>.
/// </remarks>
internal sealed class LicenseSigningKeyPair
{
    private LicenseSigningKeyPair(byte[] privateSeed, byte[] publicKey)
    {
        PrivateSeed = privateSeed;
        PublicKey = publicKey;
    }

    /// <summary>The 32-byte Ed25519 private seed. Secret material — never log or commit.</summary>
    public byte[] PrivateSeed { get; }

    /// <summary>The 32-byte Ed25519 public key used for offline verification.</summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// Generates a fresh Ed25519 key pair using a cryptographically secure RNG.
    /// </summary>
    public static LicenseSigningKeyPair Generate()
    {
        var seed = new byte[Ed25519PrivateKeyParameters.KeySize];
        new SecureRandom().NextBytes(seed);
        return FromPrivateSeed(seed);
    }

    /// <summary>
    /// Reconstructs a key pair from an existing 32-byte private seed.
    /// </summary>
    /// <param name="privateSeed">The 32-byte Ed25519 private seed.</param>
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

    /// <summary>
    /// Returns the public key in the exact format operators paste into
    /// <c>Licensing:TrustedKeys:&lt;keyId&gt;</c> (the runtime accepts a
    /// <c>base64url:</c>-prefixed Base64URL key).
    /// </summary>
    public string PublicKeyTrustedKeySetting() => "base64url:" + Base64Url.Encode(PublicKey);

    /// <summary>Returns the unprefixed Base64URL-encoded public key.</summary>
    public string PublicKeyBase64Url() => Base64Url.Encode(PublicKey);

    /// <summary>Returns the unprefixed Base64URL-encoded private seed. Secret material.</summary>
    public string PrivateSeedBase64Url() => Base64Url.Encode(PrivateSeed);
}

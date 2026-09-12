// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography.X509Certificates;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Resolves the operator-supplied certificate that encrypts the persisted data-protection key
/// ring at rest.
/// </summary>
/// <remarks>
/// The key ring shares the Redis authority that already holds operation proposals, handles and
/// the protected credential envelopes, so the ring alone is not a boundary against a reader of
/// that authority: a snapshot would carry both the ciphertext and the keys. Supplying a
/// certificate here moves the decryption material outside Redis and restores that boundary.
/// Without one the channel still keeps plaintext credentials out of every durable store, which
/// is the defect this guards, but Redis remains inside the trust boundary.
/// </remarks>
internal static class OperationSecretKeyRingProtection
{
    internal const string CertificatePathKey = "Operations:SecretChannel:KeyRingCertificatePath";
    internal const string CertificatePasswordKey = "Operations:SecretChannel:KeyRingCertificatePassword";

    /// <summary>Loads the configured key-ring certificate, or null when none is configured.</summary>
    /// <param name="configuration">The server configuration.</param>
    /// <returns>The certificate, or <see langword="null"/> when unconfigured.</returns>
    public static X509Certificate2? Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = configuration[CertificatePathKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            // Configured-but-missing is an operator error, not a reason to silently downgrade
            // to an unencrypted key ring.
            throw new InvalidOperationException(
                $"'{CertificatePathKey}' is set to '{path}', but no certificate exists at that path.");
        }

        var password = configuration[CertificatePasswordKey];
        return string.IsNullOrEmpty(password)
            ? X509CertificateLoader.LoadPkcs12FromFile(path, password: null)
            : X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }
}

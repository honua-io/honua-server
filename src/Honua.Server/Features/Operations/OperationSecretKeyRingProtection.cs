// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Resolves the operator-supplied certificate that encrypts the persisted data-protection key
/// ring at rest.
/// </summary>
/// <remarks>
/// The key ring shares the Redis authority that already holds operation proposals, handles and
/// the protected credential envelopes, so the ring alone is not a boundary against a reader of
/// that authority: a snapshot would carry both the ciphertext and the keys. Supplying a
/// certificate here moves the decryption material outside Redis and restores that boundary. The
/// certificate is mandatory whenever this durable channel is composed: ciphertext without
/// separately protected key material is not a durable-store boundary.
/// </remarks>
internal static class OperationSecretKeyRingProtection
{
    internal const string CertificatePathKey = "Operations:SecretChannel:KeyRingCertificatePath";
    internal const string CertificatePasswordKey = "Operations:SecretChannel:KeyRingCertificatePassword";

    /// <summary>
    /// PKCS#12 material for hosts that cannot mount a file. The value is either standard base64
    /// or a JSON object <c>{"pkcs12":"&lt;base64&gt;","password":"..."}</c>. An
    /// <c>aws:secretsmanager:</c> reference is resolved before this runs.
    /// </summary>
    internal const string CertificateMaterialKey = "Operations:SecretChannel:KeyRingCertificatePkcs12";

    /// <summary>PKCS#12 bytes written for <see cref="Resolve"/>, plus a password carried inside the bundle.</summary>
    /// <param name="Path">Filesystem path of the written certificate.</param>
    /// <param name="Password">Password from a JSON bundle, or <see langword="null"/> when the bundle had none.</param>
    internal readonly record struct MaterializedKeyRing(string Path, string? Password);

    /// <summary>
    /// Writes PKCS#12 material to a private file under the temp directory.
    /// </summary>
    /// <param name="material">Base64 PKCS#12, or a JSON bundle containing <c>pkcs12</c> and an optional <c>password</c>.</param>
    /// <returns>The written path and any password embedded in the bundle.</returns>
    public static MaterializedKeyRing WritePkcs12Material(string material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(material);
        string? password = null;
        byte[] bytes;
        var trimmed = material.Trim();
        try
        {
            if (trimmed.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(trimmed);
                if (!document.RootElement.TryGetProperty("pkcs12", out var pkcs12) ||
                    pkcs12.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(pkcs12.GetString()))
                {
                    throw new InvalidOperationException(
                        $"'{CertificateMaterialKey}' JSON must contain a pkcs12 string.");
                }

                bytes = Convert.FromBase64String(pkcs12.GetString()!);
                if (document.RootElement.TryGetProperty("password", out var passwordElement) &&
                    passwordElement.ValueKind == JsonValueKind.String)
                {
                    password = passwordElement.GetString();
                }
            }
            else
            {
                bytes = Convert.FromBase64String(trimmed);
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new InvalidOperationException(
                $"'{CertificateMaterialKey}' is not a PKCS#12 bundle.",
                exception);
        }

        if (bytes.Length == 0)
        {
            throw new InvalidOperationException($"'{CertificateMaterialKey}' did not contain a certificate.");
        }

        var path = Path.Join(Path.GetTempPath(), $"honua-operation-keyring-{Guid.NewGuid():N}.pfx");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(bytes);
        }

        return new MaterializedKeyRing(path, password);
    }

    /// <summary>Loads the configured key-ring certificate.</summary>
    /// <param name="configuration">The server configuration.</param>
    /// <returns>The certificate with its private key.</returns>
    public static X509Certificate2 Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = configuration[CertificatePathKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"'{CertificatePathKey}' is required when the durable operation secret channel is enabled.");
        }

        if (!File.Exists(path))
        {
            // Configured-but-missing is an operator error, not a reason to silently downgrade
            // to an unencrypted key ring.
            throw new InvalidOperationException(
                $"'{CertificatePathKey}' is set to '{path}', but no certificate exists at that path.");
        }

        var password = configuration[CertificatePasswordKey];
        var certificate = string.IsNullOrEmpty(password)
            ? X509CertificateLoader.LoadPkcs12FromFile(path, password: null)
            : X509CertificateLoader.LoadPkcs12FromFile(path, password);
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                $"'{CertificatePathKey}' must reference a certificate with a private key.");
        }

        return certificate;
    }
}

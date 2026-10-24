// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
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

    /// <summary>Uid of the published image (<c>USER 1001:1001</c>).</summary>
    internal const uint PublishedContainerUserId = 1001;

    /// <summary>Gid of the published image (<c>USER 1001:1001</c>).</summary>
    internal const uint PublishedContainerGroupId = 1001;

    /// <summary>
    /// Root-owned mode 0640: owner read/write, group read, no other bits. The published
    /// image can read this when the file's group is <see cref="PublishedContainerGroupId"/>.
    /// Other-read is not required.
    /// </summary>
    internal const UnixFileMode RootOwnedGroupReadMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

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

        // A private key lands on disk here, so the file is created privately and
        // uniquely rather than written and then tightened. WriteAllBytes to a fixed
        // /tmp name created the file under the process umask and only chmod'd it to
        // 0600 afterwards, which on a shared host leaves a window where another local
        // user can read it, lets one pre-create the path as a symlink, and lets two
        // Honua processes overwrite each other's certificate. UnixCreateMode applies
        // 0600 at open(2) time and CreateNew (O_EXCL) refuses an existing path.
        var path = Path.Join(
            Path.GetTempPath(),
            $"honua-operation-keyring-{Guid.NewGuid():N}.pfx");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
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

    /// <summary>
    /// Development and Test may start without a key-ring certificate. Every other
    /// environment that has connected to Redis composes the durable channel and requires one.
    /// </summary>
    /// <param name="isDevelopment">True when the host environment is Development.</param>
    /// <param name="isTest">True when the host environment is Test.</param>
    /// <param name="redisConnected">True when a Redis multiplexer was established.</param>
    /// <returns>True when startup must load the key-ring certificate.</returns>
    public static bool RequiresDurableChannelCertificate(bool isDevelopment, bool isTest, bool redisConnected)
        => redisConnected && !isDevelopment && !isTest;

    /// <summary>
    /// POSIX read check. A root-owned mode <c>0640</c> file is readable by the published
    /// container user when <paramref name="group"/> is that user's group. Other-read is
    /// sufficient but not required.
    /// </summary>
    /// <param name="owner">File owner uid.</param>
    /// <param name="group">File group gid.</param>
    /// <param name="mode">File mode.</param>
    /// <param name="userId">Process effective uid.</param>
    /// <param name="groupId">Process effective gid.</param>
    /// <returns>True when the process can read the file.</returns>
    internal static bool IsReadable(uint owner, uint group, UnixFileMode mode, uint userId, uint groupId)
    {
        if (owner == userId && (mode & UnixFileMode.UserRead) != 0)
        {
            return true;
        }

        if (group == groupId && (mode & UnixFileMode.GroupRead) != 0)
        {
            return true;
        }

        return (mode & UnixFileMode.OtherRead) != 0;
    }

    /// <summary>
    /// True for a root-owned mode 0640 file whose group is <paramref name="processGroup"/>.
    /// </summary>
    /// <param name="owner">File owner uid.</param>
    /// <param name="group">File group gid.</param>
    /// <param name="mode">File mode.</param>
    /// <param name="processGroup">Group the server process runs as.</param>
    /// <returns>True when the mount is the supported non-world-readable shape.</returns>
    internal static bool IsRootOwnedMode0640(uint owner, uint group, UnixFileMode mode, uint processGroup)
        => owner == 0 && group == processGroup && mode == RootOwnedGroupReadMode;

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
                $"'{CertificatePathKey}' is required when the durable operation secret channel is enabled. "
                + $"Redis turns that channel on. Mount a PKCS#12 certificate and set '{CertificatePathKey}', or on a "
                + $"host that cannot mount files (AWS Lambda) set '{CertificateMaterialKey}' to a Secrets Manager reference "
                + "for the bundle.");
        }

        if (!File.Exists(path))
        {
            // Configured-but-missing is an operator error, not a reason to silently downgrade
            // to an unencrypted key ring.
            throw new InvalidOperationException(
                $"'{CertificatePathKey}' is set to '{path}', but no certificate exists at that path.");
        }

        var password = configuration[CertificatePasswordKey];
        try
        {
            // A mode 000 mount makes the PKCS#12 loader throw CryptographicException.
            // Probe the file first so that case stays the ownership refusal.
            using var probe = File.OpenRead(path);
        }
        catch (Exception exception) when (IsCertificateUnreadable(exception))
        {
            throw UnreadableMount(path, exception);
        }

        X509Certificate2 certificate;
        try
        {
            certificate = string.IsNullOrEmpty(password)
                ? X509CertificateLoader.LoadPkcs12FromFile(path, password: null)
                : X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        catch (CryptographicException exception) when (IsCertificateUnreadable(exception))
        {
            throw UnreadableMount(path, exception);
        }
        catch (CryptographicException exception)
        {
            // A bad file or export password throws CryptographicException, not
            // InvalidOperationException. The exception text can describe the certificate,
            // so the refusal names only the exception type.
            throw new InvalidOperationException(
                $"'{CertificatePathKey}' could not be loaded as a PKCS#12 certificate ({exception.GetType().Name}). Check the file and its export password.");
        }
        catch (Exception exception) when (IsCertificateUnreadable(exception))
        {
            // A bind mount keeps the host mode. uid 1001 cannot read a root-owned 0640
            // file unless the file's group is the process group. Do not ask the operator
            // to grant other-read; the export password still encrypts the PKCS#12.
            throw UnreadableMount(path, exception);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                $"'{CertificatePathKey}' must reference a certificate with a private key.");
        }

        return certificate;
    }

    private static bool IsCertificateUnreadable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static InvalidOperationException UnreadableMount(string path, Exception exception)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return new InvalidOperationException(
                $"'{CertificatePathKey}' is set to '{path}', but this process cannot read it.",
                exception);
        }

        var supported = IsRootOwnedMode0640(
                owner: 0,
                group: PublishedContainerGroupId,
                RootOwnedGroupReadMode,
                PublishedContainerGroupId)
            && IsReadable(
                owner: 0,
                group: PublishedContainerGroupId,
                RootOwnedGroupReadMode,
                PublishedContainerUserId,
                PublishedContainerGroupId);
        var message = supported
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"'{CertificatePathKey}' is set to '{path}', but this process cannot read it. A root-owned mode 0640 PKCS#12 is accepted when its group is the server group. The published image is uid {PublishedContainerUserId}, gid {PublishedContainerGroupId}. Other-read is not required. The export password still encrypts the PKCS#12. '{CertificateMaterialKey}' is available when a mount is not possible.")
            : $"'{CertificatePathKey}' is set to '{path}', but this process cannot read it.";
        return new InvalidOperationException(message, exception);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Honua.Server.Features.Operations;

// The certificate is mandatory whenever the durable channel is composed, because ciphertext in the same store as the keys is not a boundary.
internal static class OperationSecretKeyRingProtection
{
    internal const string CertificatePathKey = "Operations:SecretChannel:KeyRingCertificatePath";
    internal const string CertificatePasswordKey = "Operations:SecretChannel:KeyRingCertificatePassword";

    internal const uint PublishedContainerUserId = 1001;

    internal const uint PublishedContainerGroupId = 1001;

    internal const UnixFileMode RootOwnedGroupReadMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    internal const string CertificateMaterialKey = "Operations:SecretChannel:KeyRingCertificatePkcs12";

    internal readonly record struct MaterializedKeyRing(string Path, string? Password);

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

    // Development and Test may start without a key-ring certificate; every other environment that connected to Redis requires one.
    public static bool RequiresDurableChannelCertificate(bool isDevelopment, bool isTest, bool redisConnected)
        => redisConnected && !isDevelopment && !isTest;

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

    internal static bool IsRootOwnedMode0640(uint owner, uint group, UnixFileMode mode, uint processGroup)
        => owner == 0 && group == processGroup && mode == RootOwnedGroupReadMode;

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

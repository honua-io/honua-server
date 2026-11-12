// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Honua.Server.Tests.Hosting;

// Production refuses to boot with a database but no key-ring certificate, so out-of-process boot
// tests that assert a later refusal mount a throwaway one to get past that check.
internal sealed class TemporaryKeyRingCertificate : IDisposable
{
    private const string Password = "temporary-key-ring-password";

    private readonly string _path;

    private TemporaryKeyRingCertificate(string path) => _path = path;

    public static TemporaryKeyRingCertificate Create()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Honua boot refusal key ring",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Join(Path.GetTempPath(), $"honua-boot-keyring-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, Password));
        return new TemporaryKeyRingCertificate(path);
    }

    public void ApplyTo(IDictionary<string, string?> environment)
    {
        environment["Operations__SecretChannel__KeyRingCertificatePath"] = _path;
        environment["Operations__SecretChannel__KeyRingCertificatePassword"] = Password;
    }

    public void Dispose() => File.Delete(_path);
}

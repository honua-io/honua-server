// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Honua.TestKit.Infrastructure;

// Outside Development and Test, a host with a database refuses to start without a key-ring certificate.
public static class TestKeyRingCertificate
{
    public const string PathKey = "Operations:SecretChannel:KeyRingCertificatePath";
    public const string PasswordKey = "Operations:SecretChannel:KeyRingCertificatePassword";

    public static string Write(string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Honua test key ring", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Join(Path.GetTempPath(), $"honua-test-keyring-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        return path;
    }
}

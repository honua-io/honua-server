// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Operations;

public sealed class OperationSecretKeyRingProtectionTests
{
    [UnitTest]
    public void Resolve_WithoutCertificatePath_FailsClosed()
    {
        var configuration = new ConfigurationBuilder().Build();

        var action = () => OperationSecretKeyRingProtection.Resolve(configuration);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{OperationSecretKeyRingProtection.CertificatePathKey}*required*");
    }

    [UnitTest]
    public void Resolve_WithMissingCertificatePath_FailsClosed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OperationSecretKeyRingProtection.CertificatePathKey] = "/missing/operation-secrets.pfx",
            })
            .Build();

        var action = () => OperationSecretKeyRingProtection.Resolve(configuration);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{OperationSecretKeyRingProtection.CertificatePathKey}*no certificate exists*");
    }

    [UnitTest]
    public void Resolve_WithPkcs12Certificate_ReturnsPrivateKeyCertificate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"honua-operation-secrets-{Guid.NewGuid():N}.pfx");
        const string password = "test-password";
        try
        {
            using var source = CreateCertificate();
            File.WriteAllBytes(path, source.Export(X509ContentType.Pkcs12, password));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OperationSecretKeyRingProtection.CertificatePathKey] = path,
                    [OperationSecretKeyRingProtection.CertificatePasswordKey] = password,
                })
                .Build();

            using var resolved = OperationSecretKeyRingProtection.Resolve(configuration);

            resolved.HasPrivateKey.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [UnitTest]
    public void IsProtectedElement_RequiresEncryptedSecretDescriptor()
    {
        var protectedKey = new XElement(
            "key",
            new XElement("descriptor", new XElement("encryptedSecret")));
        var legacyKey = new XElement(
            "key",
            new XElement("descriptor", new XElement("descriptor")));

        RedisDataProtectionKeyRepository.IsProtectedElement(protectedKey).Should().BeTrue();
        RedisDataProtectionKeyRepository.IsProtectedElement(legacyKey).Should().BeFalse();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Honua operation secret test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
    }
}

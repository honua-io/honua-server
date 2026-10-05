// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationSecretKeyRingProtectionTests;

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
    public void WritePkcs12Material_FromJsonBundle_ResolvesWithEmbeddedPassword()
    {
        using var source = CreateCertificate();
        const string password = "bundle-password";
        var bundle = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["pkcs12"] = Convert.ToBase64String(source.Export(X509ContentType.Pkcs12, password)),
            ["password"] = password,
        });

        var written = OperationSecretKeyRingProtection.WritePkcs12Material(bundle);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OperationSecretKeyRingProtection.CertificatePathKey] = written.Path,
                    [OperationSecretKeyRingProtection.CertificatePasswordKey] = written.Password,
                })
                .Build();

            using var resolved = OperationSecretKeyRingProtection.Resolve(configuration);
            resolved.HasPrivateKey.Should().BeTrue();
        }
        finally
        {
            File.Delete(written.Path);
        }
    }

    [UnitTest]
    public void WritePkcs12Material_WithGarbage_DoesNotEchoTheMaterial()
    {
        const string material = "not-a-certificate";

        var action = () => OperationSecretKeyRingProtection.WritePkcs12Material(material);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{OperationSecretKeyRingProtection.CertificateMaterialKey}*")
            .Where(exception => !exception.Message.Contains(material, StringComparison.Ordinal));
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

    // The marker sits beside the descriptor XmlKeyManager imports. That sibling is not
    // part of the master-key material, so the plaintext descriptor must still be rejected.
    [UnitTest]
    public void IsProtectedElement_RejectsPlaintextDescriptorDespiteUnrelatedMarker()
    {
        var key = XElement.Load(PlaintextDescriptorWithUnrelatedMarkerPath());
        var consumed = key.Element("descriptor")!.Elements().Single();

        key.Descendants().Any(element =>
                string.Equals(element.Name.LocalName, "encryptedSecret", StringComparison.Ordinal))
            .Should().BeTrue();
        consumed.DescendantsAndSelf().Any(element =>
                string.Equals(element.Name.LocalName, "encryptedSecret", StringComparison.Ordinal))
            .Should().BeFalse();
        consumed.Element("masterKey")!.Element("value")!.Value.Should().NotBeNullOrWhiteSpace();

        RedisDataProtectionKeyRepository.IsProtectedElement(key).Should().BeFalse();
    }

    // A private key on disk gets a unique name and 0600 at creation time, not a fixed
    // /tmp path tightened after the bytes land - otherwise a shared host leaves a
    // readable window, a symlink can be pre-created, and two processes clobber
    // each other's certificate.
    [UnitTest]
    public void WritePkcs12Material_CreatesAPrivateUniqueFile()
    {
        using var source = CreateCertificate();
        var material = Convert.ToBase64String(source.Export(X509ContentType.Pkcs12));

        var first = OperationSecretKeyRingProtection.WritePkcs12Material(material);
        var second = OperationSecretKeyRingProtection.WritePkcs12Material(material);
        try
        {
            second.Path.Should().NotBe(first.Path, "two processes must not share one keyring path");
            File.Exists(first.Path).Should().BeTrue();
            File.Exists(second.Path).Should().BeTrue();

            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(first.Path).Should()
                    .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            File.Delete(first.Path);
            File.Delete(second.Path);
        }
    }

    [UnitTest]
    public void AotKeyRing_RoundTripsAcrossProvidersThatShareTheCertificate()
    {
        using var certificate = CreateCertificate();
        var repository = new MemoryKeyRepository();
        var first = Protect(repository, certificate, "approved-secret");
        var second = Unprotect(repository, certificate, first);

        second.Should().Be("approved-secret");
        repository.Elements.Should().ContainSingle();
        RedisDataProtectionKeyRepository.IsProtectedElement(repository.Elements[0]).Should().BeTrue();
    }

    private static string PlaintextDescriptorWithUnrelatedMarkerPath()
        => Path.Join(
            AppContext.BaseDirectory,
            "Features",
            "Operations",
            "Fixtures",
            "plaintext-descriptor-with-unrelated-marker.xml");

    private static string Protect(MemoryKeyRepository repository, X509Certificate2 certificate, string value)
    {
        using var provider = BuildProvider(repository, certificate);
        return provider.GetDataProtector("Honua.Server.Operations").Protect(value);
    }

    private static string Unprotect(MemoryKeyRepository repository, X509Certificate2 certificate, string value)
    {
        using var provider = BuildProvider(repository, certificate);
        return provider.GetDataProtector("Honua.Server.Operations").Unprotect(value);
    }

    private static ServiceProvider BuildProvider(MemoryKeyRepository repository, X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new RsaAesGcmKeyRingMaterial(certificate));
        services.AddDataProtection()
            .SetApplicationName("Honua.Server")
            .AddKeyManagementOptions(options =>
            {
                options.XmlRepository = repository;
                options.XmlEncryptor = new RsaAesGcmKeyRingEncryptor(certificate);
            });
        return services.BuildServiceProvider();
    }

    private sealed class MemoryKeyRepository : IXmlRepository
    {
        public List<XElement> Elements { get; } = new();

        public IReadOnlyCollection<XElement> GetAllElements()
            => Elements.Select(element => new XElement(element)).ToArray();

        public void StoreElement(XElement element, string friendlyName)
            => Elements.Add(new XElement(element));
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

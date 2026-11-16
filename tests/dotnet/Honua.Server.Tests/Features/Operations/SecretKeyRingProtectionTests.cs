// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Server.Features.Operations;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationSecretKeyRing;

public sealed class SecretKeyRingProtectionTests
{
    private const string Purpose = "Honua.Server.Operations";
    private static readonly XNamespace _dataProtection = "http://schemas.asp.net/2015/03/dataProtection";

    [UnitTest]
    public void Parse_WithShortSecret_RefusesWithoutEchoingIt()
    {
        var secret = NewSecret()[..(SecretKeyRingMaterial.MinimumSecretLength - 1)];

        var action = () => SecretKeyRingMaterial.Parse(secret);

        action.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(OperationSecretKeyRingProtection.SecretKey)
            .And.Contain("43")
            .And.NotContain(secret);
    }

    [UnitTheory]
    [InlineData("azure:keyvault:https://prod-kv.vault.azure.net/secrets/honua-keyring")]
    [InlineData("aws:secretmanager:arn:aws:secretsmanager:us-east-1:000000000000:secret:keyring")]
    [InlineData("env:HONUA_KEY_RING_SECRET_THAT_WAS_NEVER_RESOLVED_BY_STARTUP")]
    public void Parse_WithAnUnresolvedReference_RefusesInsteadOfUsingItAsTheKey(string reference)
    {
        var action = () => SecretKeyRingMaterial.Parse(reference);

        action.Should().Throw<InvalidOperationException>().WithMessage("*reference*not resolved*");
    }

    [UnitTest]
    public void Parse_WithARepeatedCharacter_Refuses()
    {
        var action = () => SecretKeyRingMaterial.Parse(new string('a', 64));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{OperationSecretKeyRingProtection.SecretKey}*random secret*");
    }

    [UnitTest]
    public void EnsureReadable_WithAChangedSecret_RefusesNamingBothFingerprints()
    {
        var written = SecretKeyRingMaterial.Parse(NewSecret());
        var configured = SecretKeyRingMaterial.Parse(NewSecret());
        var repository = new MemoryKeyRepository();
        _ = Protect(repository, new(null, written), "approved-secret");

        var action = () => SecretKeyRingCipher.EnsureReadable(repository.Elements, new(null, configured));

        action.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(written.Fingerprint).And.Contain(configured.Fingerprint);
        SecretKeyRingCipher.EnsureReadable(repository.Elements, new(null, written));
    }

    [UnitTest]
    public void EnsureReadable_WhenTheSecretWasRemoved_RefusesAndLeavesCertificateKeysAlone()
    {
        using var certificate = CreateCertificate();
        var certificateRing = new MemoryKeyRepository();
        _ = Protect(certificateRing, new(certificate, null), "wrapped-by-certificate");
        var secretRing = new MemoryKeyRepository();
        _ = Protect(secretRing, new(null, SecretKeyRingMaterial.Parse(NewSecret())), "wrapped-by-secret");

        var action = () => SecretKeyRingCipher.EnsureReadable(secretRing.Elements, new(certificate, null));

        action.Should().Throw<InvalidOperationException>().WithMessage("*configured none*");
        SecretKeyRingCipher.EnsureReadable(certificateRing.Elements, new(certificate, null));
    }

    [UnitTest]
    public void EnsureReadable_WhenTheCurrentCertificateWasRemoved_RefusesWithoutChangingTheRing()
    {
        using var certificate = CreateCertificate();
        var repository = new MemoryKeyRepository();
        var payload = Protect(repository, new(certificate, null), "wrapped-by-certificate");

        AssertCertificateRetentionRequired(repository, certificate, payload);
    }

    [UnitTest]
    public void EnsureReadable_WhenTheLegacyCertificateWasRemoved_RefusesWithoutChangingTheRing()
    {
        using var certificate = CreateCertificate();
        var repository = new MemoryKeyRepository();
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("Honua.Server")
            .AddKeyManagementOptions(options => options.XmlRepository = repository)
            .ProtectKeysWithCertificate(certificate);
        using var provider = services.BuildServiceProvider();
        var payload = provider.GetDataProtector(Purpose).Protect("wrapped-by-certificate");

        AssertCertificateRetentionRequired(repository, certificate, payload);
    }

    private static void AssertCertificateRetentionRequired(
        MemoryKeyRepository repository, X509Certificate2 certificate, string payload)
    {
        var secret = SecretKeyRingMaterial.Parse(NewSecret());
        var before = repository.Elements.Select(element => element.ToString()).ToArray();
        var action = () => SecretKeyRingCipher.EnsureReadable(repository.Elements, new(null, secret));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Retain*certificate*private key*does not rewrap*");
        repository.Elements.Select(element => element.ToString()).Should().Equal(before);
        SecretKeyRingCipher.EnsureReadable(repository.Elements, new(certificate, secret));
        Unprotect(repository, new(certificate, secret), payload).Should().Be("wrapped-by-certificate");
    }

    [UnitTest]
    public void EnsureReadable_WithSecretOnly_AcceptsEmptyAndSecretWrappedRings()
    {
        var secret = SecretKeyRingMaterial.Parse(NewSecret());
        var repository = new MemoryKeyRepository();
        SecretKeyRingCipher.EnsureReadable(repository.Elements, new(null, secret));
        var payload = Protect(repository, new(null, secret), "wrapped-by-secret");

        SecretKeyRingCipher.EnsureReadable(repository.Elements, new(null, secret));
        Unprotect(repository, new(null, secret), payload).Should().Be("wrapped-by-secret");
    }

    [UnitTest]
    public void Parse_IgnoresSurroundingWhitespaceFromASecretFile()
    {
        var secret = NewSecret();

        SecretKeyRingMaterial.Parse(secret + "\n").Fingerprint
            .Should().Be(SecretKeyRingMaterial.Parse(secret).Fingerprint);
    }

    [UnitTest]
    public void KeyRing_RoundTripsAcrossProvidersThatShareTheSecret()
    {
        var secret = NewSecret();
        var repository = new MemoryKeyRepository();
        var payload = Protect(repository, new(null, SecretKeyRingMaterial.Parse(secret)), "approved-secret");

        Unprotect(repository, new(null, SecretKeyRingMaterial.Parse(secret)), payload).Should().Be("approved-secret");
        var stored = repository.Elements.Should().ContainSingle().Subject;
        DataProtectionKeyElementProtection.IsProtected(stored).Should().BeTrue();
        DecryptorTypes(repository).Should().ContainSingle()
            .Which.Should().Be(typeof(SecretKeyRingDecryptor).AssemblyQualifiedName);
        stored.ToString().Should().NotContain(secret).And.NotContain("approved-secret");
    }

    [UnitTest]
    public void KeyRing_WithADifferentSecret_DoesNotUnprotect()
    {
        var repository = new MemoryKeyRepository();
        var payload = Protect(repository, new(null, SecretKeyRingMaterial.Parse(NewSecret())), "approved-secret");

        var action = () => Unprotect(repository, new(null, SecretKeyRingMaterial.Parse(NewSecret())), payload);

        action.Should().Throw<CryptographicException>();
    }

    [UnitTest]
    public void Decrypt_WithADifferentSecret_NamesTheSettingAndBothFingerprints()
    {
        var written = SecretKeyRingMaterial.Parse(NewSecret());
        var configured = SecretKeyRingMaterial.Parse(NewSecret());
        var encrypted = SecretKeyRingCipher.Encrypt(new XElement("descriptor", "key-bytes"), written);

        var action = () => SecretKeyRingCipher.Decrypt(encrypted, configured);

        action.Should().Throw<CryptographicException>()
            .Which.Message.Should().Contain(OperationSecretKeyRingProtection.SecretKey)
            .And.Contain(written.Fingerprint)
            .And.Contain(configured.Fingerprint);
    }

    [UnitTheory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("nonce")]
    [InlineData("salt")]
    public void Decrypt_WithAnAlteredField_Refuses(string field)
    {
        var material = SecretKeyRingMaterial.Parse(NewSecret());
        var encrypted = SecretKeyRingCipher.Encrypt(new XElement("descriptor", "key-bytes"), material);
        var element = encrypted.Elements().Single(candidate => candidate.Name.LocalName == field);
        var bytes = Convert.FromBase64String(element.Value);
        bytes[0] ^= 0x01;
        element.Value = Convert.ToBase64String(bytes);

        var action = () => SecretKeyRingCipher.Decrypt(encrypted, material);

        action.Should().Throw<CryptographicException>();
    }

    [UnitTest]
    public void Encrypt_UsesAFreshSaltAndNonceForEveryKey()
    {
        var material = SecretKeyRingMaterial.Parse(NewSecret());
        var plaintext = new XElement("descriptor", "key-bytes");

        var first = SecretKeyRingCipher.Encrypt(plaintext, material);
        var second = SecretKeyRingCipher.Encrypt(plaintext, material);

        Field(first, "salt").Should().NotBe(Field(second, "salt"));
        Field(first, "nonce").Should().NotBe(Field(second, "nonce"));
        Field(first, "ciphertext").Should().NotBe(Field(second, "ciphertext"));
        SecretKeyRingCipher.Decrypt(second, material).Value.Should().Be("key-bytes");
    }

    [UnitTest]
    public void ResolveProtector_WithOnlyTheSecret_NeedsNoCertificate()
    {
        var configuration = Configuration((OperationSecretKeyRingProtection.SecretKey, NewSecret()));

        var protector = OperationSecretKeyRingProtection.ResolveProtector(configuration);

        protector.Certificate.Should().BeNull();
        protector.Secret.Should().NotBeNull();
    }

    [UnitTest]
    public void ResolveProtector_WithNeitherSetting_NamesTheCertificateAndTheSecret()
    {
        var action = () => OperationSecretKeyRingProtection.ResolveProtector(Configuration());

        action.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(OperationSecretKeyRingProtection.CertificatePathKey)
            .And.Contain(OperationSecretKeyRingProtection.CertificateMaterialKey)
            .And.Contain(OperationSecretKeyRingProtection.SecretKey);
    }

    [UnitTest]
    public void ResolveProtector_WithTheSecretAndAMissingCertificate_StillRefusesTheCertificate()
    {
        var missing = Path.Join(Path.GetTempPath(), "honua-missing-keyring-" + Guid.NewGuid().ToString("N") + ".pfx");
        var configuration = Configuration(
            (OperationSecretKeyRingProtection.SecretKey, NewSecret()),
            (OperationSecretKeyRingProtection.CertificatePathKey, missing));

        var action = () => OperationSecretKeyRingProtection.ResolveProtector(configuration);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{OperationSecretKeyRingProtection.CertificatePathKey}*no certificate exists*");
    }

    [UnitTest]
    public void KeyRing_WithCertificateAndSecret_KeepsWritingTheCertificateFormatAndReadsBoth()
    {
        using var certificate = CreateCertificate();
        var secret = SecretKeyRingMaterial.Parse(NewSecret());
        var repository = new MemoryKeyRepository();
        var secretPayload = Protect(repository, new(null, secret), "wrapped-by-secret");
        string certificatePayload;
        using (var both = BuildProvider(repository, new(certificate, secret)))
        {
            both.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
            certificatePayload = both.GetDataProtector(Purpose).Protect("wrapped-by-certificate");
        }

        DecryptorTypes(repository).Should().Equal(
            typeof(SecretKeyRingDecryptor).AssemblyQualifiedName,
            typeof(RsaAesGcmKeyRingDecryptor).AssemblyQualifiedName);
        Unprotect(repository, new(certificate, secret), secretPayload).Should().Be("wrapped-by-secret");
        Unprotect(repository, new(certificate, secret), certificatePayload).Should().Be("wrapped-by-certificate");
    }

    // DataProtection re-creates the decryptor by reflection from the type name in the key XML, so native AOT must keep the constructor.
    [UnitTest]
    public void ProductionRegistration_RootsTheConstructorDataProtectionActivates()
    {
        var registration = typeof(SecretKeyRingProtection).GetMethod(
            nameof(SecretKeyRingProtection.ProtectKeysWith),
            BindingFlags.Public | BindingFlags.Static)!;

        var roots = registration.GetCustomAttributes<DynamicDependencyAttribute>()
            .Where(root => root.Type == typeof(SecretKeyRingDecryptor))
            .ToArray();

        roots.Should().ContainSingle();
        roots[0].MemberTypes.Should().HaveFlag(DynamicallyAccessedMemberTypes.PublicConstructors);
        typeof(SecretKeyRingDecryptor).GetConstructor([typeof(IServiceProvider)]).Should().NotBeNull();
    }

    [UnitTest]
    public async Task EnsureKeyRingSecretResolved_ReadsAnEnvironmentReference()
    {
        var variable = "HONUA_TEST_KEYRING_SECRET_" + Guid.NewGuid().ToString("N");
        var secret = NewSecret();
        Environment.SetEnvironmentVariable(variable, secret);
        try
        {
            using var configuration = new ConfigurationManager();
            configuration[OperationSecretKeyRingProtection.SecretKey] = "env:" + variable;

            await StartupConfigurationHelpers.EnsureKeyRingSecretResolvedAsync(configuration);

            OperationSecretKeyRingProtection.ResolveProtector(configuration).Secret!.Fingerprint
                .Should().Be(SecretKeyRingMaterial.Parse(secret).Fingerprint);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static string NewSecret()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Field(XElement encrypted, string name)
        => encrypted.Elements().Single(candidate => candidate.Name.LocalName == name).Value;

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private static string[] DecryptorTypes(MemoryKeyRepository repository)
        => repository.Elements
            .SelectMany(element => element.Descendants(_dataProtection + "encryptedSecret"))
            .Select(secret => secret.Attribute("decryptorType")!.Value)
            .ToArray();

    private static string Protect(MemoryKeyRepository repository, OperationKeyRingProtector protector, string value)
    {
        using var provider = BuildProvider(repository, protector);
        return provider.GetDataProtector(Purpose).Protect(value);
    }

    private static string Unprotect(MemoryKeyRepository repository, OperationKeyRingProtector protector, string value)
    {
        using var provider = BuildProvider(repository, protector);
        return provider.GetDataProtector(Purpose).Unprotect(value);
    }

    private static ServiceProvider BuildProvider(MemoryKeyRepository repository, OperationKeyRingProtector protector)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("Honua.Server")
            .AddKeyManagementOptions(options => options.XmlRepository = repository)
            .ProtectKeysWith(protector);
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
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
    }
}

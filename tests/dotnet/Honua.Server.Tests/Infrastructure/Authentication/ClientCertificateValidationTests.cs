// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Claims;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class ClientCertificateValidationTests
{
    [Fact]
    public async Task ValidateAsync_WithMappedSanUri_ReturnsHonuaPrincipalClaims()
    {
        using var certificate = CreateCertificate("CN=Honua Native Prod", uri: "spiffe://honua/prod/admin");
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles =
            [
                CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin"),
                CreateProfile("stage-native", "stage", "CN=Honua Stage Issuer", "spiffe://honua/stage/admin")
            ]
        });

        var result = await validator.ValidateAsync(certificate);

        result.Succeeded.Should().BeTrue(result.Detail);
        result.ProfileId.Should().Be("prod-native");
        result.MappingId.Should().Be("prod-admin");
        result.PrincipalId.Should().Be("native-prod-admin");
        result.Principal.Should().NotBeNull();
        result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be("native-prod-admin");
        result.Principal.IsInRole("admin").Should().BeTrue();
        result.Principal.FindFirstValue("honua:environment_id").Should().Be("prod");
        result.Principal.FindFirstValue("honua:trust_profile_id").Should().Be("prod-native");
        result.Principal.FindFirstValue("honua:tenant_id").Should().Be("tenant-prod");
        result.Principal.FindAll("honua:environment_scope").Select(static c => c.Value)
            .Should().Contain("prod");
        result.FingerprintSha256.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ValidateAsync_WithExpiredCertificate_ReturnsExpiredCode()
    {
        using var certificate = CreateCertificate(
            "CN=Honua Native Prod",
            uri: "spiffe://honua/prod/admin",
            notBefore: DateTimeOffset.UtcNow.AddDays(-10),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin")]
        });

        var result = await validator.ValidateAsync(certificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.CertificateExpired);
    }

    [Fact]
    public async Task ValidateAsync_WithNotYetValidCertificate_ReturnsNotYetValidCode()
    {
        using var certificate = CreateCertificate(
            "CN=Honua Native Prod",
            uri: "spiffe://honua/prod/admin",
            notBefore: DateTimeOffset.UtcNow.AddDays(1),
            notAfter: DateTimeOffset.UtcNow.AddDays(30));
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin")]
        });

        var result = await validator.ValidateAsync(certificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.CertificateNotYetValid);
    }

    [Fact]
    public async Task ValidateAsync_WithUntrustedIssuer_ReturnsUntrustedIssuerCode()
    {
        using var certificate = CreateCertificate("CN=Unexpected Issuer", uri: "spiffe://honua/prod/admin");
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [CreateProfile("prod-native", "prod", "CN=Trusted Issuer", "spiffe://honua/prod/admin")]
        });

        var result = await validator.ValidateAsync(certificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.UntrustedIssuer);
    }

    [Fact]
    public async Task ValidateAsync_WithIssuerFromDifferentEnvironment_ReturnsWrongEnvironmentCode()
    {
        using var stageCertificate = CreateCertificate("CN=Honua Native Stage", uri: "spiffe://honua/stage/admin");
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles =
            [
                CreateProfile("prod-native", "prod", "CN=Honua Native Prod", "spiffe://honua/prod/admin"),
                CreateProfile("stage-native", "stage", stageCertificate.Issuer, "spiffe://honua/stage/admin")
            ]
        });

        var result = await validator.ValidateAsync(stageCertificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.WrongEnvironment);
    }

    [Fact]
    public async Task ValidateAsync_WithRevokedFingerprint_ReturnsRevokedCode()
    {
        using var certificate = CreateCertificate("CN=Honua Native Prod", uri: "spiffe://honua/prod/admin");
        var profile = CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin");
        profile.Revocations =
        [
            new ClientCertificateRevocationEntryOptions
            {
                RevocationId = "revoked-cert",
                FingerprintSha256 = ComputeFingerprint(certificate),
                Reason = "rotation",
                Actor = "test"
            }
        ];
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [profile]
        });

        var result = await validator.ValidateAsync(certificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.CertificateRevoked);
    }

    [Fact]
    public async Task ValidateAsync_WithUntrustedChain_ReturnsUntrustedChainCode()
    {
        using var certificate = CreateCertificate("CN=Honua Native Prod", uri: "spiffe://honua/prod/admin");
        var profile = CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin");
        profile.RequireChainTrust = true;
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [profile]
        });

        var result = await validator.ValidateAsync(certificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.UntrustedChain);
    }

    [Fact]
    public async Task ValidateAsync_WithoutSanAndNoFingerprintMapping_ReturnsMissingIdentityCode()
    {
        using var certificate = CreateCertificate("CN=Honua Native Prod", uri: null);
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin")]
        });

        var result = await validator.ValidateAsync(certificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.MissingIdentity);
    }

    [Fact]
    public async Task ValidateAsync_WithUnmappedSan_ReturnsUnmappedIdentityCode()
    {
        using var certificate = CreateCertificate("CN=Honua Native Prod", uri: "spiffe://honua/prod/unknown");
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin")]
        });

        var result = await validator.ValidateAsync(certificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.UnmappedIdentity);
    }

    [Fact]
    public async Task ValidateAsync_WithoutClientAuthenticationEku_ReturnsInvalidEkuCode()
    {
        using var certificate = CreateCertificate(
            "CN=Honua Native Prod",
            uri: "spiffe://honua/prod/admin",
            includeClientAuthenticationEku: false);
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin")]
        });

        var result = await validator.ValidateAsync(certificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.InvalidEku);
    }

    [Fact]
    public async Task ValidateAsync_WithCustomAnchorOnlyProfileAndUnrelatedCertificate_DoesNotMatchProfile()
    {
        using var unrelatedCertificate = CreateCertificate("CN=Honua Native Prod", uri: "spiffe://honua/prod/admin");
        using var trustAnchor = CreateCertificate("CN=Honua Prod Root CA", uri: null);
        var profile = CreateProfile("prod-native", "prod", "CN=Honua Prod Root CA", "spiffe://honua/prod/admin");
        profile.AcceptedIssuerSubjects = [];
        profile.CustomTrustAnchorCertificates = [ExportPem(trustAnchor)];
        profile.RequireChainTrust = true;

        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [profile]
        });

        var result = await validator.ValidateAsync(unrelatedCertificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.UntrustedIssuer);
    }

    [Fact]
    public void OptionsValidator_WithTwoProfilesAndDistinctMappings_Succeeds()
    {
        using var prodCertificate = CreateCertificate("CN=Honua Native Prod", uri: "spiffe://honua/prod/admin");
        var prodProfile = CreateProfile("prod-native", "prod", prodCertificate.Issuer, "spiffe://honua/prod/admin");
        prodProfile.RequireChainTrust = true;
        var stageProfile = CreateProfile("stage-native", "stage", "CN=Honua Stage Issuer", "spiffe://honua/stage/admin");
        stageProfile.RequireChainTrust = true;
        var options = new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.RequiredForAdmin,
            EnvironmentId = "prod",
            TrustProfiles = [prodProfile, stageProfile]
        };

        var result = new ClientCertificateAuthenticationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OptionsValidator_WithAcceptedIssuerSubjectsButNoChainTrust_Fails()
    {
        var profile = CreateProfile("prod-native", "prod", "CN=Honua Native Prod", "spiffe://honua/prod/admin");
        profile.RequireChainTrust = false;
        var options = new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.RequiredForAdmin,
            EnvironmentId = "prod",
            TrustProfiles = [profile]
        };

        var result = new ClientCertificateAuthenticationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("AcceptedIssuerSubjects");
        result.FailureMessage.Should().Contain("RequireChainTrust");
    }

    [Fact]
    public void OptionsValidator_WithThumbprintOnlyProfileAndNoChainTrust_Succeeds()
    {
        var profile = CreateProfile("prod-native", "prod", "CN=ignored", "spiffe://honua/prod/admin");
        profile.AcceptedIssuerSubjects = [];
        profile.AcceptedIssuerThumbprints = ["AABBCCDDEEFF00112233445566778899AABBCCDD"];
        profile.RequireChainTrust = false;
        var options = new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.RequiredForAdmin,
            EnvironmentId = "prod",
            TrustProfiles = [profile]
        };

        var result = new ClientCertificateAuthenticationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OptionsValidator_WithInvertedMappingValidityWindow_Fails()
    {
        var profile = CreateProfile("prod-native", "prod", "CN=Honua Native Prod", "spiffe://honua/prod/admin");
        profile.RequireChainTrust = true;
        profile.PrincipalMappings[0].NotBefore = DateTimeOffset.UtcNow.AddDays(10);
        profile.PrincipalMappings[0].NotAfter = DateTimeOffset.UtcNow.AddDays(1);
        var options = new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.RequiredForAdmin,
            EnvironmentId = "prod",
            TrustProfiles = [profile]
        };

        var result = new ClientCertificateAuthenticationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("NotBefore");
    }

    [Fact]
    public async Task ValidateAsync_WithMalformedCustomTrustAnchor_ReturnsSanitizedFailureWithoutThrowing()
    {
        using var certificate = CreateCertificate("CN=Honua Native Prod", uri: "spiffe://honua/prod/admin");
        var profile = CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin");
        profile.AcceptedIssuerSubjects = [];
        profile.CustomTrustAnchorCertificates = ["this-is-not-a-certificate"];
        profile.RequireChainTrust = true;

        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [profile]
        });

        var act = async () => await validator.ValidateAsync(certificate);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Succeeded.Should().BeFalse();
        result.Subject.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.UntrustedIssuer);
    }

    [Fact]
    public void OptionsValidator_WithMalformedCustomTrustAnchor_Fails()
    {
        var profile = CreateProfile("prod-native", "prod", "CN=Honua Prod Root CA", "spiffe://honua/prod/admin");
        profile.AcceptedIssuerSubjects = [];
        profile.CustomTrustAnchorCertificates = ["not-a-pem-or-base64-cert"];

        var options = new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.RequiredForAdmin,
            EnvironmentId = "prod",
            TrustProfiles = [profile]
        };

        var result = new ClientCertificateAuthenticationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("CustomTrustAnchorCertificates[0]");
    }

    [Fact]
    public void OptionsValidator_WithValidCustomTrustAnchor_Succeeds()
    {
        using var trustAnchor = CreateCertificate("CN=Honua Prod Root CA", uri: null);
        var profile = CreateProfile("prod-native", "prod", "CN=Honua Prod Root CA", "spiffe://honua/prod/admin");
        profile.AcceptedIssuerSubjects = [];
        profile.CustomTrustAnchorCertificates = [ExportPem(trustAnchor)];

        var options = new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.RequiredForAdmin,
            EnvironmentId = "prod",
            TrustProfiles = [profile]
        };

        var result = new ClientCertificateAuthenticationOptionsValidator().Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WithMalformedSanExtension_DoesNotThrowAndReturnsSanitizedFailure()
    {
        using var certificate = CreateCertificateWithMalformedSan("CN=Honua Native Prod");
        var validator = CreateValidator(new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod",
            TrustProfiles = [CreateProfile("prod-native", "prod", certificate.Issuer, "spiffe://honua/prod/admin")]
        });

        var act = async () => await validator.ValidateAsync(certificate);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Succeeded.Should().BeFalse();
        result.Subject.ErrorCode.Should().BeOneOf(
            ClientCertificateValidationErrorCode.MissingIdentity,
            ClientCertificateValidationErrorCode.UnmappedIdentity);
    }

    [Fact]
    public void TryParseTrustAnchor_WithMalformedInput_ReturnsFalseInsteadOfThrowing()
    {
        ClientCertificateValidator.TryParseTrustAnchor("not-a-certificate", out var anchor)
            .Should().BeFalse();
        anchor.Should().BeNull();
    }

    [Fact]
    public void TryParseTrustAnchor_WithValidPem_ReturnsTrueAndAnchor()
    {
        using var anchor = CreateCertificate("CN=Honua Trust Anchor", uri: null);
        var pem = ExportPem(anchor);

        var parsed = ClientCertificateValidator.TryParseTrustAnchor(pem, out var actual);

        parsed.Should().BeTrue();
        actual.Should().NotBeNull();
        actual.Thumbprint.Should().Be(anchor.Thumbprint);
        actual.Dispose();
    }

    private static ClientCertificateTrustProfileOptions CreateProfile(
        string profileId,
        string environmentId,
        string issuer,
        string sanUri)
        => new()
        {
            ProfileId = profileId,
            EnvironmentId = environmentId,
            DisplayName = profileId,
            AcceptedIssuerSubjects = [issuer],
            AllowedSanTypes = [ClientCertificateIdentityType.SanUri, ClientCertificateIdentityType.SanEmail],
            RequireClientAuthenticationEku = true,
            ExpirationWarningThresholdDays = 20,
            RotationGracePeriodDays = 7,
            PrincipalMappings =
            [
                new ClientCertificatePrincipalMappingOptions
                {
                    MappingId = $"{environmentId}-admin",
                    MatchType = ClientCertificateIdentityType.SanUri,
                    MatchValue = sanUri,
                    PrincipalId = $"native-{environmentId}-admin",
                    DisplayName = $"Native {environmentId} admin",
                    Roles = ["admin", "operator"],
                    TenantId = $"tenant-{environmentId}",
                    EnvironmentScopes = [environmentId]
                }
            ]
        };

    private static ClientCertificateValidator CreateValidator(ClientCertificateAuthenticationOptions options)
    {
        var monitor = new TestOptionsMonitor<ClientCertificateAuthenticationOptions>(options);
        var store = new InMemoryClientCertificateTrustStore(Options.Create(options));
        return new ClientCertificateValidator(monitor, store);
    }

    private static X509Certificate2 CreateCertificate(
        string subject,
        string? uri,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        bool includeClientAuthenticationEku = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subject,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        if (includeClientAuthenticationEku)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
                critical: false));
        }

        if (!string.IsNullOrWhiteSpace(uri))
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddUri(new Uri(uri));
            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(90));
    }

    private static X509Certificate2 CreateCertificateWithMalformedSan(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subject,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
            critical: false));

        // Deliberately broken SAN extension: declares a 1-byte SEQUENCE but provides garbage.
        // AsnReader will throw AsnContentException; the validator must absorb it.
        request.CertificateExtensions.Add(new X509Extension(
            new Oid("2.5.29.17"),
            new byte[] { 0x30, 0x01, 0xFF },
            critical: false));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(90));
    }

    private static string ComputeFingerprint(X509Certificate2 certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static string ExportPem(X509Certificate2 certificate)
        => new string(PemEncoding.Write("CERTIFICATE", certificate.RawData));

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

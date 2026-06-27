// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Azure;
using Azure.Security.KeyVault.Secrets;
using FluentAssertions;
using Honua.Licensing;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Licensing;

/// <summary>
/// In-process tests for the Azure Key Vault license-content resolver. They exercise reference
/// recognition, the fail-safe contract (never throw — degrade to Community), and end-to-end Pro
/// activation through <see cref="FileBackedLicenseService"/> using a mocked <see cref="SecretClient"/>
/// so no live Key Vault or Azure credentials are required.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class AzureKeyVaultLicenseContentResolverTests
{
    private const string VaultUri = "https://myvault.vault.azure.net";
    private const string SecretName = "license-pro";
    private const string ValidRef = "azure:keyvault:https://myvault.vault.azure.net/license-pro";

    private static AzureKeyVaultLicenseContentResolver CreateResolver(Func<Uri, SecretClient>? clientFactory)
        => new(NullLogger<AzureKeyVaultLicenseContentResolver>.Instance, clientFactory);

    [UnitTest]
    public void CanResolve_ClassifiesReferencesByPrefixAndShape()
    {
        var resolver = CreateResolver(_ => throw new InvalidOperationException("CanResolve must not touch the network."));

        var cases = new (string? Reference, bool Expected)[]
        {
            (ValidRef, true),
            ("AZURE:KEYVAULT:https://myvault.vault.azure.net/license-pro", true),
            ("azure:keyvault:https://myvault.vault.azure.net/secrets/license-pro", true),
            ("aws:secretsmanager:arn:aws:secretsmanager:us-east-1:111122223333:secret:license", false),
            ("azure:keyvault:", false),
            ("azure:keyvault:license-pro", false),
            ("azure:keyvault:https://myvault.vault.azure.net/", false),
            ("azure:keyvault:http://myvault.vault.azure.net/license-pro", false),
            (null, false),
            (string.Empty, false),
            ("   ", false),
        };

        foreach (var (reference, expected) in cases)
        {
            resolver.CanResolve(reference).Should().Be(
                expected,
                "reference '{0}' should classify as {1}",
                reference ?? "<null>",
                expected);
        }
    }

    [UnitTest]
    public async Task ResolveLicenseContentAsync_UnsupportedReference_ReturnsNullWithoutTouchingNetwork()
    {
        var resolver = CreateResolver(_ => throw new InvalidOperationException("must not build a client for an unsupported ref."));

        var content = await resolver.ResolveLicenseContentAsync(
            "aws:secretsmanager:honua/license-pro",
            CancellationToken.None);

        content.Should().BeNull();
    }

    [UnitTest]
    public async Task ResolveLicenseContentAsync_SecretClientThrows_FailsSafeToNull()
    {
        // A missing RBAC grant / deleted secret / unreachable vault must degrade to Community,
        // never crash the host: the resolver swallows the failure and returns null.
        var resolver = CreateResolver(_ => throw new RequestFailedException(403, "Forbidden"));

        var content = await resolver.ResolveLicenseContentAsync(ValidRef, CancellationToken.None);

        content.Should().BeNull();
    }

    [UnitTest]
    public async Task ResolveLicenseContentAsync_EmptySecretValue_ReturnsNull()
    {
        var resolver = CreateResolver(_ => CreateSecretClient(string.Empty));

        var content = await resolver.ResolveLicenseContentAsync(ValidRef, CancellationToken.None);

        content.Should().BeNull();
    }

    [UnitTest]
    public async Task ResolveLicenseContentAsync_ResolvesSecretValue_FromMatchingSecretName()
    {
        const string envelope = "{\"version\":1}";
        var resolver = CreateResolver(_ => CreateSecretClient(envelope));

        var content = await resolver.ResolveLicenseContentAsync(ValidRef, CancellationToken.None);

        content.Should().Be(envelope);
    }

    [UnitTest]
    public async Task StartAsync_KeyVaultResolvedSignedLicense_ActivatesPro()
    {
        // End-to-end parity with the AWS Secrets Manager path: a signed Pro envelope fetched from
        // Key Vault must activate Pro through the cloud-neutral license service.
        const string relabeledKeyId = "honuademo2026q2";
        var license = LicenseTestSupport.CreateSignedLicense(
            HonuaEdition.Pro,
            expiresAt: DateTimeOffset.UtcNow.AddDays(365),
            entitlements: ["editing.featureserver-edits", "analytics.clustering"],
            keyId: relabeledKeyId);
        var envelopeJson = Encoding.UTF8.GetString(license.LicenseData);

        var resolver = CreateResolver(_ => CreateSecretClient(envelopeJson));
        var service = new FileBackedLicenseService(
            Options.Create(new LicenseOptions
            {
                LicenseContentSecretRef = ValidRef,
                TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [relabeledKeyId] = license.PublicKeySetting
                }
            }),
            new BouncyCastleEd25519Verifier(),
            NullLogger<FileBackedLicenseService>.Instance,
            [resolver]);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.ValidationState.Should().Be(LicenseValidationState.Valid);
        snapshot.Edition.Should().Be(HonuaEdition.Pro);
        snapshot.KeyId.Should().Be(relabeledKeyId);
        snapshot.HasEntitlement("editing.featureserver-edits").Should().BeTrue();
        service.CheckEntitlement("editing.featureserver-edits").IsActive.Should().BeTrue();
    }

    [UnitTest]
    public async Task StartAsync_KeyVaultFetchFails_FallsBackToCommunity()
    {
        var resolver = CreateResolver(_ => throw new RequestFailedException(404, "SecretNotFound"));
        var service = new FileBackedLicenseService(
            Options.Create(new LicenseOptions { LicenseContentSecretRef = ValidRef }),
            new BouncyCastleEd25519Verifier(),
            NullLogger<FileBackedLicenseService>.Instance,
            [resolver]);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.Edition.Should().Be(HonuaEdition.Community);
        snapshot.IsValid.Should().BeTrue();
        snapshot.ValidationState.Should().Be(LicenseValidationState.NoLicenseConfigured);
    }

    private static SecretClient CreateSecretClient(string secretValue)
    {
        var secret = new KeyVaultSecret(SecretName, secretValue);
        var response = Response.FromValue(secret, Mock.Of<Response>());

        var client = new Mock<SecretClient>();
        client
            .Setup(c => c.GetSecretAsync(SecretName, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return client.Object;
    }
}

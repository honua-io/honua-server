// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Licensing;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Licensing;

/// <summary>
/// Opt-in live test for the Azure Key Vault license-content resolver. It is skipped unless the
/// operator points it at a real Key Vault secret holding a signed Pro license envelope and supplies
/// the matching trusted public key, so CI never depends on cloud credentials.
///
/// To run against a Pro license stored in Key Vault:
///   HONUA_LIVE_LICENSE_KEYVAULT_REF=azure:keyvault:https://&lt;vault&gt;.vault.azure.net/&lt;secret&gt;
///   HONUA_LIVE_LICENSE_KEY_ID=honuademo2026q2
///   HONUA_LIVE_LICENSE_PUBLIC_KEY=base64url:Y2XgDBncW5w6n7L3YG-T6HxX51DGybWazt0_gubk30k
///   plus an Azure credential (managed identity / AZURE_* env / az login) with
///   "Key Vault Secrets User" on the secret.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class AzureKeyVaultLicenseContentResolverLiveTests
{
    private const string KeyVaultRefEnv = "HONUA_LIVE_LICENSE_KEYVAULT_REF";
    private const string KeyIdEnv = "HONUA_LIVE_LICENSE_KEY_ID";
    private const string PublicKeyEnv = "HONUA_LIVE_LICENSE_PUBLIC_KEY";

    [EmulatorTest(KeyVaultRefEnv, KeyIdEnv, PublicKeyEnv)]
    [Trait("Category", "Live")]
    public async Task ResolveAndValidate_LiveSecret_ActivatesPro()
    {
        var secretRef = Environment.GetEnvironmentVariable(KeyVaultRefEnv)!;
        var keyId = Environment.GetEnvironmentVariable(KeyIdEnv)!;
        var publicKey = Environment.GetEnvironmentVariable(PublicKeyEnv)!;

        var resolver = new AzureKeyVaultLicenseContentResolver(
            NullLogger<AzureKeyVaultLicenseContentResolver>.Instance);

        resolver.CanResolve(secretRef).Should().BeTrue();

        // Feed the resolver into the real license service exactly as production does, so the test
        // proves end-to-end that a Key-Vault-sourced envelope activates Pro.
        var service = new FileBackedLicenseService(
            Options.Create(new LicenseOptions
            {
                LicenseContentSecretRef = secretRef,
                TrustedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [keyId] = publicKey
                }
            }),
            new BouncyCastleEd25519Verifier(),
            NullLogger<FileBackedLicenseService>.Instance,
            [resolver]);

        await service.StartAsync(CancellationToken.None);

        var snapshot = service.GetSnapshot();
        snapshot.ValidationState.Should().Be(LicenseValidationState.Valid);
        snapshot.Edition.Should().Be(HonuaEdition.Pro);
        snapshot.KeyId.Should().Be(keyId);
        snapshot.HasEntitlement("editing.featureserver-edits").Should().BeTrue();
    }
}

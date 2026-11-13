// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Configuration;

using Honua.Infrastructure.Authentication.ClientCertificates;
using Microsoft.Extensions.Options;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresConfiguredTrustStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task DisableProfileAsync_BeforeFirstRead_PreservesDisableAcrossBootstrapRestarts()
    {
        await using var harness = await CreateHarnessAsync();
        var options = Options.Create(new ClientCertificateAuthenticationOptions
        {
            TrustProfiles = [new() { ProfileId = "bootstrap", EnvironmentId = "production", DisplayName = "Bootstrap" }]
        });
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), options);
        (await taskA.DisableProfileAsync("BOOTSTRAP"))!.Enabled.Should().BeFalse();
        var taskB = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), options);
        (await taskB.GetProfileAsync("bootstrap"))!.Enabled.Should().BeFalse();
    }

    [IntegrationTest]
    public async Task ConfiguredTrustProfileChange_OnRestartedTask_AppliesToEveryTaskAndKeepsAdminRevocations()
    {
        await using var harness = await CreateHarnessAsync();
        var original = Options.Create(new ClientCertificateAuthenticationOptions
        {
            TrustProfiles = [new() { ProfileId = "partner", EnvironmentId = "production", DisplayName = "Partner", AcceptedIssuerThumbprints = ["AA", "BB"] }]
        });
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), original);
        var adminRevocation = new ClientCertificateRevocationEntry
        {
            RevocationId = "admin-revocation",
            Reason = "Lost",
            Actor = "operator",
            FingerprintSha256 = "abc"
        };
        await taskA.AddRevocationAsync("partner", adminRevocation);
        var changed = Options.Create(new ClientCertificateAuthenticationOptions
        {
            TrustProfiles = [new() { ProfileId = "partner", EnvironmentId = "production", DisplayName = "Partner", AcceptedIssuerThumbprints = ["AA"], Enabled = false }]
        });

        var restarted = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), changed);
        await restarted.ListProfilesAsync();

        var seenByTaskA = (await taskA.GetProfileAsync("partner"))!;
        seenByTaskA.Enabled.Should().BeFalse();
        seenByTaskA.AcceptedIssuerThumbprints.Should().Equal("AA");
        seenByTaskA.Revocations.Should().ContainSingle().Which.Should().Be(adminRevocation);
        var generation = await harness.CreateConfigurationDocuments().GetGenerationAsync(CancellationToken.None);
        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), changed).ListProfilesAsync();
        (await harness.CreateConfigurationDocuments().GetGenerationAsync(CancellationToken.None)).Should().Be(generation);
    }

    [IntegrationTest]
    public async Task ConfiguredMappingDisable_OnRestartedTask_DisablesMappingOnEveryTask()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), TrustOptions(mappingEnabled: true));
        (await taskA.GetProfileAsync("partner"))!.PrincipalMappings.Should().ContainSingle().Which.Enabled.Should().BeTrue();

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), TrustOptions(mappingEnabled: false)).ListProfilesAsync();

        var mapping = (await taskA.GetProfileAsync("partner"))!.PrincipalMappings.Should().ContainSingle().Subject;
        mapping.Enabled.Should().BeFalse();
        mapping.Roles.Should().Equal("reader");
    }

    [IntegrationTest]
    public async Task ConfiguredProfile_OnRestartedTask_KeepsStricterAdminChainSettings()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), TrustOptions(mappingEnabled: true));
        var profile = (await taskA.GetProfileAsync("partner"))!;
        await taskA.UpsertProfileAsync(profile with
        {
            RequireChainTrust = true,
            ChainRevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online
        });

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), TrustOptions(mappingEnabled: true)).ListProfilesAsync();

        var merged = (await taskA.GetProfileAsync("partner"))!;
        merged.RequireChainTrust.Should().BeTrue();
        merged.ChainRevocationMode.Should().Be(System.Security.Cryptography.X509Certificates.X509RevocationMode.Online);
    }

    [IntegrationTest]
    public async Task ConfiguredProfile_WithUnreadableStoredCopy_IsNotTrustedAndDoesNotBlockOtherProfiles()
    {
        await using var harness = await CreateHarnessAsync();
        var foreignKeyRing = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(foreignKeyRing), TrustOptions(mappingEnabled: true))
            .ListProfilesAsync();
        var store = harness.CreateTrustStore();
        await store.UpsertProfileAsync(new ClientCertificateTrustProfile { ProfileId = "other", EnvironmentId = "production", DisplayName = "Other" });

        var restarted = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), TrustOptions(mappingEnabled: true));

        (await restarted.ListProfilesAsync()).Select(profile => profile.ProfileId).Should().Equal("other");
    }

    [IntegrationTest]
    public async Task ConfiguredMappingRemoved_OnRestartedTask_DisablesItButKeepsAdminMappings()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), TrustOptions(mappingEnabled: true));
        await taskA.UpsertMappingAsync("partner", new ClientCertificatePrincipalMapping
        {
            MappingId = "admin-added",
            MatchType = ClientCertificateIdentityType.SanUri,
            MatchValue = "spiffe://honua/production/ops",
            PrincipalId = "ops",
            DisplayName = "Ops"
        });
        var withoutMapping = TrustOptions(mappingEnabled: true);
        withoutMapping.Value.TrustProfiles[0].PrincipalMappings = [];

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), withoutMapping).ListProfilesAsync();

        var mappings = (await taskA.GetProfileAsync("partner"))!.PrincipalMappings;
        mappings.Single(mapping => mapping.MappingId == "partner-reader").Enabled.Should().BeFalse();
        mappings.Single(mapping => mapping.MappingId == "admin-added").Enabled.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task ConfiguredProfileRemoved_OnRestartedTask_DisablesItButKeepsAdminProfiles()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), TrustOptions(mappingEnabled: true));
        await taskA.UpsertProfileAsync(new ClientCertificateTrustProfile { ProfileId = "admin-profile", EnvironmentId = "production", DisplayName = "Admin" });

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), Options.Create(new ClientCertificateAuthenticationOptions()))
            .ListProfilesAsync();

        (await taskA.GetProfileAsync("partner"))!.Enabled.Should().BeFalse();
        (await taskA.GetProfileAsync("admin-profile"))!.Enabled.Should().BeTrue();
    }

    private static IOptions<ClientCertificateAuthenticationOptions> TrustOptions(bool mappingEnabled) => Options.Create(new ClientCertificateAuthenticationOptions
    {
        TrustProfiles =
        [
            new()
            {
                ProfileId = "partner", EnvironmentId = "production", DisplayName = "Partner", AcceptedIssuerThumbprints = ["AA"],
                PrincipalMappings =
                [
                    new()
                    {
                        MappingId = "partner-reader", MatchType = ClientCertificateIdentityType.SanUri, MatchValue = "spiffe://honua/production/partner",
                        PrincipalId = "partner", DisplayName = "Partner", Roles = ["reader"], Enabled = mappingEnabled
                    }
                ]
            }
        ]
    });

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfiguredTrustStoreTests));
}

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
            TrustProfilesRevision = 2,
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

        var changed = TrustOptions(mappingEnabled: false);
        changed.Value.TrustProfilesRevision = 2;
        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), changed).ListProfilesAsync();

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

        var changed = TrustOptions(mappingEnabled: false);
        changed.Value.TrustProfilesRevision = 2;
        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), changed).ListProfilesAsync();

        var merged = (await taskA.GetProfileAsync("partner"))!;
        merged.PrincipalMappings.Single().Enabled.Should().BeFalse();
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
        withoutMapping.Value.TrustProfilesRevision = 2;

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

        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), Options.Create(new ClientCertificateAuthenticationOptions { TrustProfilesRevision = 2 }))
            .ListProfilesAsync();

        (await taskA.GetProfileAsync("partner"))!.Enabled.Should().BeFalse();
        (await taskA.GetProfileAsync("admin-profile"))!.Enabled.Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bootstrap_UnreadableProfileRecovers_AppliesPendingRemovalAtSameConfigurationRevision(bool removeProfile)
    {
        await using var harness = await CreateHarnessAsync();
        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), TrustOptions(mappingEnabled: true)).ListProfilesAsync();
        var table = harness.Table("configuration_documents");
        var payload = await harness.ScalarAsync<byte[]>($"SELECT payload FROM {table} WHERE kind = 'client-certificate-trust' AND key = 'PARTNER'");
        await harness.ExecuteAsync($"UPDATE {table} SET payload = @payload WHERE kind = 'client-certificate-trust' AND key = 'PARTNER'",
            new Npgsql.NpgsqlParameter("payload", new byte[] { 0 }));
        var changed = TrustOptions(mappingEnabled: true);
        changed.Value.TrustProfilesRevision = 2;
        if (removeProfile) { changed.Value.TrustProfiles = []; }
        else { changed.Value.TrustProfiles[0].PrincipalMappings = []; }

        var whileUnreadable = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), changed);
        (await whileUnreadable.GetProfileAsync("partner")).Should().BeNull();
        var manifests = new ConfigurationCollection<ConfiguredTrustManifest>("client-certificate-trust-configured", SharedConfigurationJsonContext.Default.ConfiguredTrustManifest);
        (await harness.CreateConfigurationDocuments().ReadAsync(manifests, CancellationToken.None)).Documents.Single().Value.Revision.Should().Be(1);
        await harness.ExecuteAsync($"UPDATE {table} SET payload = @payload WHERE kind = 'client-certificate-trust' AND key = 'PARTNER'",
            new Npgsql.NpgsqlParameter("payload", payload!));

        var profile = (await whileUnreadable.GetProfileAsync("partner"))!;
        if (removeProfile) { profile.Enabled.Should().BeFalse(); }
        else { profile.PrincipalMappings.Single().Enabled.Should().BeFalse(); }
        (await harness.CreateConfigurationDocuments().ReadAsync(manifests, CancellationToken.None)).Documents.Single().Value.Revision.Should().Be(2);
    }

    [IntegrationTest]
    public async Task Bootstrap_OlderDeploymentStartsAfterNewerDeployment_KeepsNewProfilesEnabled()
    {
        await using var harness = await CreateHarnessAsync();
        var newer = TrustOptions(mappingEnabled: true);
        newer.Value.TrustProfilesRevision = 2;
        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), newer).ListProfilesAsync();

        var older = Options.Create(new ClientCertificateAuthenticationOptions { TrustProfilesRevision = 1 });
        var oldTask = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), older);

        (await oldTask.GetProfileAsync("partner"))!.Enabled.Should().BeTrue();
        (await oldTask.GetProfileAsync("partner"))!.PrincipalMappings.Single().Enabled.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Bootstrap_ConfigurationChangesWithoutRevisionBump_KeepsCommittedConfiguration()
    {
        await using var harness = await CreateHarnessAsync();
        await new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), TrustOptions(mappingEnabled: true)).ListProfilesAsync();
        var changedWithoutBump = Options.Create(new ClientCertificateAuthenticationOptions());

        var restarted = new PostgresClientCertificateTrustStore(harness.CreateConfigurationDocuments(), changedWithoutBump);

        (await restarted.GetProfileAsync("partner"))!.Enabled.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task WriteBatchAsync_CompanionConflict_RollsBackProfileAndGeneration()
    {
        await using var harness = await CreateHarnessAsync();
        var documents = harness.CreateConfigurationDocuments();
        var profiles = new ConfigurationCollection<ClientCertificateTrustProfile>("client-certificate-trust", SharedConfigurationJsonContext.Default.ClientCertificateTrustProfile);
        var manifests = new ConfigurationCollection<ConfiguredTrustManifest>("client-certificate-trust-configured", SharedConfigurationJsonContext.Default.ConfiguredTrustManifest);
        var generation = await documents.GetGenerationAsync(CancellationToken.None);
        ConfigurationChange[] changes =
        [
            documents.PrepareWrite(profiles, new("PARTNER", 0, new() { ProfileId = "partner", EnvironmentId = "production", DisplayName = "Partner" })),
            documents.PrepareWrite(manifests, new("CONFIGURED", 99, new()))
        ];

        (await documents.WriteBatchAsync(generation, changes, CancellationToken.None)).Should().BeFalse();

        (await documents.ReadAsync(profiles, CancellationToken.None)).Documents.Should().BeEmpty();
        (await documents.ReadAsync(manifests, CancellationToken.None)).Documents.Should().BeEmpty();
        (await documents.GetGenerationAsync(CancellationToken.None)).Should().Be(generation);
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

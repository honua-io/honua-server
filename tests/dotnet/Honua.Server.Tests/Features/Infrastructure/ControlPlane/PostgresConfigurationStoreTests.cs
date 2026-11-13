// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Server.Tests.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Core.Features.Identity.Domain;

using Honua.Core.Features.RateLimiting.Domain;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.Infrastructure.Authentication;
using Npgsql;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresConfigurationStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task UpdateProviderAsync_OnTaskA_DisablesProviderOnTaskB()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = CreateOidcStore(harness);
        var taskB = CreateOidcStore(harness);
        var provider = Provider(enabled: true);
        await taskA.CreateProviderAsync(provider);
        (await taskB.GetProviderAsync(provider.ProviderId))!.Enabled.Should().BeTrue();

        await taskA.UpdateProviderAsync(provider.ProviderId, _ => Provider(enabled: false, provider.ProviderId));

        (await taskB.GetProviderAsync(provider.ProviderId))!.Enabled.Should().BeFalse();
        (await CreateOidcStore(harness).GetProviderAsync(provider.ProviderId))!.ClientSecret.Should().Be("private-client-secret");
    }

    [IntegrationTest]
    public async Task AddRevocationAsync_OnTaskA_IsVisibleOnTaskBAndSurvivesProfileUpdate()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = CreateTrustStore(harness);
        var taskB = CreateTrustStore(harness);
        var profile = new ClientCertificateTrustProfile
        {
            ProfileId = "production",
            EnvironmentId = "production",
            DisplayName = "Production"
        };
        await taskA.UpsertProfileAsync(profile);
        (await taskB.GetProfileAsync("PRODUCTION"))!.Revocations.Should().BeEmpty();
        var revocation = new ClientCertificateRevocationEntry
        {
            RevocationId = "compromised-key",
            Reason = "Rotated",
            Actor = "operator",
            FingerprintSha256 = "abc"
        };

        await taskA.AddRevocationAsync(profile.ProfileId, revocation);
        await taskB.UpsertProfileAsync(profile with { DisplayName = "Renamed" });

        (await taskB.GetProfileAsync(profile.ProfileId))!.Revocations.Should().ContainSingle().Which.Should().Be(revocation);
        (await CreateTrustStore(harness).GetProfileAsync(profile.ProfileId))!.Revision.Should().Be(3);
    }

    [IntegrationTest]
    public async Task AddRevocationAsync_OnTaskA_RejectsCertificateOnTaskB()
    {
        await using var harness = await CreateHarnessAsync();
        using var certificate = ClientCertificateValidationTests.CreateCertificate("CN=Shared client", "spiffe://honua/production/admin");
        var taskA = CreateTrustStore(harness);
        await taskA.UpsertProfileAsync(ClientCertificateTrustProfile.FromOptions(new ClientCertificateTrustProfileOptions
        {
            ProfileId = "production",
            EnvironmentId = "production",
            DisplayName = "Production",
            AcceptedIssuerSubjects = [certificate.Issuer],
            AllowedSanTypes = [ClientCertificateIdentityType.SanUri],
            PrincipalMappings = [new()
            {
                MappingId = "admin", MatchType = ClientCertificateIdentityType.SanUri,
                MatchValue = "spiffe://honua/production/admin", PrincipalId = "admin", DisplayName = "Administrator",
                EnvironmentScopes = ["production"], Roles = ["admin"]
            }]
        }));
        var monitor = new ClientCertificateValidationTests.TestOptionsMonitor<ClientCertificateAuthenticationOptions>(new()
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "production"
        });
        var taskB = new ClientCertificateValidator(monitor, CreateTrustStore(harness));
        (await taskB.ValidateAsync(certificate)).Succeeded.Should().BeTrue();

        await taskA.AddRevocationAsync("production", new()
        {
            RevocationId = "rotated",
            Actor = "operator",
            Reason = "Rotated",
            FingerprintSha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData))
        });
        var result = await taskB.ValidateAsync(certificate);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(ClientCertificateValidationErrorCode.CertificateRevoked);
    }

    [IntegrationTest]
    public async Task UpdatePolicyAsync_OnTaskA_ChangesResolutionOnTaskB()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = CreateRateStore(harness);
        var taskB = CreateRateStore(harness);
        var policy = Policy(100);
        await taskA.CreatePolicyAsync(policy);
        var request = new RateLimitRequestDescriptor { TenantId = "tenant-a" };
        RateLimitPolicyResolver.Resolve(await taskB.ListPoliciesAsync(), request)!.RequestsPerWindow.Should().Be(100);

        await taskA.UpdatePolicyAsync(policy.PolicyId, _ => Policy(1, policy.PolicyId));

        RateLimitPolicyResolver.Resolve(await taskB.ListPoliciesAsync(), request)!.RequestsPerWindow.Should().Be(1);
        (await CreateRateStore(harness).GetPolicyAsync(policy.PolicyId))!.RequestsPerWindow.Should().Be(1);
    }

    [IntegrationTest]
    public async Task RefreshAsync_OnTaskB_AppliesTaskAIdentityChangesBeforeAuthentication()
    {
        await using var harness = await CreateHarnessAsync();
        var registry = new OidcRuntimeProviderRegistry();
        var refresh = new ConfigurationRuntimeRefresh(harness.CreateConfigurationDocuments(), registry);
        var provider = Provider(true);
        await harness.CreateOidcStore().CreateProviderAsync(provider);
        await refresh.RefreshAsync(CancellationToken.None);
        registry.Current.Should().ContainSingle().Which.Enabled.Should().BeTrue();

        await harness.CreateOidcStore().UpdateProviderAsync(provider.ProviderId, _ => Provider(false, provider.ProviderId));
        await refresh.RefreshAsync(CancellationToken.None);

        registry.Current.Should().ContainSingle().Which.Enabled.Should().BeFalse();
    }

    [IntegrationTest]
    public async Task ConcurrentRevocations_FromDifferentTasks_PreserveEveryMutation()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.CreateTrustStore().UpsertProfileAsync(new ClientCertificateTrustProfile
        {
            ProfileId = "concurrent",
            EnvironmentId = "production",
            DisplayName = "Concurrent"
        });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => harness.CreateTrustStore().AddRevocationAsync("concurrent", new()
        {
            RevocationId = $"key-{index}",
            Reason = "Rotated",
            Actor = "operator",
            FingerprintSha256 = $"fingerprint-{index}"
        })));
        (await harness.CreateTrustStore().GetProfileAsync("concurrent"))!.Revocations.Should().HaveCount(8);
    }

    [IntegrationTest]
    public async Task UpdateProviderAsync_ConcurrentPartialUpdates_PreserveBothChanges()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = CreateOidcStore(harness);
        var taskB = CreateOidcStore(harness);
        var provider = await taskA.CreateProviderAsync(Provider(true));

        await RunCompetingUpdatesAsync<OidcProviderConfiguration>(
            update => taskA.UpdateProviderAsync(provider.ProviderId, update),
            update => taskB.UpdateProviderAsync(provider.ProviderId, update),
            (current => Provider(current.Enabled, current.ProviderId, "Renamed"),
                current => Provider(false, current.ProviderId, current.Name)));

        var result = await taskB.GetProviderAsync(provider.ProviderId);
        result!.Name.Should().Be("Renamed");
        result.Enabled.Should().BeFalse();
    }

    [IntegrationTest]
    public async Task UpdatePolicyAsync_ConcurrentPartialUpdates_PreserveBothChanges()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = CreateRateStore(harness);
        var taskB = CreateRateStore(harness);
        var policy = await taskA.CreatePolicyAsync(Policy(100));

        await RunCompetingUpdatesAsync<RateLimitPolicy>(
            update => taskA.UpdatePolicyAsync(policy.PolicyId, update),
            update => taskB.UpdatePolicyAsync(policy.PolicyId, update),
            (current => Policy(current.RequestsPerWindow, current.PolicyId, "Renamed"),
                current => Policy(1, current.PolicyId, current.Name)));

        var result = await taskB.GetPolicyAsync(policy.PolicyId);
        result!.Name.Should().Be("Renamed");
        result.RequestsPerWindow.Should().Be(1);
    }

    private static async Task RunCompetingUpdatesAsync<T>(Func<Func<T, T>, Task<T?>> updateA,
        Func<Func<T, T>, Task<T?>> updateB, (Func<T, T> First, Func<T, T> Second) mutations) where T : class
    {
        using var firstReads = new Barrier(2);
        var readsA = 0;
        var readsB = 0;
        await Task.WhenAll(Task.Run(() => updateA(current =>
        {
            if (Interlocked.Increment(ref readsA) == 1)
            {
                firstReads.SignalAndWait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            }
            return mutations.First(current);
        })), Task.Run(() => updateB(current =>
        {
            if (Interlocked.Increment(ref readsB) == 1)
            {
                firstReads.SignalAndWait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            }
            return mutations.Second(current);
        })));
        Math.Max(readsA, readsB).Should().BeGreaterThan(1, "one task must reapply its update after the forced conflict");
    }

    [IntegrationTest]
    public async Task ConfigurationWrites_AdvanceOneGeneration_AndProtectClientSecrets()
    {
        await using var harness = await CreateHarnessAsync();
        var documents = harness.CreateConfigurationDocuments();
        var initial = await documents.GetGenerationAsync(CancellationToken.None);
        var provider = Provider(true);
        await harness.CreateOidcStore().CreateProviderAsync(provider);
        (await documents.GetGenerationAsync(CancellationToken.None)).Should().Be(initial + 1);
        await harness.CreateRateStore().CreatePolicyAsync(Policy(10));
        (await documents.GetGenerationAsync(CancellationToken.None)).Should().Be(initial + 2);
        var payload = await harness.ScalarAsync<byte[]>($"SELECT payload FROM {harness.Table("configuration_documents")} WHERE kind = 'oidc-provider'");
        System.Text.Encoding.UTF8.GetString(payload!).Should().NotContain(provider.ClientSecret!);
    }

    [IntegrationTest]
    public async Task RolledBackMutation_DoesNotAdvanceGenerationOrPersistPolicy()
    {
        await using var harness = await CreateHarnessAsync();
        var initial = await harness.CreateConfigurationDocuments().GetGenerationAsync(CancellationToken.None);
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand($"INSERT INTO {harness.Table("configuration_documents")} (kind, key, payload) VALUES ('test', 'rollback', @payload)", connection, transaction))
        {
            command.Parameters.AddWithValue("payload", new byte[] { 1, 2, 3 });
            await command.ExecuteNonQueryAsync();
        }
        await transaction.RollbackAsync();
        (await harness.CreateConfigurationDocuments().GetGenerationAsync(CancellationToken.None)).Should().Be(initial);
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("configuration_documents")}")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task ReadAsync_WithDocumentFromAnotherKeyRing_SkipsItInsteadOfFailing()
    {
        await using var harness = await CreateHarnessAsync();
        var readable = Policy(10);
        await CreateRateStore(harness).CreatePolicyAsync(readable);
        var foreign = new PostgresRateLimitPolicyStore(harness.CreateConfigurationDocuments(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        await foreign.CreatePolicyAsync(Policy(20, name: "Foreign"));

        var policies = await CreateRateStore(harness).ListPoliciesAsync();

        policies.Should().ContainSingle().Which.PolicyId.Should().Be(readable.PolicyId);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationStoreTests));

    private static PostgresOidcProviderStore CreateOidcStore(ControlPlaneStateHarness harness) => harness.CreateOidcStore();
    private static PostgresRateLimitPolicyStore CreateRateStore(ControlPlaneStateHarness harness) => harness.CreateRateStore();
    private static PostgresClientCertificateTrustStore CreateTrustStore(ControlPlaneStateHarness harness)
        => harness.CreateTrustStore();

    private static OidcProviderConfiguration Provider(bool enabled, Guid? id = null, string name = "Example") => new()
    {
        ProviderId = id ?? Guid.NewGuid(),
        Name = name,
        ProviderType = "Generic",
        Authority = "https://identity.example.com",
        ClientId = "example",
        ClientSecret = "private-client-secret",
        Enabled = enabled
    };

    private static RateLimitPolicy Policy(int limit, Guid? id = null, string name = "Tenant") => new()
    {
        PolicyId = id ?? Guid.NewGuid(),
        Name = name,
        Scope = RateLimitScopes.Tenant,
        Key = "tenant-a",
        RequestsPerWindow = limit,
        WindowDuration = TimeSpan.FromMinutes(1)
    };
}

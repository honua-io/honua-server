// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

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

        await taskA.UpdateProviderAsync(Provider(enabled: false, provider.ProviderId));

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
            ProfileId = "production", EnvironmentId = "production", DisplayName = "Production"
        };
        await taskA.UpsertProfileAsync(profile);
        (await taskB.GetProfileAsync("PRODUCTION"))!.Revocations.Should().BeEmpty();
        var revocation = new ClientCertificateRevocationEntry
        {
            RevocationId = "compromised-key", Reason = "Rotated", Actor = "operator", FingerprintSha256 = "abc"
        };

        await taskA.AddRevocationAsync(profile.ProfileId, revocation);
        await taskB.UpsertProfileAsync(profile with { DisplayName = "Renamed" });

        (await taskB.GetProfileAsync(profile.ProfileId))!.Revocations.Should().ContainSingle().Which.Should().Be(revocation);
        (await CreateTrustStore(harness).GetProfileAsync(profile.ProfileId))!.Revision.Should().Be(3);
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

        await taskA.UpdatePolicyAsync(Policy(1, policy.PolicyId));

        RateLimitPolicyResolver.Resolve(await taskB.ListPoliciesAsync(), request)!.RequestsPerWindow.Should().Be(1);
        (await CreateRateStore(harness).GetPolicyAsync(policy.PolicyId))!.RequestsPerWindow.Should().Be(1);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationStoreTests));

    private static IOidcProviderStore CreateOidcStore(ControlPlaneStateHarness harness) => new InMemoryOidcProviderStore();
    private static IRateLimitPolicyStore CreateRateStore(ControlPlaneStateHarness harness) => new InMemoryRateLimitPolicyStore();
    private static IClientCertificateTrustStore CreateTrustStore(ControlPlaneStateHarness harness)
        => new InMemoryClientCertificateTrustStore(Options.Create(new ClientCertificateAuthenticationOptions()));

    private static OidcProviderConfiguration Provider(bool enabled, Guid? id = null) => new()
    {
        ProviderId = id ?? Guid.NewGuid(), Name = "Example", ProviderType = "Generic",
        Authority = "https://identity.example.com", ClientId = "example", ClientSecret = "private-client-secret", Enabled = enabled
    };

    private static RateLimitPolicy Policy(int limit, Guid? id = null) => new()
    {
        PolicyId = id ?? Guid.NewGuid(), Name = "Tenant", Scope = RateLimitScopes.Tenant,
        Key = "tenant-a", RequestsPerWindow = limit, WindowDuration = TimeSpan.FromMinutes(1)
    };
}

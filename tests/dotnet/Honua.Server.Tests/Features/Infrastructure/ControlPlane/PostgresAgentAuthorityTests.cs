// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresAgentAuthorityTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task SetAsync_SecondNodeAndRestart_ObserveLoweredCeilingAndSharedGeneration()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresAgentAuthorityTests));
        var first = new PostgresAgentAuthorityStore(harness.CreateConfigurationDocuments());
        var second = new PostgresAgentAuthorityStore(harness.CreateConfigurationDocuments());
        var policy = Ceiling();
        var generation = await harness.CreateConfigurationDocuments().GetGenerationAsync(default);
        await first.SetAsync(policy, default);
        (await second.GetAsync("tenant", "agent", default)).Should().BeEquivalentTo(policy);
        (await harness.CreateConfigurationDocuments().GetGenerationAsync(default)).Should().BeGreaterThan(generation);
        var lowered = policy with { AllowedOperationIds = [] };
        await first.SetAsync(lowered, default);
        (await second.GetAsync("tenant", "agent", default)).Should().BeEquivalentTo(lowered);
        var restarted = new PostgresAgentAuthorityStore(harness.CreateConfigurationDocuments());
        (await restarted.GetAsync("tenant", "agent", default)).Should().BeEquivalentTo(lowered);
        (await second.GetAsync("other-tenant", "agent", default)).Should().BeNull();
        (await second.GetAsync("tenant", "other-agent", default)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task GetAsync_UnreadableOrRemovedPolicy_DoesNotGrantAuthority()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresAgentAuthorityTests));
        var store = new PostgresAgentAuthorityStore(harness.CreateConfigurationDocuments());
        await store.SetAsync(Ceiling(), default);
        var unreadable = new PostgresAgentAuthorityStore(harness.CreateConfigurationDocuments(new EphemeralDataProtectionProvider()));
        (await unreadable.GetAsync("tenant", "agent", default)).Should().BeNull();
        await store.DeleteAsync("tenant", "agent", default);
        (await store.GetAsync("tenant", "agent", default)).Should().BeNull();
    }

    private static AgentAuthorityCeiling Ceiling() => new()
    {
        TenantId = "tenant", PrincipalId = "agent", AllowedOperationIds = ["service.publish"],
        MaximumBlastRadius = OperationBlastRadiusClass.ServiceScope,
        MaximumSideEffect = OperationSideEffectClass.CreatesMetadata
    };
}

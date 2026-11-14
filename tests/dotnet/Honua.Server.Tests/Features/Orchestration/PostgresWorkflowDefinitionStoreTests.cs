// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Orchestration;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresWorkflowDefinitionStoreTests
{
    private static readonly DateTimeOffset FireTime = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [IntegrationTest]
    public async Task GetAsync_AfterWritingHostStops_PreservesDefinitionAndRejectsReplacement()
    {
        var definition = Definition();
        await using (var writer = CreateHost())
        {
            await writer.InitializeAsync();
            var store = writer.GetService<IWorkflowDefinitionStore>();
            (await store.TryCreateAsync(definition)).Should().BeTrue();
            (await store.TryCreateAsync(definition with { Name = "replacement" })).Should().BeFalse();
        }

        await using var reader = CreateHost();
        await reader.InitializeAsync();
        (await reader.GetService<IWorkflowDefinitionStore>().GetAsync(definition.WorkflowId))
            .Should().BeEquivalentTo(definition);
    }

    [IntegrationTest]
    public async Task GetAsync_RasterDefinition_RoundTripsJsonbAndLists()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<IWorkflowDefinitionStore>();
        var definition = Definition() with
        {
            Steps = [new WorkflowStepDefinition { StepId = "raster", Plan = WorkflowRasterTestData.Plan() }]
        };
        await store.SetAsync(definition);

        (await store.GetAsync(definition.WorkflowId)).Should().BeEquivalentTo(definition);
        (await store.ListAsync()).Should().ContainEquivalentOf(definition);
    }

    [IntegrationTest]
    public async Task ListEventTriggeredAsync_DisabledDefinition_IsExcludedUntilEnabled()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<IWorkflowDefinitionStore>();
        var definition = Definition() with
        {
            Trigger = new WorkflowTrigger { Kind = WorkflowTriggerKind.ChangeFeed, Enabled = false }
        };
        await store.SetAsync(definition);
        (await store.ListEventTriggeredAsync()).Should().NotContain(item => item.WorkflowId == definition.WorkflowId);

        var enabled = definition with { Trigger = definition.Trigger with { Enabled = true } };
        await store.SetAsync(enabled);
        (await store.ListEventTriggeredAsync()).Should().Contain(item => item.WorkflowId == definition.WorkflowId);
        (await store.GetAsync(definition.WorkflowId)).Should().BeEquivalentTo(enabled);
    }

    [IntegrationTest]
    public async Task GetScheduleCursorAsync_LateCompletion_PreservesNewerPendingOccurrence()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<IWorkflowDefinitionStore>();
        var id = Guid.NewGuid().ToString("N");
        await store.AdvanceScheduleCursorAsync(id, FireTime);
        await store.SetPendingScheduleCursorAsync(id, FireTime.AddMinutes(2));
        await store.AdvanceScheduleCursorAsync(id, FireTime.AddMinutes(1));

        (await store.GetScheduleCursorAsync(id)).Should().Be(FireTime.AddMinutes(2));
        await store.SetPendingScheduleCursorAsync(id, FireTime);
        (await store.GetScheduleCursorAsync(id)).Should().Be(FireTime.AddMinutes(2));
    }

    [IntegrationTest]
    public async Task TryClaimScheduleFireAsync_CompetingNodes_ClaimsOccurrenceOnceAndAllowsRetry()
    {
        await using var first = CreateHost();
        await using var second = CreateHost();
        await first.InitializeAsync();
        await second.InitializeAsync();
        var owner = first.GetService<IWorkflowDefinitionStore>();
        var peer = second.GetService<IWorkflowDefinitionStore>();
        var id = Guid.NewGuid().ToString("N");
        var retention = TimeSpan.FromHours(24);

        (await owner.TryClaimScheduleFireAsync(id, FireTime, retention)).Should().BeTrue();
        (await peer.TryClaimScheduleFireAsync(id, FireTime.AddSeconds(15), retention)).Should().BeFalse();
        await owner.ReleaseScheduleClaimAsync(id, FireTime);
        (await peer.TryClaimScheduleFireAsync(id, FireTime, retention)).Should().BeTrue();
        await peer.AdvanceScheduleCursorAsync(id, FireTime);
        await peer.ReleaseScheduleClaimAsync(id, FireTime);
        (await owner.TryClaimScheduleFireAsync(id, FireTime, retention)).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task TryClaimScheduleFireAsync_EightConcurrentClaims_ExactlyOneSucceeds()
    {
        await using var first = CreateHost();
        await using var second = CreateHost();
        await first.InitializeAsync();
        await second.InitializeAsync();
        var nodes = new[]
        {
            first.GetService<IWorkflowDefinitionStore>(),
            second.GetService<IWorkflowDefinitionStore>()
        };
        var id = Guid.NewGuid().ToString("N");

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            nodes[index % nodes.Length].TryClaimScheduleFireAsync(id, FireTime, TimeSpan.FromHours(24))));

        claims.Should().ContainSingle(claimed => claimed);
    }

    [IntegrationTest]
    public async Task SetTriggerCursorAsync_CompetingNodes_AdvancesOrdinallyWithoutCompositeKeyCollisions()
    {
        await using var first = CreateHost();
        await using var second = CreateHost();
        await first.InitializeAsync();
        await second.InitializeAsync();
        var owner = first.GetService<IWorkflowDefinitionStore>();
        var peer = second.GetService<IWorkflowDefinitionStore>();
        var prefix = Guid.NewGuid().ToString("N");
        var markers = new[] { "001", "009", "003", "010", "002" };
        await Task.WhenAll(markers.Select(marker => peer.SetTriggerCursorAsync(prefix + ":source", "kind", marker)));
        await owner.SetTriggerCursorAsync(prefix, "source:kind", "different");

        (await owner.GetTriggerCursorAsync(prefix + ":source", "kind")).Should().Be("010");
        (await peer.GetTriggerCursorAsync(prefix, "source:kind")).Should().Be("different");
    }

    [IntegrationTest]
    public async Task SetTriggerCursorAsync_SupplementaryCharacters_PreservesUtf16OrdinalOrder()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<IWorkflowDefinitionStore>();
        var id = Guid.NewGuid().ToString("N");
        var markers = new[] { "\U00010000", "\uE000", "a" };
        await Task.WhenAll(markers.Select(marker => store.SetTriggerCursorAsync(id, "opaque", marker)));

        (await store.GetTriggerCursorAsync(id, "opaque"))
            .Should().Be(markers.Max(StringComparer.Ordinal));
    }

    [IntegrationTest]
    public async Task GetScheduleCursorAsync_AfterWritingHostStops_PreservesPendingOccurrence()
    {
        var id = Guid.NewGuid().ToString("N");
        await using (var writer = CreateHost())
        {
            await writer.InitializeAsync();
            var store = writer.GetService<IWorkflowDefinitionStore>();
            await store.SetPendingScheduleCursorAsync(id, FireTime);
            await store.SetTriggerCursorAsync(id, "change-feed", "009");
        }

        await using var reader = CreateHost();
        await reader.InitializeAsync();
        var recovered = reader.GetService<IWorkflowDefinitionStore>();
        (await recovered.GetScheduleCursorAsync(id)).Should().Be(FireTime);
        (await recovered.GetTriggerCursorAsync(id, "change-feed")).Should().Be("009");
    }

    [IntegrationTest]
    public async Task TryClaimTriggerFireAsync_CompetingNodes_SeparatesKindsAndAllowsRetry()
    {
        await using var first = CreateHost();
        await using var second = CreateHost();
        await first.InitializeAsync();
        await second.InitializeAsync();
        var owner = first.GetService<IWorkflowDefinitionStore>();
        var peer = second.GetService<IWorkflowDefinitionStore>();
        var id = Guid.NewGuid().ToString("N");
        var retention = TimeSpan.FromHours(24);

        (await owner.TryClaimTriggerFireAsync(id, "change-feed", "001", retention)).Should().BeTrue();
        (await peer.TryClaimTriggerFireAsync(id, "change-feed", "001", retention)).Should().BeFalse();
        (await peer.TryClaimTriggerFireAsync(id, "object-store", "001", retention)).Should().BeTrue();
        await owner.ReleaseTriggerClaimAsync(id, "change-feed", "001");
        (await peer.TryClaimTriggerFireAsync(id, "change-feed", "001", retention)).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task DeleteAsync_StoredDefinition_RemovesCursorsAndEnumerations()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<IWorkflowDefinitionStore>();
        var definition = Definition() with
        {
            Trigger = new WorkflowTrigger { Kind = WorkflowTriggerKind.Cron, CronExpression = "* * * * *" }
        };
        await store.SetAsync(definition);
        await store.AdvanceScheduleCursorAsync(definition.WorkflowId, FireTime);
        await store.SetTriggerCursorAsync(definition.WorkflowId, "change-feed", "001");
        (await store.ListScheduledAsync()).Should().Contain(item => item.WorkflowId == definition.WorkflowId);

        (await store.DeleteAsync(definition.WorkflowId)).Should().BeTrue();
        (await store.DeleteAsync(definition.WorkflowId)).Should().BeFalse();
        (await store.GetAsync(definition.WorkflowId)).Should().BeNull();
        (await store.ListAsync()).Should().NotContain(item => item.WorkflowId == definition.WorkflowId);
        (await store.GetScheduleCursorAsync(definition.WorkflowId)).Should().BeNull();
        (await store.GetTriggerCursorAsync(definition.WorkflowId, "change-feed")).Should().BeNull();
    }

    private static WorkflowDefinition Definition() => new()
    {
        WorkflowId = Guid.NewGuid().ToString("N"),
        Name = "Durable workflow",
        CreatedAt = FireTime,
        UpdatedAt = FireTime,
        Steps = [new WorkflowStepDefinition
        {
            StepId = "extract",
            Plan = new AnalysisPlan { PlanId = "plan", IntentId = "intent", Steps = [] }
        }]
    };

    private static WebAppFixture CreateHost() => new WebAppFixture()
        .WithProductionJobSubstrate()
        .ConfigureWebHost(builder => builder.UseSetting("ConnectionStrings:redis", string.Empty));
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.AnalysisContent;
using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Studio.Services.Bridging;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Studio;

// Analysis packages saved through the Studio bridge keep the creating draft's tenant, so Studio's
// tenant rail treats them like native Studio items rather than default-tenant content.
public sealed class AnalysisStudioBridgeTenantTests
{
    private const string TenantB = "tenant-b";
    private const string DefaultTenant = "public";

    [UnitTest]
    public async Task SaveVersionAsync_TenantDraft_StampsTheTenantOnTheItemPointersAndVersion()
    {
        var store = new InMemoryAnalysisContentStore(TimeProvider.System);
        var bridge = new AnalysisStudioPackageBridge(store, TimeProvider.System);
        var draft = BuildDraft(TenantB);

        var saved = await bridge.SaveVersionAsync(draft, changeNote: null, actorId: "user-b");

        saved.TenantId.Should().Be(TenantB);
        (await bridge.GetPointersAsync(draft.ItemId))!.TenantId.Should().Be(TenantB);
        (await bridge.GetVersionAsync(draft.ItemId, saved.VersionId))!.TenantId.Should().Be(TenantB);
        (await bridge.ListItemSummariesAsync()).Single().TenantId.Should().Be(TenantB);

        var appended = await bridge.SaveVersionAsync(draft, changeNote: "second", actorId: "user-b");
        appended.VersionNumber.Should().Be(2);
        appended.TenantId.Should().Be(TenantB, "a later version never moves the item between tenants");
    }

    [UnitTest]
    public async Task SaveVersionAsync_DraftWithoutTenant_LeavesTheItemUnassigned()
    {
        var store = new InMemoryAnalysisContentStore(TimeProvider.System);
        var bridge = new AnalysisStudioPackageBridge(store, TimeProvider.System);
        var draft = BuildDraft(tenantId: "  ");

        var saved = await bridge.SaveVersionAsync(draft, changeNote: null, actorId: "user");

        saved.TenantId.Should().BeNull();
        (await bridge.GetPointersAsync(draft.ItemId))!.TenantId.Should().BeNull();
    }

    [UnitTest]
    public async Task ListContentItemsAsync_AppliesTheCallersTenantScopeToBridgedAnalysisItems()
    {
        var analysisStore = new InMemoryAnalysisContentStore(TimeProvider.System);
        var bridge = new AnalysisStudioPackageBridge(analysisStore, TimeProvider.System);
        var tenantDraft = BuildDraft(TenantB);
        var unassignedDraft = BuildDraft(tenantId: null);
        await bridge.SaveVersionAsync(tenantDraft, changeNote: null, actorId: "user-b");
        await bridge.SaveVersionAsync(unassignedDraft, changeNote: null, actorId: "user");
        var store = new BridgedStudioPackageStore(new InMemoryStudioPackageStore(), [bridge]);

        var tenantB = await ListAnalysisAsync(store, new TenantScopeFilter(TenantB, IncludeUnassigned: false));
        var defaultTenant = await ListAnalysisAsync(store, new TenantScopeFilter(DefaultTenant, IncludeUnassigned: true));
        var unscoped = await ListAnalysisAsync(store, scope: null);

        tenantB.Should().ContainSingle().Which.ItemId.Should().Be(tenantDraft.ItemId);
        defaultTenant.Should().ContainSingle().Which.ItemId.Should().Be(unassignedDraft.ItemId);
        unscoped.Select(item => item.ItemId).Should().BeEquivalentTo([tenantDraft.ItemId, unassignedDraft.ItemId]);
    }

    private static async Task<IReadOnlyList<StudioContentItemSummary>> ListAnalysisAsync(
        BridgedStudioPackageStore store,
        TenantScopeFilter? scope)
    {
        var result = await store.ListContentItemsAsync(new StudioContentItemQuery
        {
            Families = [StudioPackageFamily.Analysis],
            Tenant = scope,
            Limit = 50,
        });
        return result.Items;
    }

    private static StudioPackageDraft BuildDraft(string? tenantId)
    {
        var itemId = Guid.NewGuid();
        var content = BuildContent();

        return new StudioPackageDraft
        {
            DraftId = Guid.NewGuid(),
            ItemId = itemId,
            PackageKey = $"analysis-{itemId:N}",
            Family = StudioPackageFamily.Analysis,
            OwnerId = "owner",
            TenantId = tenantId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Analysis,
                SchemaVersion = "1.0",
                Format = "honua.analysis-content.v1",
                Body = JsonSerializer.SerializeToElement(content, AnalysisContentJsonContext.Default.AnalysisPackageContent),
            },
        };
    }

    private static AnalysisPackageContent BuildContent() => new()
    {
        Intent = new AnalysisIntent
        {
            IntentId = "intent-1",
            Goal = "buffer incidents",
            RequestedOutputs = [ArtifactKind.FeatureLayer],
        },
        Plan = new AnalysisPlan
        {
            PlanId = "plan-1",
            IntentId = "intent-1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "query-1",
                    Kind = AnalysisPlanStepKind.QueryFeatures,
                    Inputs = new Dictionary<string, string> { ["layerId"] = "1" },
                },
            ],
        },
        RequestedArtifacts = [ArtifactKind.FeatureLayer],
        SpatialReferenceId = 4326,
    };
}

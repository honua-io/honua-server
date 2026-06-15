// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Tests.Features.Licensing;

/// <summary>
/// Unit tests for FeatureCatalog and LicenseModels.
/// </summary>
public sealed class FeatureCatalogTests
{
    [Fact]
    public void All_ContainsFeatures()
    {
        FeatureCatalog.All.Should().NotBeEmpty();
    }

    [Fact]
    public void All_HasUniqueKeys()
    {
        var keys = FeatureCatalog.All.Select(f => f.Key).ToList();
        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void All_EachFeatureHasRequiredProperties()
    {
        foreach (var feature in FeatureCatalog.All)
        {
            feature.Key.Should().NotBeNullOrWhiteSpace();
            feature.DisplayName.Should().NotBeNullOrWhiteSpace();
            feature.Category.Should().NotBeNullOrWhiteSpace();
            feature.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void All_ContainsAllExpectedCategories()
    {
        var categories = FeatureCatalog.All.Select(f => f.Category).Distinct().ToList();

        categories.Should().Contain(FeatureCatalog.Categories.Alerts);
        categories.Should().Contain(FeatureCatalog.Categories.Channels);
        categories.Should().Contain(FeatureCatalog.Categories.Geocoding);
        categories.Should().Contain(FeatureCatalog.Categories.Routing);
        categories.Should().Contain(FeatureCatalog.Categories.Identity);
        categories.Should().Contain(FeatureCatalog.Categories.Caching);
        categories.Should().Contain(FeatureCatalog.Categories.Import);
        categories.Should().Contain(FeatureCatalog.Categories.StaticMap);
        categories.Should().Contain(FeatureCatalog.Categories.Styling);
        categories.Should().Contain(FeatureCatalog.Categories.Raster);
        categories.Should().Contain(FeatureCatalog.Categories.Analytics);
        categories.Should().Contain(FeatureCatalog.Categories.Streaming);
        categories.Should().Contain(FeatureCatalog.Categories.Temporal);
        categories.Should().Contain(FeatureCatalog.Categories.FieldOps);
        categories.Should().Contain(FeatureCatalog.Categories.Ai);
    }

    [Theory]
    [InlineData("analytics.clustering")]
    [InlineData("analytics.spatial-join")]
    [InlineData("analytics.buffer-aggregate")]
    [InlineData("analytics.density")]
    public void All_SpatialAnalyticsFeaturesAreProTier(string key)
    {
        // ADR-0024 + ticket #342 acceptance: every spatial analytics endpoint
        // must be gated behind at least the Pro edition. This test is the
        // catalog-side counterpart to the SpatialAnalyticsEditionGateTests
        // unit tests on the request handler.
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == key);

        feature.Should().NotBeNull($"feature catalog must define '{key}' for ticket #342");
        feature!.Category.Should().Be(FeatureCatalog.Categories.Analytics);
        feature.MinimumEdition.Should().Be(HonuaEdition.Pro);
    }

    [Fact]
    public void All_FeatureStreamingIsProTier()
    {
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == "streaming.feature-subscriptions");

        feature.Should().NotBeNull("feature catalog must define real-time streaming for ticket #339");
        feature!.Category.Should().Be(FeatureCatalog.Categories.Streaming);
        feature.MinimumEdition.Should().Be(HonuaEdition.Pro);
    }

    [Fact]
    public void All_RedisDistributedCacheIsProTier()
    {
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == "caching.redis");

        feature.Should().NotBeNull("Redis L2 activation is gated by the Pro license entitlement for ticket #358");
        feature!.Category.Should().Be(FeatureCatalog.Categories.Caching);
        feature.MinimumEdition.Should().Be(HonuaEdition.Pro);
    }

    [Fact]
    public void All_RoutingSolveIsProTier()
    {
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == "routing.solve");

        feature.Should().NotBeNull("MCP route solving is gated by the Pro license entitlement for ticket #1597");
        feature!.Category.Should().Be(FeatureCatalog.Categories.Routing);
        feature.MinimumEdition.Should().Be(HonuaEdition.Pro);
    }

    [Fact]
    public void All_FeatureServerEditingIsProTier()
    {
        // Ticket #1591: only the Esri GeoServices FeatureServer write surface is
        // Pro-gated. Open-protocol edits remain Community while using the shared
        // edit pipeline.
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.FeatureServerEditsKey);

        feature.Should().NotBeNull("feature catalog must define FeatureServer editing for ticket #1591");
        feature!.Key.Should().Be("editing.featureserver-edits");
        feature.Category.Should().Be(FeatureCatalog.Categories.Editing);
        feature.MinimumEdition.Should().Be(HonuaEdition.Pro);
        feature.Description.Should().Contain("FeatureServer");
        feature.Description.Should().NotContain("OGC API Features");
        feature.Description.Should().NotContain("WFS-T");
        feature.Description.Should().NotContain("OData");
        feature.Description.Should().NotContain("gRPC");
    }

    [Fact]
    public void All_OfflineFieldSyncIsProTier()
    {
        // Ticket #1592: disconnected sync and field offline policy discovery
        // are Pro entitlements; online form reads/submissions remain Community.
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.FieldOpsOfflineSyncKey);

        feature.Should().NotBeNull("feature catalog must define offline field sync for ticket #1592");
        feature!.Key.Should().Be("fieldops.offline-sync");
        feature.Category.Should().Be(FeatureCatalog.Categories.FieldOps);
        feature.MinimumEdition.Should().Be(HonuaEdition.Pro);
    }

    [Theory]
    [InlineData(FeatureCatalog.AiSpecApplyKey, "ai.spec-apply")]
    [InlineData(FeatureCatalog.AiGroundingKey, "ai.grounding")]
    [InlineData(FeatureCatalog.AiWorkflowGenerationKey, "ai.workflow-generation")]
    public void All_AgenticAiOperationsAreProTier(string key, string expectedKey)
    {
        // Ticket #1592: AI operations that mutate, generate, or submit
        // execution work are Pro entitlements. Discovery/query/read surfaces
        // stay Community and are not represented by these keys.
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == key);

        feature.Should().NotBeNull($"feature catalog must define '{expectedKey}' for ticket #1592");
        feature!.Key.Should().Be(expectedKey);
        feature.Category.Should().Be(FeatureCatalog.Categories.Ai);
        feature.MinimumEdition.Should().Be(HonuaEdition.Pro);
    }

    [Fact]
    public void All_BranchVersioningIsEnterpriseTier()
    {
        // Branch versioning stays an Enterprise entitlement, distinct from the Pro
        // FeatureServer editing gate scoped in #1591.
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.BranchVersioningKey);

        feature.Should().NotBeNull("feature catalog must define branch versioning");
        feature!.Category.Should().Be(FeatureCatalog.Categories.Editing);
        feature.MinimumEdition.Should().Be(HonuaEdition.Enterprise);
    }

    [Fact]
    public void All_OidcSingleProviderIsProAndGovernanceIsEnterprise()
    {
        // Ticket #1612: basic single-provider OIDC has no SSO tax and validates
        // under Pro. Multi-provider configuration and claim-to-role mapping stay
        // Enterprise identity-governance entitlements.
        var oidc = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.OidcAuthenticationKey);
        var multiProvider = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.OidcMultiProviderKey);
        var claimsMapping = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.OidcClaimsMappingKey);

        oidc.Should().NotBeNull("feature catalog must define basic OIDC authentication for ticket #1612");
        oidc!.Category.Should().Be(FeatureCatalog.Categories.Identity);
        oidc.MinimumEdition.Should().Be(HonuaEdition.Pro);
        oidc.Description.Should().Contain("Single-provider");

        multiProvider.Should().NotBeNull("feature catalog must keep multi-provider SSO Enterprise-gated");
        multiProvider!.Category.Should().Be(FeatureCatalog.Categories.Identity);
        multiProvider.MinimumEdition.Should().Be(HonuaEdition.Enterprise);

        claimsMapping.Should().NotBeNull("feature catalog must keep OIDC claims mapping Enterprise-gated");
        claimsMapping!.Category.Should().Be(FeatureCatalog.Categories.Identity);
        claimsMapping.MinimumEdition.Should().Be(HonuaEdition.Enterprise);
    }

    [Fact]
    public void All_CommunityFeaturesAreExpected()
    {
        // Community features are explicitly tracked — adding one requires updating this test
        var communityFeatures = FeatureCatalog.All
            .Where(f => f.MinimumEdition == HonuaEdition.Community)
            .Select(f => f.Key)
            .ToList();

        communityFeatures.Should().BeEquivalentTo(
        [
            "styling.defaults",
            "temporal.filtering",
            "temporal.extent-discovery",
            "identity.portal-token",
            "identity.portal-sharing",
            "import.file",
            "ai.mcp-discovery"
        ]);
    }

    [Fact]
    public void All_AiGuardrailLadderHasExpectedEditions()
    {
        // Ticket #1631 / #1592: the AI-operations guardrail ladder is the edition
        // axis. MCP discovery/query stays Community (agent reads never gated), the
        // agent-operations validation layer is Pro, and approval workflows are
        // Enterprise.
        var discovery = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.McpDiscoveryKey);
        var operations = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.AiOperationsKey);
        var approval = FeatureCatalog.All.SingleOrDefault(f => f.Key == FeatureCatalog.AiApprovalWorkflowsKey);

        discovery.Should().NotBeNull("MCP discovery/query stays Community for ticket #1631");
        discovery!.Category.Should().Be(FeatureCatalog.Categories.Ai);
        discovery.MinimumEdition.Should().Be(HonuaEdition.Community);

        operations.Should().NotBeNull("agent-operations validation layer is Pro for ticket #1631");
        operations!.Category.Should().Be(FeatureCatalog.Categories.Ai);
        operations.MinimumEdition.Should().Be(HonuaEdition.Pro);

        approval.Should().NotBeNull("agent approval workflows are Enterprise for ticket #1631");
        approval!.Category.Should().Be(FeatureCatalog.Categories.Ai);
        approval.MinimumEdition.Should().Be(HonuaEdition.Enterprise);
    }

    [Theory]
    [InlineData("temporal.filtering", HonuaEdition.Community)]
    [InlineData("temporal.extent-discovery", HonuaEdition.Community)]
    [InlineData("temporal.histogram", HonuaEdition.Pro)]
    [InlineData("temporal.time-series-tiles", HonuaEdition.Pro)]
    [InlineData("temporal.animation-api", HonuaEdition.Pro)]
    public void All_TemporalFeaturesHaveExpectedEdition(string key, HonuaEdition expectedEdition)
    {
        // Ticket #379 acceptance: capability reporting tells SDK/admin clients
        // which temporal features are available and edition-gated.
        var feature = FeatureCatalog.All.SingleOrDefault(f => f.Key == key);

        feature.Should().NotBeNull($"feature catalog must define '{key}' for ticket #379");
        feature!.Category.Should().Be(FeatureCatalog.Categories.Temporal);
        feature.MinimumEdition.Should().Be(expectedEdition);
    }

    [Theory]
    [InlineData(HonuaEdition.Community, 0)]
    [InlineData(HonuaEdition.Pro, 1)]
    [InlineData(HonuaEdition.Enterprise, 2)]
    public void HonuaEdition_HasExpectedValues(HonuaEdition edition, int expectedValue)
    {
        ((int)edition).Should().Be(expectedValue);
    }

    [Fact]
    public void LicenseStatus_DaysUntilExpiry_NullForNoExpiry()
    {
        var status = new LicenseStatus(HonuaEdition.Community, true, null, null);
        status.DaysUntilExpiry.Should().BeNull();
    }

    [Fact]
    public void LicenseStatus_DaysUntilExpiry_CalculatedCorrectly()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(15);
        var status = new LicenseStatus(HonuaEdition.Pro, true, expiresAt, "Test Corp");

        status.DaysUntilExpiry.Should().BeInRange(14, 16);
        status.LicensedTo.Should().Be("Test Corp");
    }

    [Fact]
    public void LicenseUploadResult_Success()
    {
        var result = new LicenseUploadResult(true, "License applied.");
        result.Success.Should().BeTrue();
        result.Message.Should().Be("License applied.");
    }

    [Fact]
    public void LicenseUploadResult_Failure()
    {
        var result = new LicenseUploadResult(false, "Not supported.");
        result.Success.Should().BeFalse();
    }
}

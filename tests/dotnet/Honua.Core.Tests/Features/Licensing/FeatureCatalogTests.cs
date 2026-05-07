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
        categories.Should().Contain(FeatureCatalog.Categories.Identity);
        categories.Should().Contain(FeatureCatalog.Categories.Caching);
        categories.Should().Contain(FeatureCatalog.Categories.Import);
        categories.Should().Contain(FeatureCatalog.Categories.StaticMap);
        categories.Should().Contain(FeatureCatalog.Categories.Styling);
        categories.Should().Contain(FeatureCatalog.Categories.Raster);
        categories.Should().Contain(FeatureCatalog.Categories.Analytics);
        categories.Should().Contain(FeatureCatalog.Categories.Streaming);
        categories.Should().Contain(FeatureCatalog.Categories.Temporal);
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
            "temporal.extent-discovery"
        ]);
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

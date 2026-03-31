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
    }

    [Fact]
    public void All_CommunityFeaturesAreExpected()
    {
        // Community features are explicitly tracked — adding one requires updating this test
        var communityFeatures = FeatureCatalog.All
            .Where(f => f.MinimumEdition == HonuaEdition.Community)
            .Select(f => f.Key)
            .ToList();

        communityFeatures.Should().BeEquivalentTo(["styling.defaults"]);
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

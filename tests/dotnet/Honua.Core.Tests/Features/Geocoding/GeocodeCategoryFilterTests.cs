// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Core.Tests.Features.Geocoding;

public sealed class GeocodeCategoryFilterTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseCategories_ReturnsNull_WhenNoFilterRequested(string? raw)
    {
        Assert.Null(GeocodeCategoryFilter.ParseCategories(raw));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void ParseCategories_SplitsAndTrimsTokens()
    {
        var result = GeocodeCategoryFilter.ParseCategories(" POI , PointAddress ;City ");

        Assert.NotNull(result);
        Assert.Equal(["POI", "PointAddress", "City"], result!);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Matches_ReturnsTrue_WhenNoFilterRequested()
    {
        Assert.True(GeocodeCategoryFilter.Matches("PointAddress", requestedCategories: null));
        Assert.True(GeocodeCategoryFilter.Matches(null, requestedCategories: null));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Matches_IsCaseInsensitive()
    {
        var requested = GeocodeCategoryFilter.ParseCategories("poi");

        Assert.True(GeocodeCategoryFilter.Matches("POI", requested));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Matches_ReturnsFalse_WhenCategoryNotInRequestedSet()
    {
        var requested = GeocodeCategoryFilter.ParseCategories("POI,City");

        Assert.False(GeocodeCategoryFilter.Matches("PointAddress", requested));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Matches_ReturnsFalse_WhenResultHasNoCategoryButFilterRequested()
    {
        var requested = GeocodeCategoryFilter.ParseCategories("POI");

        Assert.False(GeocodeCategoryFilter.Matches(null, requested));
        Assert.False(GeocodeCategoryFilter.Matches("", requested));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("PointAddress")]
    [InlineData("StreetAddress")]
    [InlineData("SubAddress")]
    public void Matches_AddressCategory_CoversAddressTypeFamily(string addressType)
    {
        var requested = GeocodeCategoryFilter.ParseCategories("Address");

        Assert.True(GeocodeCategoryFilter.Matches(addressType, requested));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Matches_AddressCategory_DoesNotMatchNonAddressTypes()
    {
        var requested = GeocodeCategoryFilter.ParseCategories("Address");

        Assert.False(GeocodeCategoryFilter.Matches("POI", requested));
        Assert.False(GeocodeCategoryFilter.Matches("City", requested));
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Core.Tests.Features.Geocoding;

public sealed class GeocodeCountryCodesTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("United States", "US")]
    [InlineData("usa", "US")]
    [InlineData("Deutschland", "DE")]
    [InlineData("Germany", "DE")]
    [InlineData("JPN", "JP")]
    [InlineData("France", "FR")]
    public void TryParseGeoServicesCountry_ResolvesNamesAndAlpha3Codes(string value, string expected)
    {
        Assert.True(GeocodeCountryCodes.TryParseGeoServicesCountry(value, out var code));
        Assert.Equal(expected, code);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void TryParseGeoServicesCountry_RejectsUnknownName()
    {
        Assert.False(GeocodeCountryCodes.TryParseGeoServicesCountry("Atlantis", out var code));
        Assert.Null(code);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void StoredRepresentations_IncludeTheCodeItsAlpha3AndEnglishName()
    {
        var stored = GeocodeCountryCodes.StoredRepresentations(["US"]);

        Assert.Contains("US", stored);
        Assert.Contains("USA", stored);
        Assert.Contains("UNITED STATES", stored);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("United States")]
    [InlineData("usa")]
    public void Matches_AcceptsStoredNamesOfTheRequestedCountry(string stored)
    {
        Assert.True(GeocodeCountryCodes.Matches(stored, ["US"]));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Matches_RejectsAnotherCountry()
    {
        Assert.False(GeocodeCountryCodes.Matches("Canada", ["US"]));
    }
}

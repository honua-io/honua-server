// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.GeoServicesRest.Tests;

public class DataProviderNameNormalizationTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("arcgis-rest")]
    [InlineData("arcgis_rest")]
    [InlineData("ArcGisRest")]
    [InlineData("ARCGIS-REST")]
    [InlineData("arcgis")]
    [InlineData("esri")]
    [InlineData("esri-rest")]
    [InlineData("Esri-FeatureServer")]
    [InlineData("FeatureServer")]
    public void Normalize_AllAliases_ResolveToCanonicalArcGisRest(string provider)
    {
        Assert.Equal(DataProviderNames.ArcGisRest, DataProviderNames.Normalize(provider));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Normalize_UnknownString_PassesThroughLowercased()
    {
        Assert.Equal("custom", DataProviderNames.Normalize("Custom"));
    }
}

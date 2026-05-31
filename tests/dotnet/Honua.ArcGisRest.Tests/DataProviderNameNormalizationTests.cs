// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.ArcGisRest.Tests;

public class DataProviderNameNormalizationTests
{
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

    [Fact]
    public void Normalize_UnknownString_PassesThroughLowercased()
    {
        Assert.Equal("custom", DataProviderNames.Normalize("Custom"));
    }
}

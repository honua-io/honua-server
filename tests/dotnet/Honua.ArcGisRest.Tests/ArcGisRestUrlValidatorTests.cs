// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ArcGisRest.Features.FeatureStore.Services;

namespace Honua.ArcGisRest.Tests;

public sealed class ArcGisRestUrlValidatorTests
{
    [Theory]
    [InlineData("https://10.1.2.3/arcgis/rest/services/parcels/FeatureServer")]
    [InlineData("https://172.16.2.3/arcgis/rest/services/parcels/FeatureServer")]
    [InlineData("https://192.168.2.3/arcgis/rest/services/parcels/FeatureServer")]
    [InlineData("https://169.254.169.254/arcgis/rest/services/parcels/FeatureServer")]
    [InlineData("https://[::1]/arcgis/rest/services/parcels/FeatureServer")]
    [InlineData("https://[fc00::1]/arcgis/rest/services/parcels/FeatureServer")]
    public void NormalizeServiceRootUrl_WithNonPublicAddress_Throws(string url)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ArcGisRestUrlValidator.NormalizeServiceRootUrl(url));

        Assert.Contains("private", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeServiceRootUrl_WithPublicServiceRoot_ReturnsCanonicalRoot()
    {
        var normalized = ArcGisRestUrlValidator.NormalizeServiceRootUrl(
            "https://8.8.8.8/arcgis/rest/services/parcels/FeatureServer/?f=json");

        Assert.Equal(
            "https://8.8.8.8/arcgis/rest/services/parcels/FeatureServer",
            normalized);
    }
}

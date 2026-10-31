// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.GeoServicesRest.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.GeoServicesRest.Tests;

public class GeoServicesRestServiceLocatorTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_PreferConnectionString_TrimsTrailingSlash()
    {
        var connection = new DataConnection
        {
            Provider = DataProviderNames.GeoServicesRest,
            ConnectionString = "https://services.arcgis.com/o/arcgis/rest/services/parcels/FeatureServer/"
        };

        var resolved = GeoServicesRestServiceLocator.Resolve(connection);

        Assert.Equal(
            "https://services.arcgis.com/o/arcgis/rest/services/parcels/FeatureServer",
            resolved.ServiceUrl);
        Assert.Null(resolved.Token);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_FallsBackToServiceUrlProperty_WhenConnectionStringEmpty()
    {
        var connection = new DataConnection
        {
            Provider = DataProviderNames.GeoServicesRest,
            ConnectionString = string.Empty
        };
        connection.Properties[GeoServicesRestServiceLocator.ServiceUrlPropertyKey] =
            "https://example.com/arcgis/rest/services/x/MapServer";

        var resolved = GeoServicesRestServiceLocator.Resolve(connection);

        Assert.Equal("https://example.com/arcgis/rest/services/x/MapServer", resolved.ServiceUrl);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_ReadsTokenFromProperties()
    {
        var connection = new DataConnection
        {
            Provider = DataProviderNames.GeoServicesRest,
            ConnectionString = "https://services.arcgis.com/o/arcgis/rest/services/parcels/FeatureServer"
        };
        connection.Properties[GeoServicesRestServiceLocator.TokenPropertyKey] = "abc-token";

        var resolved = GeoServicesRestServiceLocator.Resolve(connection);

        Assert.Equal("abc-token", resolved.Token);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_NullConnection_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => GeoServicesRestServiceLocator.Resolve(connection: null));
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("http://services.arcgis.com/x/FeatureServer")]
    [InlineData("ftp://services.arcgis.com/x/FeatureServer")]
    [InlineData("https://user:pass@services.arcgis.com/x/FeatureServer")]
    [InlineData("https://localhost/x/FeatureServer")]
    [InlineData("https://example.com/x/SomeOtherService")]
    [InlineData("not-a-url")]
    public void Resolve_RejectsUnsafeOrMalformedUrls(string url)
    {
        var connection = new DataConnection
        {
            Provider = DataProviderNames.GeoServicesRest,
            ConnectionString = url
        };

        Assert.Throws<ArgumentException>(() => GeoServicesRestServiceLocator.Resolve(connection));
    }
}

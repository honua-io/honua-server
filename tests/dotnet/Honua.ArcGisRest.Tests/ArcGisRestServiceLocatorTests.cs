// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ArcGisRest.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.ArcGisRest.Tests;

public class ArcGisRestServiceLocatorTests
{
    [Fact]
    public void Resolve_PreferConnectionString_TrimsTrailingSlash()
    {
        var connection = new DataConnection
        {
            Provider = DataProviderNames.ArcGisRest,
            ConnectionString = "https://services.arcgis.com/o/arcgis/rest/services/parcels/FeatureServer/"
        };

        var resolved = ArcGisRestServiceLocator.Resolve(connection);

        Assert.Equal(
            "https://services.arcgis.com/o/arcgis/rest/services/parcels/FeatureServer",
            resolved.ServiceUrl);
        Assert.Null(resolved.Token);
    }

    [Fact]
    public void Resolve_FallsBackToServiceUrlProperty_WhenConnectionStringEmpty()
    {
        var connection = new DataConnection
        {
            Provider = DataProviderNames.ArcGisRest,
            ConnectionString = string.Empty
        };
        connection.Properties[ArcGisRestServiceLocator.ServiceUrlPropertyKey] =
            "https://example.com/arcgis/rest/services/x/MapServer";

        var resolved = ArcGisRestServiceLocator.Resolve(connection);

        Assert.Equal("https://example.com/arcgis/rest/services/x/MapServer", resolved.ServiceUrl);
    }

    [Fact]
    public void Resolve_ReadsTokenFromProperties()
    {
        var connection = new DataConnection
        {
            Provider = DataProviderNames.ArcGisRest,
            ConnectionString = "https://services.arcgis.com/o/arcgis/rest/services/parcels/FeatureServer"
        };
        connection.Properties[ArcGisRestServiceLocator.TokenPropertyKey] = "abc-token";

        var resolved = ArcGisRestServiceLocator.Resolve(connection);

        Assert.Equal("abc-token", resolved.Token);
    }

    [Fact]
    public void Resolve_NullConnection_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ArcGisRestServiceLocator.Resolve(connection: null));
    }

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
            Provider = DataProviderNames.ArcGisRest,
            ConnectionString = url
        };

        Assert.Throws<ArgumentException>(() => ArcGisRestServiceLocator.Resolve(connection));
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Mapsui;
using Mapsui.Providers.Wms;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

[Protocol(TestProtocols.Wms13)]
[Collection("Database")]
public sealed class MapsuiWmsClientCompatibilityTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public MapsuiWmsClientCompatibilityTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task MapsuiWmsProvider_CanDiscoverLayerAndFetchMap()
    {
        var wmsUri = new Uri(
            _fixture.Client.BaseAddress!,
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS");

        var provider = await WmsProvider.CreateAsync(
            wmsUri.ToString(),
            "1.3.0",
            FetchBytesAsync,
            persistentCache: null,
            userAgent: "Honua.Tests/Mapsui");

        provider.Version.Should().Be("1.3.0");
        provider.OutputFormats.Should().Contain(format =>
            string.Equals(format, "image/png", StringComparison.OrdinalIgnoreCase));
        provider.RootLayer.Should().NotBeNull();

        var layerName = GetFirstAdvertisedLayerName(provider.RootLayer.Value);
        provider.GetLayer(layerName).Should().NotBeNull();

        provider.AddLayer(layerName);
        provider.AddStyle("default");
        provider.SetImageFormat("image/png");
        provider.CRS = "EPSG:4326";
        provider.Transparent = true;

        var requestUrl = provider.GetRequestUrl(new MRect(-180, -90, 180, 90), 256, 256);
        requestUrl.Should().Contain("REQUEST=GetMap");
        requestUrl.Should().Contain("VERSION=1.3.0");
        requestUrl.Should().Contain("Layers=");

        using var response = await _fixture.Client.GetAsync(requestUrl);
        var content = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        content.Should().StartWith([137, 80, 78, 71, 13, 10, 26, 10]);
    }

    private static string GetFirstAdvertisedLayerName(Client.WmsServerLayer layer)
    {
        if (!string.IsNullOrWhiteSpace(layer.Name))
        {
            return layer.Name;
        }

        foreach (var name in (layer.ChildLayers ?? []).Select(GetFirstAdvertisedLayerName))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        throw new InvalidOperationException("Mapsui did not discover an advertised WMS layer name.");
    }

    private async Task<byte[]> FetchBytesAsync(string url)
    {
        using var response = await _fixture.Client.GetAsync(url);
        var content = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return content;
    }
}

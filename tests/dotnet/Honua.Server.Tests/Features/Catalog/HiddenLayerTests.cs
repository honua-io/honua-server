// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Catalog;

[Collection("Database")]
public sealed partial class HiddenLayerTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private MetadataV2Graph _original = null!;
    private TestMetadataV2GraphProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _provider = (TestMetadataV2GraphProvider)_fixture.Services.GetRequiredService<IMetadataV2GraphProvider>();
        _original = _fixture.GetCurrentV2GraphSnapshot().Graph;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /ogc/services/{serviceId}/wms")]
    public async Task Wms_HiddenPublication_OmitsCapabilitiesAndRendersNamedLayer()
    {
        var publication = _original.Publications.First(publication => publication.ServiceId == _original.Services.First(service => service.Metadata.Name == WebAppFixture.TestServiceId).Metadata.Id);
        var route = $"/ogc/services/{WebAppFixture.TestServiceId}/wms?SERVICE=WMS";
        using var before = await _fixture.Client.GetAsync(route + "&REQUEST=GetCapabilities");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var visible = XDocument.Parse(await before.Content.ReadAsStringAsync());
        var names = visible.Descendants().Where(element => element.Name.LocalName == "Layer")
            .Elements().Where(element => element.Name.LocalName == "Name").Select(element => element.Value).ToArray();
        Assert.NotEmpty(names);
        SetAdvertising(false);
        using var capabilities = await _fixture.Client.GetAsync(route + "&REQUEST=GetCapabilities");
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        var xml = XDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        Assert.DoesNotContain(xml.Descendants().Where(element => element.Name.LocalName == "Layer")
            .Elements().Where(element => element.Name.LocalName == "Name"), name => names.Contains(name.Value, StringComparer.Ordinal));
        using var rendered = await _fixture.Client.GetAsync(route + $"&REQUEST=GetMap&VERSION=1.3.0&LAYERS={Uri.EscapeDataString(publication.ServiceLocalId ?? WebAppFixture.TestLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture))}&CRS=EPSG:4326&BBOX=37,-123,38,-122&WIDTH=32&HEIGHT=32&FORMAT=image/png&STYLES=");
        Assert.Equal(HttpStatusCode.OK, rendered.StatusCode);
        Assert.Equal("image/png", rendered.Content.Headers.ContentType?.MediaType);
    }

    [IntegrationTheory]
    [InlineData("retired")]
    [InlineData("disabled")]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId:int}/query")]
    public async Task Query_HiddenThenUnavailablePublication_RemainsRequestableUntilUnavailable(string transition)
    {
        SetAdvertising(false);
        var route = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer";
        using var metadata = await _fixture.Client.GetAsync(route + "?f=json");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        using var document = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
        Assert.Empty(document.RootElement.GetProperty("layers").EnumerateArray());
        using var query = await _fixture.Client.GetAsync(route + $"/{WebAppFixture.TestLayerId}/query?where=1%3D1&returnCountOnly=true&f=json");
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        using var count = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        Assert.True(count.RootElement.GetProperty("count").GetInt32() > 0);
        if (transition == "disabled")
        {
            _fixture.SetV2LayerEnabled(WebAppFixture.TestLayerId, enabled: false);
        }
        else
        {
            _provider.SetGraph(_original with { Revision = _original.Revision + 2, Publications = _original.Publications.Select(publication => publication with
            { Advertised = false, Status = new() { Lifecycle = MetadataV2LifecycleStatus.Retired } }).ToArray() }, schema: _fixture.CurrentSchema);
        }
        using var retired = await _fixture.Client.GetAsync(route + $"/{WebAppFixture.TestLayerId}/query?where=1%3D1&returnCountOnly=true&f=json");
        Assert.NotEqual(HttpStatusCode.OK, retired.StatusCode);
    }

    [IntegrationTheory]
    [InlineData("/ogc/features/collections", HttpStatusCode.OK)]
    [InlineData("/ogc/maps/collections", HttpStatusCode.OK)]
    [InlineData("/ogc/tiles/collections", HttpStatusCode.NotFound)]
    [InlineData("/stac/collections", HttpStatusCode.OK)]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/collections")]
    [Endpoint("GET /ogc/maps/collections")]
    [Endpoint("GET /ogc/tiles/collections")]
    [Endpoint("GET /stac/collections")]
    public async Task Collections_HiddenPublication_OmitsCollections(string path, HttpStatusCode emptyStatus)
    {
        using var before = await _fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var visible = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        Assert.NotEmpty(visible.RootElement.GetProperty("collections").EnumerateArray());
        SetAdvertising(false);
        using var after = await _fixture.Client.GetAsync(path);
        Assert.Equal(emptyStatus, after.StatusCode);
        if (emptyStatus == HttpStatusCode.OK)
        {
            using var hidden = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
            Assert.Empty(hidden.RootElement.GetProperty("collections").EnumerateArray());
        }
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs20)]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    [Endpoint("GET /wfs")]
    public async Task WfsCapabilities_HiddenPublication_OmitsFeatureTypes()
    {
        const string path = "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0";
        using var before = await _fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var visible = XDocument.Parse(await before.Content.ReadAsStringAsync());
        Assert.Contains(visible.Descendants(), element => element.Name.LocalName == "FeatureType");
        SetAdvertising(false);
        using var after = await _fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var hidden = XDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.DoesNotContain(hidden.Descendants(), element => element.Name.LocalName == "FeatureType");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.Wmts)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    public async Task WmtsCapabilities_HiddenPublication_OmitsLayers()
    {
        var path = $"/ogc/services/{WebAppFixture.TestServiceId}/wmts?SERVICE=WMTS&REQUEST=GetCapabilities";
        using var before = await _fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var visible = XDocument.Parse(await before.Content.ReadAsStringAsync());
        Assert.Contains(visible.Descendants(), element => element.Name.LocalName == "Layer");
        SetAdvertising(false);
        using var after = await _fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var hidden = XDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.DoesNotContain(hidden.Descendants(), element => element.Name.LocalName == "Layer");
    }

    private void SetAdvertising(bool advertised)
    {
        _provider.SetGraph(_original with
        {
            Revision = _original.Revision + 1,
            Publications = _original.Publications.Select(publication =>
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(publication, MetadataV2JsonContext.Default.MetadataV2Publication));
                var members = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
                members["advertised"] = JsonSerializer.SerializeToElement(advertised);
                return JsonSerializer.Deserialize<MetadataV2Publication>(JsonSerializer.Serialize(members), MetadataV2JsonContext.Default.MetadataV2Publication)!;
            }).ToArray()
        }, schema: _fixture.CurrentSchema);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Provides an isolated database-backed application whose layer 0 marks <c>category</c>
/// as publisher-hidden.
/// </summary>
public sealed class OgcClassicFeatureInfoHiddenFieldFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceSchemaField(0, new MetadataV2Field
        {
            Name = "category",
            Type = MetadataV2FieldType.String,
            Nullable = true,
            Hidden = true
        });
    }

    public Task DisposeAsync() => App.DisposeAsync();
}

/// <summary>
/// WMS and WMTS GetFeatureInfo omit publisher-hidden fields in every info format, and WMS
/// FILTER cannot reference them, matching WFS (#6098).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class OgcClassicFeatureInfoHiddenFieldTests(OgcClassicFeatureInfoHiddenFieldFixture fixture)
    : IClassFixture<OgcClassicFeatureInfoHiddenFieldFixture>
{
    private const string WmsGetFeatureInfo =
        $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0" +
        "&BBOX=-180,-90,180,90&CRS=CRS:84&WIDTH=256&HEIGHT=256&LAYERS=0&QUERY_LAYERS=0&I=41&J=74";

    private readonly WebAppFixture _fixture = fixture.App;

    [IntegrationTheory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("application/vnd.ogc.gml")]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetFeatureInfo_OmitsHiddenFields(string infoFormat)
    {
        using var response = await _fixture.Client.GetAsync(
            $"{WmsGetFeatureInfo}&INFO_FORMAT={Uri.EscapeDataString(infoFormat)}");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("Feature", "the click must hit a feature for the omission to mean anything");
        content.Should().NotContain("category");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Wmts10)]
    [Operation(Operations.Wmts)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task Wmts_GetFeatureInfo_OmitsHiddenFields()
    {
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0" +
            "&LAYER=0&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0" +
            "&I=41&J=99&INFOFORMAT=application/json");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var json = JsonDocument.Parse(content);
        var features = json.RootElement.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThan(0, content);
        foreach (var feature in features.EnumerateArray())
        {
            feature.GetProperty("properties").TryGetProperty("name", out _).Should().BeTrue(content);
            feature.GetProperty("properties").TryGetProperty("category", out _).Should().BeFalse(content);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_FilterOnHiddenField_IsRejected()
    {
        const string filter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsLike wildCard=\"*\" singleChar=\".\" escapeChar=\"!\"><fes:ValueReference>category</fes:ValueReference><fes:Literal>*</fes:Literal></fes:PropertyIsLike></fes:Filter>";
        using var response = await _fixture.Client.GetAsync(
            $"{WmsGetFeatureInfo}&INFO_FORMAT=application/json&FILTER={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, content);
        content.Should().Contain("InvalidParameterValue");
    }

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_FilterOnVisibleField_StillApplies()
    {
        const string filter = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsLike wildCard=\"*\" singleChar=\".\" escapeChar=\"!\"><fes:ValueReference>name</fes:ValueReference><fes:Literal>*Feature</fes:Literal></fes:PropertyIsLike></fes:Filter>";
        using var response = await _fixture.Client.GetAsync(
            $"{WmsGetFeatureInfo}&INFO_FORMAT=application/json&FILTER={Uri.EscapeDataString(filter)}");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("features").GetArrayLength().Should().BeGreaterThan(0, content);
    }
}

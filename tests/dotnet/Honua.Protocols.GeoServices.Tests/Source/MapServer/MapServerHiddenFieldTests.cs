// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>
/// MapServer identify, find, generateKml and dynamicLayers joins never emit, search or join on
/// publisher-hidden fields, matching FeatureServer query (#6098). The in-memory store returns
/// every stored attribute, so these prove the handlers filter rather than relying on the reader.
/// Left layer 0 hides <c>category</c>; right layer 1 hides <c>name</c>.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerHiddenFieldTests
{
    private const string ServicePath = $"/rest/services/{MapServerDynamicJoinTests.ServiceName}/MapServer";
    private const string IdentifyQuery =
        "?geometry=-122.5,37.5&geometryType=esriGeometryPoint&mapExtent=-180,-90,180,90" +
        "&imageDisplay=800,600,96&tolerance=10&returnGeometry=false";

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task Identify_OmitsHiddenFields()
    {
        using var factory = MapServerDynamicJoinTests.CreateFactory(rightLayerAnonymous: true, hiddenFields: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{ServicePath}/identify{IdentifyQuery}&layers=all:0&f=json");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var results = document.RootElement.GetProperty("results");
        results.GetArrayLength().Should().BeGreaterThan(0, content);
        foreach (var result in results.EnumerateArray())
        {
            var attributes = result.GetProperty("attributes");
            attributes.TryGetProperty("name", out _).Should().BeTrue(content);
            attributes.TryGetProperty("category", out _).Should().BeFalse(content);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task Identify_WithJoin_OmitsHiddenLeftAndRightFields()
    {
        using var factory = MapServerDynamicJoinTests.CreateFactory(rightLayerAnonymous: true, hiddenFields: true);
        using var client = factory.CreateClient();

        var dynamicLayers = Uri.EscapeDataString(
            MapServerDynamicJoinTests.BuildJoinDynamicLayers("esriLeftOuterJoin"));
        using var response = await client.GetAsync(
            $"{ServicePath}/identify{IdentifyQuery}&layers=all&dynamicLayers={dynamicLayers}&f=json");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var document = JsonDocument.Parse(content);
        var results = document.RootElement.GetProperty("results");
        results.GetArrayLength().Should().BeGreaterThan(0, content);
        var attributes = results[0].GetProperty("attributes");
        attributes.TryGetProperty("right_parcels.id", out _).Should().BeTrue(content);
        attributes.TryGetProperty("right_parcels.name", out _).Should().BeFalse(content);
        attributes.TryGetProperty("category", out _).Should().BeFalse(content);
        content.Should().NotContain("String ID");
    }

    [IntegrationTheory]
    [InlineData("category", "objectid")]
    [InlineData("objectid", "name")]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/identify")]
    public async Task Identify_JoinOnHiddenKey_Returns400(string leftKey, string rightKey)
    {
        using var factory = MapServerDynamicJoinTests.CreateFactory(rightLayerAnonymous: true, hiddenFields: true);
        using var client = factory.CreateClient();

        var dynamicLayers = Uri.EscapeDataString(
            MapServerDynamicJoinTests.BuildJoinDynamicLayers("esriInnerJoin", leftKey, rightKey));
        using var response = await client.GetAsync(
            $"{ServicePath}/identify{IdentifyQuery}&layers=all&dynamicLayers={dynamicLayers}&f=json");

        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Find)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/find")]
    public async Task Find_DoesNotSearchOrReturnHiddenFields()
    {
        using var factory = MapServerDynamicJoinTests.CreateFactory(rightLayerAnonymous: true, hiddenFields: true);
        using var client = factory.CreateClient();

        // "sample" occurs only in the hidden category column.
        using var hiddenOnly = await client.GetAsync($"{ServicePath}/find?searchText=sample&layers=0&f=json");
        var hiddenContent = await hiddenOnly.Content.ReadAsStringAsync();
        hiddenOnly.StatusCode.Should().Be(HttpStatusCode.OK, hiddenContent);
        using (var document = JsonDocument.Parse(hiddenContent))
        {
            document.RootElement.GetProperty("results").GetArrayLength().Should().Be(0, hiddenContent);
        }

        using var explicitHidden = await client.GetAsync(
            $"{ServicePath}/find?searchText=sample&layers=0&searchFields=category&f=json");
        (await explicitHidden.Content.ReadAsStringAsync()).Should().NotContain("\"foundFieldName\":\"category\"");

        using var visible = await client.GetAsync($"{ServicePath}/find?searchText=Feature&layers=0&f=json");
        var visibleContent = await visible.Content.ReadAsStringAsync();
        visible.StatusCode.Should().Be(HttpStatusCode.OK, visibleContent);
        using var visibleDocument = JsonDocument.Parse(visibleContent);
        var results = visibleDocument.RootElement.GetProperty("results");
        results.GetArrayLength().Should().BeGreaterThan(0, visibleContent);
        foreach (var result in results.EnumerateArray())
        {
            result.GetProperty("attributes").TryGetProperty("category", out _).Should().BeFalse(visibleContent);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/generateKml")]
    public async Task GenerateKml_OmitsHiddenFieldsFromExtendedData()
    {
        using var factory = MapServerDynamicJoinTests.CreateFactory(rightLayerAnonymous: true, hiddenFields: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{ServicePath}/generateKml?layers=0&f=kml");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        XNamespace kml = "http://www.opengis.net/kml/2.2";
        var dataNames = XDocument.Parse(content)
            .Descendants(kml + "Data")
            .Select(data => (string?)data.Attribute("name"))
            .ToArray();
        dataNames.Should().Contain("name", content);
        dataNames.Should().NotContain("category", content);
    }
}

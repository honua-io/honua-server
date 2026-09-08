// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts;

/// <summary>
/// Verifies WMTS responses for CLR attribute values returned by data providers.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wmts10)]
public sealed class WmtsRuntimeAttributeFeatureInfoTests : IAsyncLifetime
{
    private readonly IFeatureReader _reader = Substitute.For<IFeatureReader>();
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.ReplaceRequestService(_reader);
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("ogc")]
    [InlineData("mapserver")]
    [InlineData("restful")]
    [Operation(Operations.Wmts)]
    [InterfaceOperation(TestProtocols.Wmts10, "GetFeatureInfo")]
    [Endpoint("GET /ogc/services/{serviceId}/wmts")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    public async Task GetFeatureInfo_ProviderRuntimeTypes_ReturnsTypedGeoJson(string route)
    {
        // Supply CLR values directly: the JSONB seed path normalizes numbers to
        // long/double and cannot reproduce every provider's runtime attribute types.
        var attributes = new Dictionary<string, object?>
        {
            ["decimal_value"] = 12.345678901234567890123456789m,
            ["float_value"] = 1.25f,
            ["guid_value"] = new Guid("01234567-89ab-cdef-0123-456789abcdef"),
            ["bytes_value"] = new byte[] { 0, 1, 254, 255 },
            ["date_value"] = new DateOnly(2026, 9, 8),
            ["__tenant_id"] = "hidden-marker"
        };
        _reader.QueryAsync(WebAppFixture.TestLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, [Feature.Create(1, null, attributes.ToImmutableDictionary())]));

        var service = WebAppFixture.TestServiceId;
        var layer = WebAppFixture.TestLayerId;
        var url = route == "restful"
            ? $"/rest/services/{service}/MapServer/WMTS/{layer}/default/WebMercatorQuad/0/0/0/99/41.json"
            : (route == "ogc" ? $"/ogc/services/{service}/wmts" : $"/rest/services/{service}/MapServer/WMTS")
                + $"?SERVICE=WMTS&REQUEST=GetFeatureInfo&VERSION=1.0.0&LAYER={layer}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0&I=41&J=99&INFOFORMAT=application/json";

        using var response = await _fixture.Client.GetAsync(url);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        content.Should().NotContain("__tenant_id").And.NotContain("hidden-marker");
        using var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().ContainSingle();
        var feature = features[0];
        feature.GetProperty("type").GetString().Should().Be("Feature");
        feature.GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Null);
        var properties = feature.GetProperty("properties");
        properties.GetProperty("decimal_value").GetDecimal().Should().Be(12.345678901234567890123456789m);
        properties.GetProperty("float_value").GetSingle().Should().Be(1.25f);
        properties.GetProperty("guid_value").GetString().Should().Be("01234567-89ab-cdef-0123-456789abcdef");
        properties.GetProperty("bytes_value").GetString().Should().Be("AAH+/w==");
        properties.GetProperty("date_value").GetString().Should().Be("2026-09-08");
        properties.GetRawText().Should().Be(feature.GetProperty("attributes").GetRawText());
        await _reader.Received(1).QueryAsync(WebAppFixture.TestLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }
}

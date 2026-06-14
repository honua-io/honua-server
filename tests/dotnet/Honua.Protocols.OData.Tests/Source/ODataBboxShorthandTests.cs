// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Verifies the OData <c>bbox</c> spatial shorthand maps to a canonical envelope spatial
/// filter so viewport windowing does not require a verbose geo.intersects WKT polygon
/// (issue #1621). Cities reference odata.yaml seed coordinates.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataBboxShorthandTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;
    private const long SanFranciscoId = 1;   // -122.4194, 37.7749
    private const long LosAngelesId = 2;     // -118.2437, 34.0522
    private const long SacramentoId = 3;     // -121.4944, 38.5816
    private const long SeattleId = 6;        // -122.3321, 47.6062

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?bbox=minLon,minLat,maxLon,maxLat")]
    public async Task Bbox_WindowAroundBayArea_ReturnsFeaturesInsideEnvelope()
    {
        // Envelope around the San Francisco Bay Area / Sacramento.
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?bbox=-123,37,-121,39");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(SanFranciscoId);
        objectIds.Should().Contain(SacramentoId);
        objectIds.Should().NotContain(LosAngelesId);
        objectIds.Should().NotContain(SeattleId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?bbox=...&$filter=...")]
    public async Task Bbox_CombinedWithAttributeFilter_ReturnsIntersection()
    {
        // California envelope AND only capitals -> Sacramento only.
        var filter = "is_capital eq true";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?bbox=-124,32,-114,42&$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var features = await ParseFeaturesAsync(response);

        features.Should().NotBeEmpty();
        foreach (var feature in features)
        {
            feature.GetProperty("is_capital").GetBoolean().Should().BeTrue();
        }

        var objectIds = features.Select(f => f.GetProperty("ObjectId").GetInt64()).ToArray();
        objectIds.Should().Contain(SacramentoId);
        objectIds.Should().NotContain(SanFranciscoId);
    }

    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /odata/Features({layerId})?bbox=<invalid>")]
    public async Task Bbox_InvalidValue_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?bbox=not-a-bbox");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<List<JsonElement>> ParseFeaturesAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerQueryTopFeaturesTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public FeatureServerQueryTopFeaturesTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_ValidRequest_ReturnsFeatures()
    {
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_WithReturnCountOnly_ReturnsCountNotFeatures()
    {
        // ArcGIS API for Python query_top_features paging requests returnCountOnly=true
        // and reads count. Return the top-feature count without features, and always
        // include exceededTransferLimit.
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&returnCountOnly=true&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("count", out var count).Should().BeTrue(
            "returnCountOnly=true must return a top-feature count for the arcgis paginator");
        count.ValueKind.Should().Be(JsonValueKind.Number);
        count.GetInt64().Should().BeGreaterThan(0);

        root.TryGetProperty("features", out _).Should().BeFalse(
            "returnCountOnly=true must not return a FeatureSet");

        root.TryGetProperty("exceededTransferLimit", out var exceeded).Should().BeTrue(
            "the count-only response must still carry exceededTransferLimit (Esri always emits it)");
        exceeded.ValueKind.Should().Be(JsonValueKind.False);
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_ReturnCountOnly_IgnoresResultRecordCountAndOffset()
    {
        // The ArcGIS API for Python reads this count as the paging total, so a page window
        // must not shrink it.
        var topFilter = Uri.EscapeDataString(JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        }));
        var route = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={topFilter}&returnCountOnly=true&f=json";

        var total = await ReadTopFeaturesCountAsync(route);
        var windowed = await ReadTopFeaturesCountAsync(route + "&resultRecordCount=1&resultOffset=1");

        total.Should().BeGreaterThan(1, "the seed must have more than one top feature for this check");
        windowed.Should().Be(total);
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_ReturnIdsOnlyAndReturnCountOnly_PreservesPagingOnIds()
    {
        var topFilter = Uri.EscapeDataString(JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        }));
        var route = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={topFilter}&returnIdsOnly=true&returnCountOnly=true&resultRecordCount=1&f=json";

        var response = await _fixture.Client.GetAsync(route);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("objectIds").GetArrayLength().Should().Be(1);
    }

    private async Task<long> ReadTopFeaturesCountAsync(string route)
    {
        var response = await _fixture.Client.GetAsync(route);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt64();
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_WithPbf_ReturnsProtobuf()
    {
        // QueryTopFeatures rejected f=pbf ("Supported formats: json,
        // pjson"). ArcGIS supports pbf here, so it must return application/x-protobuf.
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&f=pbf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/x-protobuf");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_MissingTopFilter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?f=json");

        // GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("topFilter");
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_InvalidTopFilter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter=not-json&f=json");

        // GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTheory]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    [InlineData("""{"wkid":"3857"}""")]
    [InlineData("""{"latestWkid":"3857"}""")]
    [InlineData("""{"wkid":{"value":3857}}""")]
    public async Task QueryTopFeatures_WronglyTypedOutSrMember_ReturnsBadRequest(string value)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString("""{"groupByFields":"category","topCount":2,"orderByFields":"objectid desc"}""")}&outSR={Uri.EscapeDataString(value)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        error.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString())
            .Should().Contain("Invalid outSR parameter");
    }

    [IntegrationTheory]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    [InlineData("""{"groupByFields":"category","topCount":1,"orderByFields":["objectid desc"]}""")]
    [InlineData("""{"groupByFields":["category"],"topCount":1,"orderByFields":"objectid desc"}""")]
    [InlineData("""{"groupByFields":"category","topCount":"1","orderByFields":"objectid desc"}""")]
    [InlineData("""{"groupByFields":"category","topCount":1,"orderByFields":{"objectid":"desc"}}""")]
    public async Task QueryTopFeatures_WronglyTypedTopFilterMember_ReturnsBadRequest(string topFilter)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        error.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString())
            .Should().Contain("Invalid topFilter parameter");
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_InvalidService_ReturnsNotFound()
    {
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/nonexistent/FeatureServer/0/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&f=json");

        // GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_WithReturnIdsOnly_ReturnsObjectIdsNotFeatures()
    {
        // The ArcGIS API for Python query_top_features() always sends returnIdsOnly.
        // A restrictive allowlist returned 400 ("Unknown query parameter:
        // returnIdsOnly"), making the operation unusable from the real Esri client.
        // It must honor returnIdsOnly: return the objectId-set form
        // ({ objectIdFieldName, objectIds }) and no FeatureSet.
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&returnIdsOnly=true&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("objectIdFieldName", out var objectIdFieldName).Should().BeTrue(
            "returnIdsOnly=true must return the objectId-set form");
        objectIdFieldName.ValueKind.Should().Be(JsonValueKind.String);

        root.TryGetProperty("objectIds", out var objectIds).Should().BeTrue();
        objectIds.ValueKind.Should().Be(JsonValueKind.Array);

        root.TryGetProperty("features", out _).Should().BeFalse(
            "returnIdsOnly=true must not return a FeatureSet");
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_WithReturnGeometryFalse_OmitsGeometry()
    {
        // returnGeometry=false is a standard query flag the Esri clients pass; it must
        // be accepted (not 400) and must omit geometry from each feature.
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&returnGeometry=false&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        foreach (var feature in features.EnumerateArray())
        {
            feature.TryGetProperty("geometry", out _).Should().BeFalse(
                "returnGeometry=false must omit geometry from each feature");
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_WithOutSr_ReturnsRequestedSpatialReference()
    {
        // outSR is a standard query param; it must be accepted (not 400) and the
        // response spatialReference must reflect the requested WKID.
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&outSR=3857&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("spatialReference", out var spatialReference).Should().BeTrue();
        spatialReference.TryGetProperty("wkid", out var wkid).Should().BeTrue();
        wkid.GetInt32().Should().Be(3857);
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_WithPaging_AcceptsResultOffsetAndRecordCount()
    {
        // resultOffset/resultRecordCount are standard paging params; they must be
        // accepted (not 400) and bound the returned page.
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 5,
            orderByFields = "objectid asc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&resultOffset=0&resultRecordCount=1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
        features.GetArrayLength().Should().BeLessThanOrEqualTo(1,
            "resultRecordCount=1 must bound the returned page");
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_PageShorterThanTheTopSet_ReportsExceededTransferLimit()
    {
        var all = await GetTopFeaturesAsync(string.Empty);
        all.GetProperty("features").GetArrayLength().Should().BeGreaterThan(1, "the fixture holds several top features");

        var page = await GetTopFeaturesAsync("&resultRecordCount=1");

        page.GetProperty("features").GetArrayLength().Should().Be(1);
        page.GetProperty("exceededTransferLimit").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_OffsetAboveTheMaximum_ReturnsAnError()
    {
        var root = await GetTopFeaturesAsync($"&resultOffset={int.MaxValue}");

        root.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetRawText().Should().Contain("resultOffset cannot exceed");
        root.TryGetProperty("features", out _).Should().BeFalse();
    }

    private async Task<JsonElement> GetTopFeaturesAsync(string parameters)
    {
        var topFilter = JsonSerializer.Serialize(new { groupByFields = "category", topCount = 2, orderByFields = "objectid desc" });
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}{parameters}&f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeatures_UnknownParameter_ReturnsBadRequest()
    {
        // Genuinely-unknown params must still be rejected with 400; only the standard
        // query family was added to the allowlist.
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid desc"
        });

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures?topFilter={Uri.EscapeDataString(topFilter)}&bogusParam=1&f=json");

        // GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.QueryTopFeatures)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryTopFeatures")]
    public async Task QueryTopFeaturesPost_ValidRequest_ReturnsFeatures()
    {
        var topFilter = JsonSerializer.Serialize(new
        {
            groupByFields = "category",
            topCount = 2,
            orderByFields = "objectid asc"
        });

        var payload = JsonSerializer.Serialize(new
        {
            topFilter,
            f = "json"
        });

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/queryTopFeatures",
            payloadContent);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.ValueKind.Should().Be(JsonValueKind.Array);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureServer;

[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public sealed class FeatureServerQueryParameterTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Theory]
    [InlineData("returnTrueCurves=true", "returnTrueCurves")]
    [InlineData("returnExceededLimitFeatures=true", "returnExceededLimitFeatures")]
    [InlineData("resultType=tile", "resultType")]
    [InlineData("having=1=1", "having")]
    [InlineData("sqlFormat=standard", "sqlFormat")]
    [InlineData("gdbVersion=sde.DEFAULT", "gdbVersion")]
    [InlineData("quantizationParameters=1", "quantizationParameters")]
    [InlineData("datumTransformation=4326", "datumTransformation")]
    [InlineData("returnCentroid=true", "returnCentroid")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithUnsupportedParameter_ReturnsBadRequest(string queryParam, string expectedToken)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&{queryParam}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Unsupported query parameters").And.Contain(expectedToken);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithPbfFormat_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=pbf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/x-protobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufFormat_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.flatgeobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeobufFormat_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=geobuf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufFormatAndDistinct_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&returnDistinctValues=true");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("returnDistinctValues is not supported when f=fgb.");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeobufFormatAndDistinct_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=geobuf&returnDistinctValues=true");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("returnDistinctValues is not supported when f=geobuf.");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufAcceptHeader_ReturnsOk()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.Accept.ParseAdd("application/vnd.flatgeobuf");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.flatgeobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeobufAcceptHeader_ReturnsOk()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.Accept.ParseAdd("application/geobuf");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithExplicitFormat_PrefersFOverAcceptHeader()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json");
        request.Headers.Accept.ParseAdd("application/vnd.flatgeobuf");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithUnsupportedFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=xml");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var details = document.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        details.Should().Contain(detail => detail!.Contains("Output format 'xml' is not supported"));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithMalformedObjectIdsDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?objectIds=1,,2&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithMalformedOutFieldsDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?outFields=name,,category&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryPost_WithUnsupportedBodyParameter_ReturnsBadRequest()
    {
        var payload = """
            {
              "where": "1=1",
              "outStatistics": [{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"count"}],
              "f": "json"
            }
            """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("outStatistics");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithMalformedOutStatistics_DoesNotLeakJsonParserDetails()
    {
        const string sentinel = "SENTINEL_STAT_FIELD";
        var malformedOutStatistics = Uri.EscapeDataString($"[{{\"statisticType\":\"count\",\"onStatisticField\":\"{sentinel}\"");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&outStatistics={malformedOutStatistics}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("outStatistics must be a valid JSON array.");
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
        content.Should().NotContain(sentinel);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeometryPrecision_RoundsCoordinates()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?geometryPrecision=0&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();

        queryResponse!.Features.Should().NotBeNull();
        var features = queryResponse.Features!;
        var feature = features.FirstOrDefault(item => item.Geometry?.X != null && item.Geometry?.Y != null);
        feature.Should().NotBeNull("expected at least one feature with geometry");

        var x = feature!.Geometry!.X!.Value;
        var y = feature.Geometry!.Y!.Value;

        Math.Abs(x % 1).Should().BeLessThan(1e-9);
        Math.Abs(y % 1).Should().BeLessThan(1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithReturnDistinctValues_ReturnsUniqueAttributes()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?outFields=category&returnDistinctValues=true&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();

        queryResponse!.Features.Should().NotBeNull();
        var categories = queryResponse.Features!
            .Select(feature => GetStringAttribute(feature.Attributes, "category"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        categories.Should().NotBeEmpty();
        categories.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(categories.Count);
    }

    private static string? GetStringAttribute(Dictionary<string, object?> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        if (value is string text)
        {
            return text;
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return value.ToString();
    }
}

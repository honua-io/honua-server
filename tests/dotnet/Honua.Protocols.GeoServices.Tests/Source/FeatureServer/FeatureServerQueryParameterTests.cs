// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerQueryParameterTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // Parameters that change result semantics are still rejected — silently ignoring
    // them would return output that differs from what the client asked for.
    [Theory]
    [InlineData("returnTrueCurves=true", "returnTrueCurves")]
    [InlineData("returnExceededLimitFeatures=true", "returnExceededLimitFeatures")]
    [InlineData("having=1=1", "having")]
    [InlineData("sqlFormat=standard", "sqlFormat")]
    [InlineData("returnCentroid=true", "returnCentroid")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithSemanticsChangingUnsupportedParameter_ReturnsBadRequest(string queryParam, string expectedToken)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&{queryParam}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Unsupported query parameters").And.Contain(expectedToken);
    }

    // ArcGIS Pro and the JS API send these parameters by default even when the
    // backing service doesn't honor them. Rejecting the request would break
    // out-of-the-box client connections for interop-compat reasons, so these
    // compatibility-oriented knobs are silently accepted (no-ops on the server).
    [Theory]
    [InlineData("gdbVersion=sde.DEFAULT")]
    [InlineData("quantizationParameters=1")]
    [InlineData("datumTransformation=4326")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithCompatibilityParameter_AcceptsRequest(string queryParam)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&{queryParam}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithTileResultType_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&where=1%3D1&resultType=tile");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
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
    public async Task Query_WithGeoParquetFormat_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        var payload = await response.Content.ReadAsByteArrayAsync();
        await AssertGeoParquetPayloadAsync(payload, expectGeometry: true);
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
    public async Task Query_WithGeoParquetFormatAndNon4326OutSR_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&outSR=3857");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoParquet output does not yet support non-4326 outSR");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatAndReturnM_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&returnM=true");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoParquet output does not support returnM=true");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoArrowFormatAndReturnM_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=arrow&returnM=true");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoArrow output does not support returnM=true");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoJsonFormatAndReturnM_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=geojson&returnM=true");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoJSON output does not support returnM=true");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatNoGeometryAndNon4326OutSR_ReturnsOk()
    {
        // When returnGeometry=false the parquet file has no geometry column or CRS metadata,
        // so the 4326 restriction should not apply.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&returnGeometry=false&outSR=3857");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatAndOutStatisticsAndNon4326OutSR_ReturnsJson()
    {
        // outStatistics queries always return JSON regardless of f=, so the parquet
        // CRS restriction must not reject the request.
        var outStats = Uri.EscapeDataString(
            """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"cnt"}]""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&outSR=3857&outStatistics={outStats}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatNoGeometry_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&returnGeometry=false");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        var payload = await response.Content.ReadAsByteArrayAsync();
        await AssertGeoParquetPayloadAsync(payload, expectGeometry: false);
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
    public async Task Query_WithGeoParquetAcceptHeader_ReturnsOk()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.Accept.ParseAdd("application/vnd.apache.parquet");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        var payload = await response.Content.ReadAsByteArrayAsync();
        await AssertGeoParquetPayloadAsync(payload, expectGeometry: true);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithAcceptHeaderQuality_PrefersHighestWeightedFormat()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.TryAddWithoutValidation("Accept", "application/json;q=0.1, application/geo+json;q=0.9");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithAcceptHeaderQuality_IgnoresZeroWeightedFormats()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.flatgeobuf;q=0, application/json;q=0.5");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
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
    public async Task Query_WithPjsonFormat_ReturnsCompactJson()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=pjson");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        content.Should().NotContain("\n");
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
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryPost_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var payload = """
            {
              "where": "1=1",
              "f": "json"
            }
            """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query",
            new StringContent(payload, Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Unsupported Media Type");
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

    private static async Task AssertGeoParquetPayloadAsync(byte[] payload, bool expectGeometry)
    {
        payload.Should().NotBeEmpty();
        payload.Length.Should().BeGreaterThanOrEqualTo(4);
        Encoding.ASCII.GetString(payload, 0, 4).Should().Be("PAR1");

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        await batchReader.ReadNextRecordBatchAsync(); // materialize schema

        var fieldNames = reader.Schema.FieldsList.Select(f => f.Name).ToArray();
        fieldNames.Should().OnlyHaveUniqueItems();
        fieldNames.Should().Contain("objectid");

        if (expectGeometry)
        {
            fieldNames.Should().Contain("geometry");
            reader.Schema.Metadata.Should().ContainKey("geo");

            using var metadataDocument = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
            metadataDocument.RootElement.GetProperty("primary_column").GetString().Should().Be("geometry");
        }
        else
        {
            fieldNames.Should().NotContain("geometry");
            var hasGeoKey = reader.Schema.Metadata?.ContainsKey("geo") ?? false;
            hasGeoKey.Should().BeFalse();
        }
    }
}

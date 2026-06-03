// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Xunit.Abstractions;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for FeatureServer metadata endpoints.
/// Tests Issue #5 - Layer metadata endpoint implementation.
/// </summary>
/// <summary>
/// Integration tests for streaming query functionality (Issue #229)
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class StreamingFeatureServerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _webAppFixture = new();
    private readonly ITestOutputHelper _output;

    public StreamingFeatureServerEndpointTests(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public async Task InitializeAsync()
    {
        await _webAppFixture.InitializeAsync();

        // Ensure we have a large dataset for streaming tests
        await _webAppFixture.EnsureLargeTestDatasetAsync();
    }

    public async Task DisposeAsync()
    {
        await _webAppFixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_LargeResultSet_UsesStreamingResponse()
    {
        // Arrange
        using var client = _webAppFixture.CreateClient();

        // Query for a large result set (>1000 features to trigger streaming)
        var queryParams = new Dictionary<string, string?>
        {
            ["where"] = "1=1", // Get all features
            ["resultRecordCount"] = "2000", // Request large batch
            ["f"] = "json"
        };

        var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value ?? "")}"));
        var requestUri = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?{queryString}";

        // Act
        var response = await client.GetAsync(requestUri);

        // Assert
        response.EnsureSuccessStatusCode();

        Assert.False(
            response.Headers.TransferEncodingChunked ?? false,
            "FeatureServer streaming should not set Transfer-Encoding manually; the server transport owns response framing.");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        Assert.NotNull(queryResponse);
        Assert.True(queryResponse.Features?.Length > 0, "Should return features");

        _output.WriteLine($"Streamed {queryResponse.Features?.Length} features");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_LargeResultSet_GeoJsonFormat_UsesStreamingResponse()
    {
        // Arrange
        using var client = _webAppFixture.CreateClient();

        // Query for a large result set in GeoJSON format
        var queryParams = new Dictionary<string, string?>
        {
            ["where"] = "1=1",
            ["resultRecordCount"] = "1500",
            ["f"] = "geojson"
        };

        var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value ?? "")}"));
        var requestUri = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?{queryString}";

        // Act
        var response = await client.GetAsync(requestUri);

        // Assert
        response.EnsureSuccessStatusCode();

        // Verify content type for GeoJSON
        Assert.Equal("application/geo+json", response.Content.Headers.ContentType?.MediaType);

        var content = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        Assert.NotNull(geoJsonResponse);
        Assert.Equal("FeatureCollection", geoJsonResponse.Type);
        Assert.True(geoJsonResponse.Features?.Length > 0, "Should return features in GeoJSON format");

        _output.WriteLine($"Streamed {geoJsonResponse.Features?.Length} features in GeoJSON format");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_SmallResultSet_DoesNotUseStreaming()
    {
        // Arrange
        using var client = _webAppFixture.CreateClient();

        // Query for a small result set (should not trigger streaming)
        var queryParams = new Dictionary<string, string?>
        {
            ["where"] = "1=1",
            ["resultRecordCount"] = "10", // Small batch
            ["f"] = "json"
        };

        var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value ?? "")}"));
        var requestUri = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?{queryString}";

        // Act
        var response = await client.GetAsync(requestUri);

        // Assert
        response.EnsureSuccessStatusCode();

        Assert.False(
            response.Headers.TransferEncodingChunked ?? false,
            "Small result sets should not set Transfer-Encoding manually.");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        Assert.NotNull(queryResponse);
        Assert.True(queryResponse.Features?.Length <= 10, "Should return limited features");

        _output.WriteLine($"Non-streaming response for {queryResponse.Features?.Length} features");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_ReturnIdsOnly_UsesStreaming()
    {
        // Arrange
        using var client = _webAppFixture.CreateClient();

        // Query for IDs only with large result set
        var queryParams = new Dictionary<string, string?>
        {
            ["where"] = "1=1",
            ["returnIdsOnly"] = "true",
            ["resultRecordCount"] = "2000",
            ["f"] = "json"
        };

        var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value ?? "")}"));
        var requestUri = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?{queryString}";

        // Act
        var response = await client.GetAsync(requestUri);

        // Assert
        response.EnsureSuccessStatusCode();

        Assert.False(
            response.Headers.TransferEncodingChunked ?? false,
            "FeatureServer IDs-only streaming should not set Transfer-Encoding manually; the server transport owns response framing.");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);

        Assert.NotNull(queryResponse);
        Assert.True(queryResponse.ObjectIds?.Length > 0, "Should return object IDs");
        queryResponse.Features.Should().BeNull();

        using var jsonDoc = JsonDocument.Parse(content);
        Assert.False(jsonDoc.RootElement.TryGetProperty("features", out _));

        _output.WriteLine($"Streamed {queryResponse.ObjectIds?.Length} object IDs");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_POST_LargeResultSet_UsesStreamingResponse()
    {
        // Arrange
        using var client = _webAppFixture.CreateClient();

        var queryParams = new QueryParameters
        {
            Where = "1=1",
            ResultRecordCount = 1500,
            F = "json"
        };

        var requestUri = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query";
        var json = JsonSerializer.Serialize(queryParams, FeatureServerJsonContext.Default.QueryParameters);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync(requestUri, content);

        // Assert
        response.EnsureSuccessStatusCode();

        Assert.False(
            response.Headers.TransferEncodingChunked ?? false,
            "FeatureServer POST streaming should not set Transfer-Encoding manually; the server transport owns response framing.");

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(responseContent, FeatureServerJsonContext.Default.QueryResponse);

        Assert.NotNull(queryResponse);
        Assert.True(queryResponse.Features?.Length > 0, "Should return features via POST");

        _output.WriteLine($"Streamed {queryResponse.Features?.Length} features via POST request");
    }
}

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_WithValidServiceId_ReturnsServiceInfo()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();

        // Deserialize and validate response structure
        var serviceResponse = JsonSerializer.Deserialize<FeatureServerResponse>(
            content, FeatureServerJsonContext.Default.FeatureServerResponse);
        serviceResponse.Should().NotBeNull();

        // Validate required properties
        serviceResponse!.ServiceName.Should().Be(TestServiceId);
        serviceResponse.ServiceDescription.Should().NotBeNullOrEmpty();
        serviceResponse.CurrentVersion.Should().BeGreaterThan(0);
        serviceResponse.SpatialReference.Should().NotBeNull();
        serviceResponse.SpatialReference.Wkid.Should().BeGreaterThan(0);
        serviceResponse.Layers.Should().NotBeNull();
        serviceResponse.MaxRecordCount.Should().BeGreaterThan(0);
        serviceResponse.SupportedQueryFormats.Should().NotBeEmpty();
        serviceResponse.SupportedQueryFormats.Should().Contain("PBF");
        serviceResponse.SupportedQueryFormats.Should().Contain("FGB");
        serviceResponse.SupportedQueryFormats.Should().Contain("GEOBUF");
        serviceResponse.Capabilities.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_WithNonExistentService_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/rest/services/nonexistent/FeatureServer");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("code").GetInt32().Should().Be(404);
        errorElement.GetProperty("message").GetString().Should().Be("Not Found");
        errorElement.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString() ?? string.Empty)
            .Should().Contain(detail => detail.Contains("Service 'nonexistent' not found"));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_WithUnsupportedFormat_Returns400()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer?f=html");

        response.Be400BadRequest();
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var details = document.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray()
            .Select(detail => detail.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value));
        details.Should().Contain(detail => detail!.Contains("Output format 'html' is not supported"));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithValidLayerId_ReturnsLayerInfo()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();

        // Deserialize and validate response structure
        var layerResponse = JsonSerializer.Deserialize<LayerResponse>(
            content, FeatureServerJsonContext.Default.LayerResponse);
        layerResponse.Should().NotBeNull();

        // Validate required properties
        layerResponse!.Id.Should().Be(TestLayerId);
        layerResponse.Name.Should().NotBeNullOrEmpty();
        layerResponse.Type.Should().Be("Feature Layer");
        layerResponse.CurrentVersion.Should().BeGreaterThan(0);
        layerResponse.GeometryType.Should().NotBeNullOrEmpty();
        layerResponse.SpatialReference.Should().NotBeNull();
        layerResponse.SpatialReference.Wkid.Should().BeGreaterThan(0);
        layerResponse.Fields.Should().NotBeEmpty();
        layerResponse.ObjectIdField.Should().NotBeNullOrEmpty();
        layerResponse.MaxRecordCount.Should().BeGreaterThan(0);
        layerResponse.SupportedQueryFormats.Should().NotBeEmpty();
        layerResponse.SupportedQueryFormats.Should().Contain("PBF");
        layerResponse.SupportedQueryFormats.Should().Contain("FGB");
        layerResponse.SupportedQueryFormats.Should().Contain("GEOBUF");
        layerResponse.Capabilities.Should().NotBeNullOrEmpty();
        layerResponse.AdvancedQueryCapabilities.Should().NotBeNull();
        layerResponse.AdvancedQueryCapabilities!.SupportsPagination.Should().Be(layerResponse.SupportsPagination);
        layerResponse.AdvancedQueryCapabilities.SupportsOrderBy.Should().Be(layerResponse.SupportsOrderBy);
        layerResponse.AdvancedQueryCapabilities.SupportsDistinct.Should().Be(layerResponse.SupportsDistinct);
        layerResponse.AdvancedQueryCapabilities.SupportsStatistics.Should().Be(layerResponse.SupportsStatistics);

        // Validate fields structure
        foreach (var field in layerResponse.Fields)
        {
            field.Name.Should().NotBeNullOrEmpty();
            field.Type.Should().NotBeNullOrEmpty();
            field.Alias.Should().NotBeNullOrEmpty();
        }

        // Should have at least an object ID field
        layerResponse.Fields.Should().Contain(f =>
            f.Name.Equals(layerResponse.ObjectIdField, StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithSubtypes_ReturnsSubtypeFieldAndSubtypes()
    {
        // honua-server#1378 (#1254): an Esri subtype set carried on the canonical
        // resource must be served on the FeatureServer layer metadata as
        // subtypeField / subtypes / defaultSubtypeCode, reusing the shared domain mapper
        // for per-subtype field domains.

        // Arrange: declare an integer subtype field on the served resource, then attach
        // a subtype set referencing it (one subtype carries a default value + a domain).
        var subtypeField = new MetadataV2Field
        {
            Name = "buildingtype",
            Type = MetadataV2FieldType.Integer,
            Title = "Building Type",
            Alias = "Building Type",
            Nullable = true,
        };
        _fixture.UpdateV2ResourceSchemaField(TestLayerId, subtypeField);

        var statusOverride = new MetadataV2SubtypeFieldOverride
        {
            DefaultValue = JsonSerializer.SerializeToElement("occupied"),
            Domain = new MetadataV2FieldDomain
            {
                Name = "OccupancyDomain",
                Type = "codedValue",
                CodedValues =
                [
                    new MetadataV2CodedValue { Code = JsonSerializer.SerializeToElement("occupied"), Name = "Occupied" },
                    new MetadataV2CodedValue { Code = JsonSerializer.SerializeToElement("vacant"), Name = "Vacant" },
                ],
            },
        };

        var subtypes = new MetadataV2Subtypes
        {
            SubtypeField = "buildingtype",
            DefaultSubtypeCode = JsonSerializer.SerializeToElement(1),
            Subtypes =
            [
                new MetadataV2Subtype { Code = JsonSerializer.SerializeToElement(1), Name = "Commercial" },
                new MetadataV2Subtype
                {
                    Code = JsonSerializer.SerializeToElement(2),
                    Name = "Residential",
                    FieldOverrides = new Dictionary<string, MetadataV2SubtypeFieldOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["status"] = statusOverride,
                    },
                },
            ],
        };

        try
        {
            _fixture.UpdateV2ResourceSubtypes(TestLayerId, subtypes);

            // Act
            var layerResponse = await GetLayerMetadataAsync();

            // Assert
            layerResponse.SubtypeField.Should().Be("buildingtype");
            layerResponse.DefaultSubtypeCode.Should().NotBeNull();
            layerResponse.DefaultSubtypeCode!.Value.GetInt32().Should().Be(1);
            layerResponse.Subtypes.Should().NotBeNull();
            layerResponse.Subtypes!.Select(s => s.Name)
                .Should().BeEquivalentTo(["Commercial", "Residential"]);

            var residential = layerResponse.Subtypes.Single(s => s.Name == "Residential");
            residential.Code.GetInt32().Should().Be(2);
            residential.DefaultValues.Should().NotBeNull();
            residential.DefaultValues!["status"].GetString().Should().Be("occupied");
            residential.Domains.Should().NotBeNull();
            residential.Domains!["status"].Type.Should().Be("codedValue");
            residential.Domains["status"].CodedValues.Should().NotBeNull();
            residential.Domains["status"].CodedValues!.Select(v => v.Name)
                .Should().BeEquivalentTo(["Occupied", "Vacant"]);
        }
        finally
        {
            // Restore the seeded graph so sibling tests in the collection are unaffected.
            _fixture.UpdateV2ResourceSubtypes(TestLayerId, null);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_ObjectIdField_IsTypedEsriFieldTypeOid()
    {
        // Regression for #1299: Esri clients (including the ArcGIS Python SDK) locate the
        // object-id field by filtering fields for type == "esriFieldTypeOID". The field that
        // matches objectIdField must therefore be typed esriFieldTypeOID, and there must be
        // exactly one such field per layer.

        // Act
        var layerResponse = await GetLayerMetadataAsync();

        // Assert
        layerResponse.ObjectIdField.Should().NotBeNullOrEmpty();

        var oidFields = layerResponse.Fields
            .Where(f => f.Type.Equals("esriFieldTypeOID", StringComparison.Ordinal))
            .ToList();

        oidFields.Should().ContainSingle("there must be exactly one esriFieldTypeOID field per layer");
        oidFields[0].Name.Should().Be(layerResponse.ObjectIdField);

        // No field named like the object-id field should leak a non-OID Esri type.
        layerResponse.Fields
            .Where(f => f.Name.Equals(layerResponse.ObjectIdField, StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(f => f.Type == "esriFieldTypeOID");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_ObjectIdField_IsTypedEsriFieldTypeOid()
    {
        // Regression for #1299: the query response fields[] array must also type the
        // object-id field as esriFieldTypeOID so feature-returning queries resolve the OID.

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=1%3D1&f=json");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.ObjectIdFieldName.Should().NotBeNullOrEmpty();
        queryResponse.Fields.Should().NotBeNullOrEmpty();

        var oidFields = queryResponse.Fields!
            .Where(f => f.Type.Equals("esriFieldTypeOID", StringComparison.Ordinal))
            .ToList();

        oidFields.Should().ContainSingle("the query response must expose exactly one esriFieldTypeOID field");
        oidFields[0].Name.Should().Be(queryResponse.ObjectIdFieldName);
    }

    private async Task<LayerResponse> GetLayerMetadataAsync()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var layerResponse = JsonSerializer.Deserialize<LayerResponse>(
            content, FeatureServerJsonContext.Default.LayerResponse);
        layerResponse.Should().NotBeNull();
        return layerResponse!;
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithUnsupportedFormat_Returns400()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}?f=html");

        response.Be400BadRequest();
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var details = document.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray()
            .Select(detail => detail.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value));
        details.Should().Contain(detail => detail!.Contains("Output format 'html' is not supported"));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_IncludesDrawingInfo()
    {
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var layerResponse = JsonSerializer.Deserialize<LayerResponse>(
            content, FeatureServerJsonContext.Default.LayerResponse);

        layerResponse.Should().NotBeNull();
        layerResponse!.DrawingInfo.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithoutSavedStyle_SynthesizesDefaultRenderer()
    {
        // Only layer 0 carries a saved esri-drawing-info style in the fixture; layer 1
        // has none. Esri FeatureServers always return drawingInfo for renderable layers,
        // so the server must synthesize a default simple renderer from the geometry type
        // rather than omit drawingInfo (which leaves Esri clients with no symbology).
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/1");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        root.TryGetProperty("drawingInfo", out var drawingInfo)
            .Should().BeTrue("a style-less renderable layer must still expose a default drawingInfo");
        drawingInfo.ValueKind.Should().Be(JsonValueKind.Object);

        var renderer = drawingInfo.GetProperty("renderer");
        renderer.GetProperty("type").GetString().Should().Be("simple");
        renderer.TryGetProperty("symbol", out var symbol)
            .Should().BeTrue("the default simple renderer must carry a symbol");
        symbol.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithNonExistentService_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/nonexistent/FeatureServer/{TestLayerId}");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("code").GetInt32().Should().Be(404);
        errorElement.GetProperty("message").GetString().Should().Be("Not Found");
        errorElement.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString() ?? string.Empty)
            .Should().Contain(detail => detail.Contains("Service 'nonexistent' not found"));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithNonExistentLayer_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/999");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("message").GetString().Should().Be("Not Found");
        errorElement.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString() ?? string.Empty)
            .Should().Contain(detail => detail.Contains("Layer 999 not found in service"));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithInvalidLayerId_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/invalid");

        // Assert - Should return 404 because 'invalid' doesn't match int route constraint
        response.HaveStatusCode(System.Net.HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_WithWrongHttpMethod_Returns405()
    {
        // Service metadata answers GET and POST (Esri clients hydrate via POST);
        // a genuinely unsupported method (DELETE) must still return 405.
        var response = await _fixture.Client.DeleteAsync($"/rest/services/{TestServiceId}/FeatureServer");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_WithWrongHttpMethod_Returns405()
    {
        // Layer metadata answers GET and POST; DELETE remains unsupported -> 405.
        var response = await _fixture.Client.DeleteAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task GetServiceMetadata_ResponseValidatesAgainstGeoServicesSchema()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var serviceResponse = JsonSerializer.Deserialize<FeatureServerResponse>(
            content, FeatureServerJsonContext.Default.FeatureServerResponse);

        // Validate GeoServices REST JSON schema compliance
        serviceResponse.Should().NotBeNull();

        // Required GeoServices REST service properties
        serviceResponse!.CurrentVersion.Should().BeGreaterThan(0);
        serviceResponse.ServiceName.Should().NotBeNullOrEmpty();
        serviceResponse.ServiceDescription.Should().NotBeNullOrEmpty();
        serviceResponse.Layers.Should().NotBeNull();
        serviceResponse.Tables.Should().NotBeNull();
        serviceResponse.SpatialReference.Should().NotBeNull();
        serviceResponse.Units.Should().NotBeNullOrEmpty();
        serviceResponse.SupportedQueryFormats.Should().NotBeNull();
        serviceResponse.Capabilities.Should().NotBeNullOrEmpty();

        // Validate spatial reference structure
        serviceResponse.SpatialReference.Wkid.Should().BeGreaterThan(0);

        // Validate at least basic capabilities are present
        var capabilities = serviceResponse.Capabilities.Split(',');
        capabilities.Should().Contain("Query");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task GetLayerMetadata_ResponseValidatesAgainstGeoServicesSchema()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var layerResponse = JsonSerializer.Deserialize<LayerResponse>(
            content, FeatureServerJsonContext.Default.LayerResponse);

        // Validate GeoServices REST JSON schema compliance for layer metadata
        layerResponse.Should().NotBeNull();

        // Required GeoServices REST layer properties
        layerResponse!.CurrentVersion.Should().BeGreaterThan(0);
        layerResponse.Id.Should().BeGreaterThanOrEqualTo(0);
        layerResponse.Name.Should().NotBeNullOrEmpty();
        layerResponse.Type.Should().Be("Feature Layer");
        layerResponse.GeometryType.Should().NotBeNullOrEmpty();
        layerResponse.SpatialReference.Should().NotBeNull();
        layerResponse.Fields.Should().NotBeNull().And.NotBeEmpty();
        layerResponse.ObjectIdField.Should().NotBeNullOrEmpty();
        layerResponse.Capabilities.Should().NotBeNullOrEmpty();

        // Validate geometry type format
        layerResponse.GeometryType.Should().StartWith("esriGeometry");

        // Validate field structure
        foreach (var field in layerResponse.Fields)
        {
            field.Name.Should().NotBeNullOrEmpty();
            field.Type.Should().NotBeNullOrEmpty().And.StartWith("esriFieldType");
            field.Alias.Should().NotBeNullOrEmpty();
        }

        // Validate at least basic capabilities are present
        var capabilities = layerResponse.Capabilities.Split(',');
        capabilities.Should().Contain("Query");
    }

    // Query endpoint tests (Issue #6)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithValidWhereClause_ReturnsFilteredFeatures()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=name='Test Feature'");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();

        // Deserialize and validate response structure
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.ObjectIdFieldName.Should().NotBeNullOrEmpty();
        queryResponse.Fields.Should().NotBeNullOrEmpty();
        queryResponse.DisplayFieldName.Should().NotBeNullOrWhiteSpace();
        queryResponse.HasZ.Should().BeFalse();
        queryResponse.HasM.Should().BeFalse();
    }

    // Regression: returnDistinctValues=true with a paramless WHERE clause (the canonical
    // ArcGIS "where=1=1" select-all) plus a named outFields previously returned HTTP 500
    // ("Parameter '' cannot be null") because the unlimited count path mis-bound parameters.
    // See issue #1280 — ArcGIS Pro's Unique Values renderer relies on this exact request.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_ReturnDistinctValuesWithTautologicalWhere_Returns200WithDistinctValues()
    {
        // Act: where=1=1 produces no bound parameters; category has duplicate values across rows.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=1%3D1&outFields=category&returnDistinctValues=true&returnGeometry=false&f=json");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();

        // Seed layer 0 has categories test/sample/test/sample/test -> two distinct values.
        var distinctCategories = queryResponse.Features!
            .Select(feature => feature.Attributes.TryGetValue("category", out var value) ? value?.ToString() : null)
            .Where(value => value is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        queryResponse.Features!.Length.Should().Be(distinctCategories.Length);
        distinctCategories.Should().HaveCount(2);
        distinctCategories.Should().Contain("test");
        distinctCategories.Should().Contain("sample");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_ReturnDistinctValuesWithContradictoryWhere_Returns200Empty()
    {
        // Act: where=1=2 is also paramless and matches nothing.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=1%3D2&outFields=category&returnDistinctValues=true&returnGeometry=false&f=json");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features!.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_ReturnDistinctValuesWithRealPredicate_Returns200WithDistinctValues()
    {
        // Act: a real bound predicate exercises the parameterized path alongside distinct.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=category='test'&outFields=category&returnDistinctValues=true&returnGeometry=false&f=json");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features!.Length.Should().Be(1);
        queryResponse.Features![0].Attributes.TryGetValue("category", out var category).Should().BeTrue();
        category?.ToString().Should().Be("test");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_ReturnDistinctValuesWithAllFields_Returns200()
    {
        // Act: outFields=* does not apply distinct, but must still succeed with where=1=1.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=1%3D1&outFields=*&returnDistinctValues=true&returnGeometry=false&f=json");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features!.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithPostRequest_ReturnsFilteredFeatures()
    {
        // Arrange
        var json = """
            {
                "where": "name='Test Feature'",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().NotBeNullOrEmpty();

        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/query")]
    public async Task ServiceQueryFeatures_GetWithLayerId_ReturnsFilteredFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/query?layerId={TestLayerId}&where=1%3D1&f=json");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<ServiceQueryResponse>(
            content,
            FeatureServerJsonContext.Default.ServiceQueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Layers.Should().ContainSingle(layer => layer.Id == TestLayerId);
        queryResponse.Layers.Single(layer => layer.Id == TestLayerId).Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostRequest_HonorsQueryStringParameters()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("where", "1=1"),
            new KeyValuePair<string, string>("f", "json")
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?returnGeometry=false",
            payload);

        response.Be200Ok();
        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent,
            FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().AllSatisfy(feature => feature.Geometry.Should().BeNull());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/query")]
    public async Task ServiceQueryFeatures_GetWithMalformedLayersDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/query?layers={TestLayerId},&where=1%3D1&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnCountOnly=true")]
    public async Task QueryFeatures_WithReturnCountOnly_ReturnsCount()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?returnCountOnly=true");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Count.Should().Be(5);
        queryResponse.ObjectIds.Should().BeNull();
        queryResponse.Extent.Should().BeNull();
        queryResponse.Features.Should().BeNull();
        queryResponse.ObjectIdFieldName.Should().BeNull();

        using var jsonDoc = JsonDocument.Parse(content);
        jsonDoc.RootElement.TryGetProperty("features", out _).Should().BeFalse();
        // Esri's query contract always emits exceededTransferLimit (including false).
        jsonDoc.RootElement.TryGetProperty("exceededTransferLimit", out var countExceeded).Should().BeTrue();
        countExceeded.GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnIdsOnly=true")]
    public async Task QueryFeatures_WithReturnIdsOnly_ReturnsIds()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?returnIdsOnly=true");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.ObjectIds.Should().NotBeNull();
        queryResponse.ObjectIds!.Should().HaveCount(5);
        queryResponse.Count.Should().BeNull();
        queryResponse.Extent.Should().BeNull();
        queryResponse.Features.Should().BeNull();

        using var jsonDoc = JsonDocument.Parse(content);
        jsonDoc.RootElement.TryGetProperty("features", out _).Should().BeFalse();
        // returnIdsOnly returns the full id set (paged by offset), not a transfer-limited
        // FeatureSet, so Esri does not emit exceededTransferLimit on this shape.
        jsonDoc.RootElement.TryGetProperty("exceededTransferLimit", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnIdsOnly=true")]
    public async Task QueryFeatures_WithReturnIdsOnly_DoesNotIncludeExceededTransferLimit()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?returnIdsOnly=true&resultRecordCount=1");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.ObjectIds.Should().NotBeNull();
        queryResponse.ObjectIds!.Should().HaveCount(5);

        using var jsonDoc = JsonDocument.Parse(content);
        jsonDoc.RootElement.TryGetProperty("features", out _).Should().BeFalse();
        // returnIdsOnly is not a FeatureSet; exceededTransferLimit is not part of this shape.
        jsonDoc.RootElement.TryGetProperty("exceededTransferLimit", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnExtentOnly=true")]
    public async Task QueryFeatures_WithReturnExtentOnly_ReturnsExtent()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?returnExtentOnly=true");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Extent.Should().NotBeNull();
        queryResponse.Extent!.SpatialReference.Should().NotBeNull();
        queryResponse.ObjectIds.Should().BeNull();
        // Esri's returnExtentOnly response shape is {extent, count}; the handler reports the
        // matched-feature count alongside the extent, so count is populated (not null) here.
        queryResponse.Count.Should().Be(5);
        queryResponse.Features.Should().BeNull();
        queryResponse.ObjectIdFieldName.Should().BeNull();

        using var jsonDoc = JsonDocument.Parse(content);
        jsonDoc.RootElement.TryGetProperty("features", out _).Should().BeFalse();
        // Esri's query contract always emits exceededTransferLimit (including false).
        jsonDoc.RootElement.TryGetProperty("exceededTransferLimit", out var extentExceeded).Should().BeTrue();
        extentExceeded.GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    public async Task GenerateRenderer_WithUnsupportedClassificationType_ReturnsBadRequest()
    {
        var classificationDef = Uri.EscapeDataString("""{"type":"uniqueValue"}""");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/generateRenderer?classificationDef={classificationDef}");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest,
            "only classBreaksDef and uniqueValueDef classification types are supported");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("classificationDef");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    public async Task GenerateRenderer_WithClassBreaksDef_ReturnsClassBreaksRenderer()
    {
        var classificationDef = Uri.EscapeDataString(
            """{"type":"classBreaksDef","classificationField":"objectid","classificationMethod":"esriClassifyEqualInterval","breakCount":3}""");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/generateRenderer?classificationDef={classificationDef}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var jsonDoc = JsonDocument.Parse(content);
        var root = jsonDoc.RootElement;

        root.GetProperty("type").GetString().Should().Be("classBreaks");
        root.GetProperty("field").GetString().Should().Be("objectid");
        root.TryGetProperty("minValue", out _).Should().BeTrue();

        var classBreakInfos = root.GetProperty("classBreakInfos");
        classBreakInfos.ValueKind.Should().Be(JsonValueKind.Array);
        classBreakInfos.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var info in classBreakInfos.EnumerateArray())
        {
            info.TryGetProperty("classMaxValue", out _).Should().BeTrue();
            info.TryGetProperty("label", out _).Should().BeTrue();
            var symbol = info.GetProperty("symbol");
            symbol.ValueKind.Should().Be(JsonValueKind.Object);
            symbol.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        }
    }

    // Regression (#1426): ArcGIS API for Python POSTs generateRenderer (form body).
    // The POST companion must accept the body payload instead of returning 405.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    public async Task GenerateRenderer_PostWithClassBreaksDef_ReturnsClassBreaksRenderer()
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>(
                "classificationDef",
                """{"type":"classBreaksDef","classificationField":"objectid","classificationMethod":"esriClassifyEqualInterval","breakCount":3}"""),
            new KeyValuePair<string, string>("f", "json")
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/generateRenderer",
            form);

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var jsonDoc = JsonDocument.Parse(content);
        jsonDoc.RootElement.GetProperty("type").GetString().Should().Be("classBreaks");
        jsonDoc.RootElement.GetProperty("field").GetString().Should().Be("objectid");
    }

    // Regression (#1426): POST generateRenderer with no body returns the simple
    // renderer (mirrors the GET form) rather than 405.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    public async Task GenerateRenderer_PostWithoutClassificationDef_ReturnsSimpleRenderer()
    {
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/generateRenderer",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]));

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        using var jsonDoc = JsonDocument.Parse(content);
        jsonDoc.RootElement.GetProperty("type").GetString().Should().Be("simple");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    public async Task GenerateRenderer_WithClassBreaksDefQuantile_ReturnsClassBreaksRenderer()
    {
        var classificationDef = Uri.EscapeDataString(
            """{"type":"classBreaksDef","classificationField":"objectid","classificationMethod":"esriClassifyQuantile","breakCount":2}""");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/generateRenderer?classificationDef={classificationDef}");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var root = jsonDoc.RootElement;

        root.GetProperty("type").GetString().Should().Be("classBreaks");
        root.GetProperty("classificationMethod").GetString().Should().Be("esriClassifyQuantile");
        root.GetProperty("classBreakInfos").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    public async Task GenerateRenderer_WithUniqueValueDef_ReturnsUniqueValueRenderer()
    {
        var classificationDef = Uri.EscapeDataString(
            """{"type":"uniqueValueDef","uniqueValueFields":["category"],"fieldDelimiter":","}""");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/generateRenderer?classificationDef={classificationDef}");

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var root = jsonDoc.RootElement;

        root.GetProperty("type").GetString().Should().Be("uniqueValue");
        root.GetProperty("field1").GetString().Should().Be("category");
        root.GetProperty("fieldDelimiter").GetString().Should().Be(",");

        var uniqueValueInfos = root.GetProperty("uniqueValueInfos");
        uniqueValueInfos.ValueKind.Should().Be(JsonValueKind.Array);
        uniqueValueInfos.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var info in uniqueValueInfos.EnumerateArray())
        {
            info.GetProperty("value").GetString().Should().NotBeNull();
            info.TryGetProperty("label", out _).Should().BeTrue();
            var symbol = info.GetProperty("symbol");
            symbol.ValueKind.Should().Be(JsonValueKind.Object);
            symbol.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer")]
    public async Task GenerateRenderer_WithMalformedClassificationDef_DoesNotLeakJsonParserDetails()
    {
        var malformedClassificationDef = Uri.EscapeDataString("{\"type\":");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/generateRenderer?classificationDef={malformedClassificationDef}");

        response.HaveStatusCode(System.Net.HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("classificationDef must be valid JSON.");
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithSqlInjectionAttempt_Returns400()
    {
        // Act - Attempt SQL injection
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=name='Test'; DROP TABLE users; --");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("message").GetString().Should().Be("Bad Request");
        errorElement.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString() ?? string.Empty)
            .Should().Contain(detail =>
                detail.Contains("Invalid query parameters", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("SQL injection attempt detected", StringComparison.OrdinalIgnoreCase));
        errorElement.GetProperty("details").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithInvalidWhereClause_Returns400()
    {
        // Act - Invalid WHERE clause format
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=invalid syntax here");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("message").GetString().Should().Be("Bad Request");
        errorElement.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString() ?? string.Empty)
            .Should().Contain(detail => detail.Contains("Invalid query parameters"));
        errorElement.GetProperty("details").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?unexpected=1")]
    public async Task QueryFeatures_WithUnknownParameter_Returns400()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?unexpected=1");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("message").GetString().Should().Be("Bad Request");
        var hasUnknownParameter = false;
        foreach (var detail in errorElement.GetProperty("details").EnumerateArray())
        {
            if (detail.GetString() == "Unknown query parameter: unexpected")
            {
                hasUnknownParameter = true;
                break;
            }
        }

        hasUnknownParameter.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query?returnAllRecords=true")]
    public async Task QueryFeatures_WithArcGisClientParameters_ReturnsOk()
    {
        // ArcGIS Pro and the ArcGIS API for Python send these by default; they must
        // be accepted (and ignored where unsupported) rather than rejected (#1276).
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query" +
            "?where=1=1&outFields=*&returnAllRecords=true&multipatchOption=xyFootprint" +
            "&featureEncoding=esriDefault&datumTransformations=[]&f=json");

        response.HaveStatusCode(System.Net.HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithNonExistentService_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/nonexistent/FeatureServer/{TestLayerId}/query");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("code").GetInt32().Should().Be(404);
        errorElement.GetProperty("message").GetString().Should().Be("Not Found");
        errorElement.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString() ?? string.Empty)
            .Should().Contain(detail => detail.Contains("Service 'nonexistent' not found"));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithNonExistentLayer_Returns404()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/999/query");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.NotFound);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("message").GetString().Should().Be("Not Found");
        errorElement.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString() ?? string.Empty)
            .Should().Contain(detail => detail.Contains("Layer 999 not found in service"));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithOutFields_ReturnsOnlySpecifiedFields()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?outFields=objectid,name");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithReturnGeometryFalse_ReturnsNoGeometry()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?returnGeometry=false");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();

        // All features should have null geometry
        queryResponse.Features.Should().AllSatisfy(f => f.Geometry.Should().BeNull());
    }

    // ObjectIds parameter tests (Issue #156)

    /// <summary>
    /// Tests query with single objectId parameter returns only that feature
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithSingleObjectId_ReturnsOneFeature()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?objectIds=1");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(1);

        // Verify the returned feature has the correct objectId
        var feature = queryResponse.Features[0];
        var objectIdValue = feature.Attributes["objectid"];
        objectIdValue.Should().NotBeNull();
        ReadObjectIdValue(objectIdValue).Should().Be(1);
    }

    /// <summary>
    /// Tests query with multiple objectIds parameter returns multiple specific features
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithMultipleObjectIds_ReturnsSpecificFeatures()
    {
        // Act - Request features with objectIds 1, 3, 5
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?objectIds=1,3,5");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(3);

        // Verify the returned features have the correct objectIds
        var returnedObjectIds = queryResponse.Features
            .Select(f => ReadObjectIdValue(f.Attributes["objectid"]))
            .ToArray();
        returnedObjectIds.Should().Contain(new long[] { 1, 3, 5 });
    }

    /// <summary>
    /// Tests that objectIds queries are not truncated by the default result record count.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithManyObjectIds_DoesNotApplyDefaultRecordLimit()
    {
        await _fixture.EnsureLargeTestDatasetAsync();

        _fixture.CurrentSchema.Should().NotBeNullOrWhiteSpace();
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT objectid
            FROM features
            WHERE layer_id = @layerId
            ORDER BY objectid
            LIMIT 1200;
            """;
        command.Parameters.AddWithValue("layerId", TestLayerId);

        var requestedObjectIds = new List<long>(1200);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                requestedObjectIds.Add(reader.GetInt64(0));
            }
        }

        requestedObjectIds.Should().HaveCount(1200);
        var payload = JsonSerializer.Serialize(new
        {
            objectIds = requestedObjectIds,
            returnIdsOnly = true,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.ObjectIds.Should().NotBeNull();
        queryResponse.ObjectIds!.Should().HaveCount(requestedObjectIds.Count);
        queryResponse.ObjectIds.Should().BeEquivalentTo(
            requestedObjectIds,
            options => options.WithoutStrictOrdering());
    }

    /// <summary>
    /// Tests query with objectIds parameter via POST request
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostWithObjectIds_ReturnsSpecificFeatures()
    {
        // Arrange
        var json = """
            {
                "objectIds": [2, 4],
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(2);

        // Verify the returned features have the correct objectIds
        var returnedObjectIds = queryResponse.Features
            .Select(f => ReadObjectIdValue(f.Attributes["objectid"]))
            .ToArray();
        returnedObjectIds.Should().Contain(new long[] { 2, 4 });
    }

    /// <summary>
    /// Tests combining objectIds with where clause (intersection semantics)
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithObjectIdsAndWhereClause_ReturnsIntersection()
    {
        // Act - Request objectId 1 with a where clause that excludes it.
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?objectIds=1&where=objectid>100");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().BeEmpty();
    }

    /// <summary>
    /// Tests objectIds parameter with returnIdsOnly=true
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithObjectIdsAndReturnIdsOnly_ReturnsOnlyIds()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?objectIds=1,2,3&returnIdsOnly=true");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.ObjectIds.Should().NotBeNull();
        queryResponse.ObjectIds!.Should().HaveCount(3);
        queryResponse.ObjectIds.Should().Contain(new long[] { 1, 2, 3 });
        queryResponse.Count.Should().BeNull();
        queryResponse.Features.Should().BeNull();
    }

    /// <summary>
    /// Tests objectIds parameter with returnCountOnly=true
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithObjectIdsAndReturnCountOnly_ReturnsCount()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?objectIds=1,2,3&returnCountOnly=true");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Count.Should().Be(3);
        queryResponse.ObjectIds.Should().BeNull();
        queryResponse.Features.Should().BeNull();
        queryResponse.ObjectIdFieldName.Should().BeNull();
    }

    /// <summary>
    /// Tests error handling for invalid objectId formats
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithInvalidObjectIdFormat_Returns400()
    {
        // Act - Invalid objectId format (non-numeric)
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?objectIds=1,invalid,3");

        // Assert
        response.HaveStatusCode(System.Net.HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(content);
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.GetProperty("message").GetString().Should().Be("Bad Request");
        errorElement.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString() ?? string.Empty)
            .Should().Contain(detail => detail.Contains("Invalid query parameters"));
        errorElement.GetProperty("details").GetArrayLength().Should().BeGreaterThan(0);

        // Verify error details mention objectIds parameter specifically
        var details = errorElement.GetProperty("details").EnumerateArray()
            .Select(d => d.GetString())
            .ToArray();
        details.Should().Contain(d => d!.Contains("objectIds"));
    }

    /// <summary>
    /// Tests objectIds parameter with form data POST request
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostFormDataWithObjectIds_ReturnsSpecificFeatures()
    {
        // Arrange - Form data POST
        var formData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("objectIds", "1,5"),
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("returnGeometry", "true")
        });

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", formData);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(2);

        // Verify the returned features have the correct objectIds
        var returnedObjectIds = queryResponse.Features
            .Select(f => ReadObjectIdValue(f.Attributes["objectid"]))
            .ToArray();
        returnedObjectIds.Should().Contain(new long[] { 1, 5 });
    }

    /// <summary>
    /// Tests objectIds parameter with non-existent IDs returns empty result
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithNonExistentObjectIds_ReturnsEmptyResult()
    {
        // Act - Request objectIds that don't exist (TestFeatureStore has IDs 1-5)
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?objectIds=999,1000");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().BeEmpty();
        queryResponse.ExceededTransferLimit.Should().BeFalse();
    }

    // Query paging tests (Issue #8)

    /// <summary>
    /// Tests that the resultOffset parameter correctly skips the specified number of records
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithResultOffset_SkipsCorrectNumberOfRecords()
    {
        // Act - Skip first 2 records
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultOffset=2");

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();

        // With offset=2, should get features 3, 4, 5 from TestFeatureStore (features with IDs 3, 4, 5)
        queryResponse.Features.Should().HaveCount(3);
        queryResponse.ExceededTransferLimit.Should().BeFalse("because all remaining features after offset are returned");
    }

    /// <summary>
    /// Tests that the resultRecordCount parameter correctly limits the number of returned features
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithResultRecordCount_LimitsReturnedFeatures()
    {
        // Act - Limit to 3 records
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultRecordCount=3");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(3);
        queryResponse.ExceededTransferLimit.Should().BeTrue("because 5 features exist but only 3 were requested");
    }

    /// <summary>
    /// Tests that combining resultOffset and resultRecordCount parameters returns the correct page of results
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithOffsetAndCount_ReturnsCorrectPage()
    {
        // Act - Skip 1 record and limit to 2 records
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultOffset=1&resultRecordCount=2");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(2);
        queryResponse.ExceededTransferLimit.Should().BeTrue("because offset=1 leaves 4 features, but only 2 were requested");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithPagingWithoutExplicitOrder_DoesNotOverlapPages()
    {
        var page1Response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=1%3D1&resultOffset=0&resultRecordCount=2");
        var page2Response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=1%3D1&resultOffset=2&resultRecordCount=2");

        page1Response.Be200Ok();
        page2Response.Be200Ok();

        var page1Content = await page1Response.Content.ReadAsStringAsync();
        var page2Content = await page2Response.Content.ReadAsStringAsync();

        var page1 = JsonSerializer.Deserialize<QueryResponse>(
            page1Content,
            FeatureServerJsonContext.Default.QueryResponse);
        var page2 = JsonSerializer.Deserialize<QueryResponse>(
            page2Content,
            FeatureServerJsonContext.Default.QueryResponse);

        page1.Should().NotBeNull();
        page2.Should().NotBeNull();
        page1!.Features.Should().NotBeNull();
        page2!.Features.Should().NotBeNull();

        var page1Ids = page1.Features!
            .Select(feature => ReadObjectIdValue(feature.Attributes!["objectid"]))
            .ToHashSet();
        var page2Ids = page2.Features!
            .Select(feature => ReadObjectIdValue(feature.Attributes!["objectid"]))
            .ToHashSet();

        page1Ids.Intersect(page2Ids).Should().BeEmpty();
    }

    /// <summary>
    /// Tests that the exceededTransferLimit flag is correctly set when more results are available than requested
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithExceededLimit_SetsExceededTransferLimitFlag()
    {
        // Act - Request only 1 record when TestFeatureStore has 5 features
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultRecordCount=1");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(1);
        queryResponse.ExceededTransferLimit.Should().BeTrue("because there are 5 total features but only 1 was requested");

        using var jsonDoc = JsonDocument.Parse(content);
        jsonDoc.RootElement.GetProperty("exceededTransferLimit").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// Tests that the exceededTransferLimit flag is correctly set to false when all results are returned
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithAllResults_ExceededTransferLimitIsFalse()
    {
        // Act - Request all 5 records that exist in TestFeatureStore
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?resultRecordCount=5");

        // Assert
        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(5);
        queryResponse.ExceededTransferLimit.Should().BeFalse("because all available features were returned");

        using var jsonDoc = JsonDocument.Parse(content);
        // Esri's query contract always emits exceededTransferLimit; the false case must be
        // present in the wire payload (the ArcGIS API for Python paginator reads it
        // unconditionally — a missing value caused a fetched >= None TypeError).
        jsonDoc.RootElement.TryGetProperty("exceededTransferLimit", out var allResultsExceeded).Should().BeTrue();
        allResultsExceeded.GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// Tests that POST query requests correctly handle paging parameters (resultOffset and resultRecordCount)
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostWithPagingParameters_ReturnsCorrectPage()
    {
        // Arrange
        var json = """
            {
                "where": "1=1",
                "resultOffset": 1,
                "resultRecordCount": 2,
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.Features.Should().HaveCount(2);
        queryResponse.ExceededTransferLimit.Should().BeTrue("because offset=1 leaves 4 features, but only 2 were requested");
    }

    /// <summary>
    /// Tests that spatial queries with point geometry and default spatial relationship (intersects) work correctly
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithPointGeometryIntersects_ReturnsFilteredFeatures()
    {
        // Arrange - Point geometry in GeoServices REST JSON format
        var pointGeometry = @"{""x"":-122.4194,""y"":37.7749}"; // San Francisco coordinates

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(pointGeometry)}&spatialRel=esriSpatialRelIntersects&f=json");

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        // Spatial filtering should work even with our simple test data
        queryResponse.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests spatial queries with polygon geometry using contains relationship
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostWithPolygonGeometryContains_ReturnsContainedFeatures()
    {
        // Arrange - Polygon geometry around San Francisco Bay Area
        var json = """
            {
                "geometry": "{\"rings\":[[[-123.0,37.0],[-122.0,37.0],[-122.0,38.0],[-123.0,38.0],[-123.0,37.0]]]}",
                "spatialRel": "esriSpatialRelContains",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests that spatial relationship mapping works correctly for all supported relationships
    /// </summary>
    [Theory]
    [InlineData("esriSpatialRelIntersects")]
    [InlineData("esriSpatialRelContains")]
    [InlineData("esriSpatialRelWithin")]
    [InlineData("esriSpatialRelEnvelopeIntersects")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_SpatialRelMapping_ReturnsValidResponse(string spatialRel)
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4,""y"":37.7}";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(pointGeometry)}&spatialRel={spatialRel}&f=json");

        // Assert - Should not throw an exception for valid spatial relationships
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests error handling for invalid geometry in spatial queries
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithInvalidGeometry_Returns400()
    {
        // Arrange - Invalid JSON geometry
        var invalidGeometry = @"{""invalid"":""geometry""}";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(invalidGeometry)}&f=json");

        // Assert
        response.Be400BadRequest();
    }

    /// <summary>
    /// Tests error handling for unsupported spatial relationships
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithUnsupportedSpatialRel_Returns400()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4,""y"":37.7}";
        var unsupportedSpatialRel = "esriSpatialRelInvalid"; // Invalid spatial relationship

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(pointGeometry)}&spatialRel={unsupportedSpatialRel}&f=json");

        // Assert
        response.Be400BadRequest();
    }

    /// <summary>
    /// Tests that spatial queries can be combined with WHERE clauses
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithSpatialAndAttributeFilters_ReturnsFilteredFeatures()
    {
        // Arrange
        var pointGeometry = @"{""x"":-122.4,""y"":37.7}";
        var whereClause = "name='Test Feature'";

        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?geometry={Uri.EscapeDataString(pointGeometry)}&where={Uri.EscapeDataString(whereClause)}&f=json");

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    // Output format tests (Issue #9)

    /// <summary>
    /// Tests that f=json returns GeoServices REST JSON format with correct content type
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithFormatJson_ReturnsGeoServicesJsonFormat()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json");

        // Assert
        response.BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
        queryResponse.ObjectIdFieldName.Should().NotBeNullOrEmpty();
        queryResponse.Features.Should().AllSatisfy(f =>
        {
            f.Attributes.Should().NotBeNull();
            // Some features may not have geometry in test data, just verify structure
        });
    }

    /// <summary>
    /// Tests that f=geojson returns GeoJSON format with correct content type
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithFormatGeoJson_ReturnsGeoJsonFormat()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=geojson");

        // Assert
        response.BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var content = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            content, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonResponse.Should().NotBeNull();
        geoJsonResponse!.Type.Should().Be("FeatureCollection");
        geoJsonResponse.Features.Should().NotBeNull();
        geoJsonResponse.ExceededTransferLimit.Should().BeFalse();
        geoJsonResponse.Properties.Should().BeNull();

        using var jsonDoc = JsonDocument.Parse(content);
        jsonDoc.RootElement.TryGetProperty("exceededTransferLimit", out _).Should().BeFalse();
        jsonDoc.RootElement.TryGetProperty("properties", out _).Should().BeFalse();

        geoJsonResponse.Features.Should().AllSatisfy(f =>
        {
            f.Type.Should().Be("Feature");
            f.Properties.Should().NotBeNull();
            // Some features may not have geometry in test data, just verify structure
            if (f.Geometry != null)
            {
                f.Geometry.Type.Should().NotBeNullOrEmpty();
                f.Geometry.Coordinates.Should().NotBeNull();
            }
        });
    }

    /// <summary>
    /// Tests that GeoJSON format includes exceededTransferLimit only when exceeded
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithGeoJsonExceededTransferLimit_IncludesExceededTransferLimit()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=geojson&resultRecordCount=1");

        // Assert
        response.BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var content = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            content, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonResponse.Should().NotBeNull();
        geoJsonResponse!.ExceededTransferLimit.Should().BeTrue();
        geoJsonResponse.Properties.Should().NotBeNull();
        var exceededValue = geoJsonResponse.Properties!["exceededTransferLimit"];
        exceededValue.Should().BeOfType<JsonElement>();
        ((JsonElement)exceededValue).GetBoolean().Should().BeTrue();

        using var jsonDoc = JsonDocument.Parse(content);
        jsonDoc.RootElement.GetProperty("exceededTransferLimit").GetBoolean().Should().BeTrue();
        jsonDoc.RootElement.GetProperty("properties").GetProperty("exceededTransferLimit").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// Tests that outFields parameter filters returned attributes in both formats
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithOutFieldsParam_FiltersAttributesInBothFormats()
    {
        // Test GeoServices REST JSON format
        var geoServicesResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&outFields=objectid,name");
        geoServicesResponse.BeSuccessful();

        var geoServicesContent = await geoServicesResponse.Content.ReadAsStringAsync();
        var geoServicesQueryResponse = JsonSerializer.Deserialize<QueryResponse>(
            geoServicesContent, FeatureServerJsonContext.Default.QueryResponse);

        geoServicesQueryResponse!.Features.Should().AllSatisfy(f =>
        {
            f.Attributes.Keys.Should().Contain("objectid");
            f.Attributes.Keys.Should().Contain("name");
            // Should not contain other fields like description, etc.
            f.Attributes.Keys.Count.Should().BeLessThanOrEqualTo(2);
        });

        // Test GeoJSON format
        var geoJsonResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=geojson&outFields=objectid,name");
        geoJsonResponse.BeSuccessful();

        var geoJsonContent = await geoJsonResponse.Content.ReadAsStringAsync();
        var geoJsonQueryResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            geoJsonContent, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonQueryResponse!.Features.Should().AllSatisfy(f =>
        {
            f.Properties.Keys.Should().Contain("objectid");
            f.Properties.Keys.Should().Contain("name");
            // Should not contain other fields like description, etc.
            f.Properties.Keys.Count.Should().BeLessThanOrEqualTo(2);
        });
    }

    /// <summary>
    /// Tests that outFields supports wildcard combined with explicit fields
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithOutFieldsWildcardAndExplicitFields_ReturnsAllAttributes()
    {
        // Act
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&outFields=*,name");

        // Assert
        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            content, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNullOrEmpty();
        queryResponse.Features.Should().AllSatisfy(feature =>
        {
            feature.Attributes.Keys.Should().Contain("name");
            feature.Attributes.Keys.Should().Contain("description");
            feature.Attributes.Keys.Should().Contain("category");
        });
    }

    /// <summary>
    /// Tests that returnGeometry=false omits geometry in both formats
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithReturnGeometryFalse_OmitsGeometryInBothFormats()
    {
        // Test GeoServices REST JSON format
        var geoServicesResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&returnGeometry=false");
        geoServicesResponse.BeSuccessful();

        var geoServicesContent = await geoServicesResponse.Content.ReadAsStringAsync();
        var geoServicesQueryResponse = JsonSerializer.Deserialize<QueryResponse>(
            geoServicesContent, FeatureServerJsonContext.Default.QueryResponse);

        geoServicesQueryResponse!.Features.Should().AllSatisfy(f => f.Geometry.Should().BeNull());

        using var geoServicesDoc = JsonDocument.Parse(geoServicesContent);
        foreach (var feature in geoServicesDoc.RootElement.GetProperty("features").EnumerateArray())
        {
            feature.TryGetProperty("geometry", out _).Should().BeFalse();
        }

        // Test GeoJSON format
        var geoJsonResponse = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=geojson&returnGeometry=false");
        geoJsonResponse.BeSuccessful();

        var geoJsonContent = await geoJsonResponse.Content.ReadAsStringAsync();
        var geoJsonQueryResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            geoJsonContent, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonQueryResponse!.Features.Should().AllSatisfy(f => f.Geometry.Should().BeNull());
    }

    /// <summary>
    /// Tests that POST requests support format parameter in request body
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_PostWithGeoJsonFormat_ReturnsGeoJsonFormat()
    {
        // Arrange
        var requestBody = JsonSerializer.Serialize(new QueryParameters
        {
            Where = "1=1",
            F = "geojson",
            ResultRecordCount = 2
        }, FeatureServerJsonContext.Default.QueryParameters);
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.BeSuccessful();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            responseContent, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonResponse.Should().NotBeNull();
        geoJsonResponse!.Type.Should().Be("FeatureCollection");
        geoJsonResponse.Features.Should().HaveCount(2);
        geoJsonResponse.Features.Should().AllSatisfy(f =>
        {
            f.Type.Should().Be("Feature");
            f.Properties.Should().NotBeNull();
            f.Id.Should().NotBeNull();
        });
    }

    /// <summary>
    /// Tests that invalid format parameter returns 400 with validation details
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithInvalidFormat_Returns400()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=invalid");

        // Assert
        response.Be400BadRequest();
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var details = document.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray()
            .Select(detail => detail.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value));
        details.Should().Contain(detail => detail!.Contains("Output format 'invalid' is not supported"));
    }

    /// <summary>
    /// Tests that GeoJSON features include proper IDs from objectid field
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_GeoJsonFormat_IncludesFeatureIds()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=geojson&resultRecordCount=1");

        // Assert
        response.BeSuccessful();

        var content = await response.Content.ReadAsStringAsync();
        var geoJsonResponse = JsonSerializer.Deserialize<GeoJsonFeatureSet>(
            content, FeatureServerJsonContext.Default.GeoJsonFeatureSet);

        geoJsonResponse.Should().NotBeNull();
        geoJsonResponse!.Features.Should().HaveCount(1);

        var feature = geoJsonResponse.Features[0];
        feature.Id.Should().NotBeNull("GeoJSON features should include ID from objectid field");
        feature.Properties.Should().ContainKey("objectid");

        // The ID should match the objectid in properties - verify both have the same numeric value
        // Handle potential type differences between feature.Id and objectid property
        var idValue = feature.Id?.ToString();
        var objectidValue = feature.Properties["objectid"]?.ToString();

        idValue.Should().NotBeNullOrEmpty("Feature ID should have a value");
        objectidValue.Should().NotBeNullOrEmpty("Objectid property should have a value");
        idValue.Should().Be(objectidValue, "Feature ID should match the objectid property value");

        // Verify ID is a valid positive number
        var numericId = feature.Id switch
        {
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Number => jsonElement.GetInt64(),
            var other => Convert.ToInt64(other, CultureInfo.InvariantCulture)
        };
        numericId.Should().BeGreaterThan(0);
    }

    #region Geometry Type Support Tests (Issue #94)

    /// <summary>
    /// Tests spatial queries with LineString geometry
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithLineStringGeometry_ReturnsValidResponse()
    {
        // Arrange - LineString geometry in GeoServices REST JSON format
        var json = """
            {
                "geometry": "{\"paths\":[[[-122.5,37.7],[-122.4,37.8],[-122.3,37.9]]]}",
                "geometryType": "esriGeometryPolyline",
                "spatialRel": "esriSpatialRelIntersects",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests spatial queries with MultiPoint geometry
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithMultiPointGeometry_ReturnsValidResponse()
    {
        // Arrange - MultiPoint geometry
        var json = """
            {
                "geometry": "{\"points\":[[-122.4,37.8],[-122.3,37.9]]}",
                "geometryType": "esriGeometryMultipoint",
                "spatialRel": "esriSpatialRelIntersects",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests spatial queries with Envelope geometry (bounding box)
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithEnvelopeGeometry_ReturnsValidResponse()
    {
        // Arrange - Envelope geometry (bounding box)
        var json = """
            {
                "geometry": "{\"xmin\":-123.0,\"ymin\":37.0,\"xmax\":-122.0,\"ymax\":38.0}",
                "geometryType": "esriGeometryEnvelope",
                "spatialRel": "esriSpatialRelIntersects",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests spatial queries with MultiPolygon geometry (polygon with multiple rings)
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithMultiPolygonGeometry_ReturnsValidResponse()
    {
        // Arrange - MultiPolygon geometry (two separate polygons)
        var json = """
            {
                "geometry": "{\"rings\":[[[-123.0,37.0],[-122.5,37.0],[-122.5,37.5],[-123.0,37.5],[-123.0,37.0]],[[-122.3,37.6],[-121.8,37.6],[-121.8,38.1],[-122.3,38.1],[-122.3,37.6]]]}",
                "geometryType": "esriGeometryPolygon",
                "spatialRel": "esriSpatialRelContains",
                "returnGeometry": true,
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize<QueryResponse>(
            responseContent, FeatureServerJsonContext.Default.QueryResponse);

        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    /// <summary>
    /// Tests error handling for invalid geometry formats in new geometry types
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithInvalidLineStringGeometry_Returns400()
    {
        // Arrange - Invalid LineString geometry (missing paths)
        var json = """
            {
                "geometry": "{\"invalidProperty\":[]}",
                "geometryType": "esriGeometryPolyline",
                "spatialRel": "esriSpatialRelIntersects",
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be400BadRequest();
    }

    /// <summary>
    /// Tests error handling for empty geometry arrays
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task QueryFeatures_WithEmptyMultiPointGeometry_Returns400()
    {
        // Arrange - Empty MultiPoint geometry
        var json = """
            {
                "geometry": "{\"points\":[]}",
                "geometryType": "esriGeometryMultipoint",
                "spatialRel": "esriSpatialRelIntersects",
                "f": "json"
            }
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query", content);

        // Assert
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    public async Task ApplyEdits_ServiceLevel_WithLayerPayload_ReturnsPerLayerResults()
    {
        var request = """
            [
                {
                    "id": 0,
                    "adds": [
                        {
                            "attributes": {
                                "name": "Service-level test feature"
                            },
                            "geometry": {
                                "x": -122.4194,
                                "y": 37.7749
                            }
                        }
                    ]
                }
            ]
            """;
        var content = new StringContent(request, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits",
            content);

        response.Be200Ok();
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("editResults");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    public async Task ApplyEdits_ServiceLevel_WithMalformedJson_DoesNotLeakParserDetails()
    {
        var malformedRequest = """[{"id":0,"adds":[{"attributes":{"name":"bad"}}]""";
        var content = new StringContent(malformedRequest, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits",
            content);

        response.Be400BadRequest();
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Request body contains invalid JSON.");
        responseContent.Should().NotContain("BytePositionInLine");
        responseContent.Should().NotContain("LineNumber");
        responseContent.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    public async Task ApplyEdits_ServiceLevel_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var request = """[{"id":0,"adds":[{"attributes":{"name":"bad"}}]}]""";
        var content = new StringContent(request, Encoding.UTF8, "text/plain");

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Unsupported Media Type");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    public async Task ApplyEdits_ServiceLevel_WithUseGlobalIdsEnabled_ReturnsBadRequest()
    {
        var request = """
            [
              {
                "id": 0,
                "adds": [
                  {
                    "attributes": { "name": "Global id test" }
                  }
                ]
              }
            ]
            """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/applyEdits?useGlobalIds=true",
            new StringContent(request, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("useGlobalIds is not supported");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithAddOperation_ReturnsNewObjectId()
    {
        // Arrange
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Test Added Feature",
                        ["description"] = "Added via ApplyEdits test"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        X = -122.4194,
                        Y = 37.7749
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().NotBeNullOrEmpty();

        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.Success.Should().BeTrue();
        applyEditsResponse.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
        applyEditsResponse.AddResults[0].ObjectId.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithAddOperationAndCallerObjectId_ReturnsGeneratedObjectId()
    {
        var editsRequest = new ApplyEditsRequest
        {
            Adds =
            [
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = 999999L,
                        ["name"] = "Caller ObjectId Should Be Ignored"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        X = -122.4194,
                        Y = 37.7749
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);

        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.AddResults.Should().ContainSingle();
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
        applyEditsResponse.AddResults[0].ObjectId.Should().BeGreaterThan(0);
        applyEditsResponse.AddResults[0].ObjectId.Should().NotBe(999999L);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithUpdateOperation_ReturnsUpdatedObjectId()
    {
        // Arrange - First add a feature to update
        var addRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Feature to Update",
                        ["description"] = "Original description"
                    }
                }
            }
        };

        var addJson = JsonSerializer.Serialize(addRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var addContent = new StringContent(addJson, System.Text.Encoding.UTF8, "application/json");
        var addResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", addContent);

        var addResponseContent = await addResponse.Content.ReadAsStringAsync();
        var addResult = JsonSerializer.Deserialize<ApplyEditsResponse>(
            addResponseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        var objectId = addResult!.AddResults![0].ObjectId!.Value;

        // Now update the feature
        var updateRequest = new ApplyEditsRequest
        {
            Updates = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = objectId,
                        ["name"] = "Updated Feature Name",
                        ["description"] = "Updated description"
                    }
                }
            }
        };

        var updateJson = JsonSerializer.Serialize(updateRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var updateContent = new StringContent(updateJson, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", updateContent);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.Success.Should().BeTrue();
        applyEditsResponse.UpdateResults.Should().HaveCount(1);
        applyEditsResponse.UpdateResults![0].Success.Should().BeTrue();
        applyEditsResponse.UpdateResults[0].ObjectId.Should().Be(objectId);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithDeleteOperation_ReturnsDeletedObjectId()
    {
        // Arrange - First add a feature to delete
        var addRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Feature to Delete",
                        ["description"] = "Will be deleted"
                    }
                }
            }
        };

        var addJson = JsonSerializer.Serialize(addRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var addContent = new StringContent(addJson, System.Text.Encoding.UTF8, "application/json");
        var addResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", addContent);

        var addResponseContent = await addResponse.Content.ReadAsStringAsync();
        var addResult = JsonSerializer.Deserialize<ApplyEditsResponse>(
            addResponseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        var objectId = addResult!.AddResults![0].ObjectId!.Value;

        // Now delete the feature
        var deleteRequest = new ApplyEditsRequest
        {
            Deletes = new object[] { objectId }
        };

        var deleteJson = JsonSerializer.Serialize(deleteRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var deleteContent = new StringContent(deleteJson, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", deleteContent);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.Success.Should().BeTrue();
        applyEditsResponse.DeleteResults.Should().HaveCount(1);
        applyEditsResponse.DeleteResults![0].Success.Should().BeTrue();
        applyEditsResponse.DeleteResults[0].ObjectId.Should().Be(objectId);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithMixedOperations_ReturnsCorrectResults()
    {
        // Arrange - First add a feature to update/delete
        var setupRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Feature for Update",
                        ["description"] = "Setup feature"
                    }
                },
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "Feature for Delete",
                        ["description"] = "Setup feature"
                    }
                }
            }
        };

        var setupJson = JsonSerializer.Serialize(setupRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var setupContent = new StringContent(setupJson, System.Text.Encoding.UTF8, "application/json");
        var setupResponse = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", setupContent);

        var setupResponseContent = await setupResponse.Content.ReadAsStringAsync();
        var setupResult = JsonSerializer.Deserialize<ApplyEditsResponse>(
            setupResponseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        var updateObjectId = setupResult!.AddResults![0].ObjectId!.Value;
        var deleteObjectId = setupResult.AddResults![1].ObjectId!.Value;

        // Mixed operations request
        var mixedRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "New Added Feature",
                        ["description"] = "Added in mixed operation"
                    }
                }
            },
            Updates = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = updateObjectId,
                        ["name"] = "Updated in Mixed Operation",
                        ["description"] = "Updated description"
                    }
                }
            },
            Deletes = new object[] { deleteObjectId }
        };

        var mixedJson = JsonSerializer.Serialize(mixedRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var mixedContent = new StringContent(mixedJson, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", mixedContent);

        // Assert
        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.Success.Should().BeTrue();

        // Verify all operations succeeded
        applyEditsResponse.AddResults.Should().HaveCount(1);
        applyEditsResponse.AddResults![0].Success.Should().BeTrue();
        applyEditsResponse.AddResults[0].ObjectId.Should().BeGreaterThan(0);

        applyEditsResponse.UpdateResults.Should().HaveCount(1);
        applyEditsResponse.UpdateResults![0].Success.Should().BeTrue();
        applyEditsResponse.UpdateResults[0].ObjectId.Should().Be(updateObjectId);

        applyEditsResponse.DeleteResults.Should().HaveCount(1);
        applyEditsResponse.DeleteResults![0].Success.Should().BeTrue();
        applyEditsResponse.DeleteResults[0].ObjectId.Should().Be(deleteObjectId);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithInvalidUpdate_ReturnsError()
    {
        // Arrange - Try to update non-existent feature
        var updateRequest = new ApplyEditsRequest
        {
            Updates = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = 999999, // Non-existent ID
                        ["name"] = "Invalid Update"
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(updateRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await _fixture.Client.PostAsync($"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);

        // Assert
        response.Be200Ok(); // ApplyEdits returns 200 but with error in results
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var responseContent = await response.Content.ReadAsStringAsync();
        var applyEditsResponse = JsonSerializer.Deserialize<ApplyEditsResponse>(
            responseContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyEditsResponse.Should().NotBeNull();
        applyEditsResponse!.UpdateResults.Should().HaveCount(1);
        applyEditsResponse.UpdateResults![0].Success.Should().BeFalse();
        applyEditsResponse.UpdateResults[0].Error.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithUseGlobalIdsEnabled_ReturnsBadRequest()
    {
        var payload = """
            {
              "adds": [
                {
                  "attributes": { "name": "Global id test" }
                }
              ]
            }
            """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits?useGlobalIds=true",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("useGlobalIds is not supported");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_WithUnsupportedFormat_ReturnsBadRequest()
    {
        var payload = """
            {
              "adds": [
                {
                  "attributes": { "name": "Unsupported format test" }
                }
              ]
            }
            """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits?f=xml",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var details = document.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray()
            .Select(detail => detail.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value));
        details.Should().Contain(detail => detail!.Contains("Output format 'xml' is not supported"));
    }

    [IntegrationTest]
    [Operation(Operations.BulkCreate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/addFeatures")]
    public async Task AddFeatures_WithFeaturePayload_ReturnsAddResults()
    {
        var payload = """
            {
              "features": [
                {
                  "attributes": {
                    "name": "Added via addFeatures",
                    "description": "Bulk create endpoint"
                  },
                  "geometry": {
                    "x": -122.35,
                    "y": 37.77
                  }
                }
              ],
              "f": "json"
            }
            """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.ApplyEditsResponse);
        result.Should().NotBeNull();
        result!.AddResults.Should().HaveCount(1);
        result.AddResults![0].Success.Should().BeTrue();
        result.AddResults[0].ObjectId.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.BulkUpdate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    public async Task UpdateFeatures_WithFeaturePayload_ReturnsUpdateResults()
    {
        var addPayload = """
            {
              "features": [
                {
                  "attributes": {
                    "name": "Update target",
                    "description": "Before update"
                  }
                }
              ]
            }
            """;

        var addResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            new StringContent(addPayload, Encoding.UTF8, "application/json"));
        addResponse.Be200Ok();

        var addContent = await addResponse.Content.ReadAsStringAsync();
        var addResult = JsonSerializer.Deserialize(addContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        var objectId = addResult!.AddResults![0].ObjectId!.Value;

        var updatePayload = $$"""
            {
              "features": [
                {
                  "attributes": {
                    "objectid": {{objectId}},
                    "name": "Updated via updateFeatures",
                    "description": "After update"
                  }
                }
              ],
              "rollbackOnFailure": true
            }
            """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/updateFeatures",
            new StringContent(updatePayload, Encoding.UTF8, "application/json"));

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.ApplyEditsResponse);
        result.Should().NotBeNull();
        result!.UpdateResults.Should().HaveCount(1);
        result.UpdateResults![0].Success.Should().BeTrue();
        result.UpdateResults[0].ObjectId.Should().Be(objectId);
    }

    [IntegrationTest]
    [Operation(Operations.BulkUpdate)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    public async Task UpdateFeatures_WithAttributeOnlyPayload_PreservesExistingGeometryAndAttributes()
    {
        var addPayload = """
            {
              "features": [
                {
                  "attributes": {
                    "name": "Attribute-only update target",
                    "description": "Description should survive"
                  },
                  "geometry": {
                    "x": -122.35,
                    "y": 37.77
                  }
                }
              ]
            }
            """;

        var addResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            new StringContent(addPayload, Encoding.UTF8, "application/json"));
        addResponse.Be200Ok();

        var addContent = await addResponse.Content.ReadAsStringAsync();
        var addResult = JsonSerializer.Deserialize(addContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        var objectId = addResult!.AddResults![0].ObjectId!.Value;

        var updatePayload = $$"""
            {
              "features": [
                {
                  "attributes": {
                    "objectid": {{objectId}},
                    "name": "Attribute-only update applied"
                  }
                }
              ]
            }
            """;

        var updateResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/updateFeatures",
            new StringContent(updatePayload, Encoding.UTF8, "application/json"));
        updateResponse.Be200Ok();

        var queryResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?f=json&objectIds={objectId}&returnGeometry=true");
        queryResponse.Be200Ok();

        using var document = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
        var feature = document.RootElement.GetProperty("features").EnumerateArray().Single();
        var attributes = feature.GetProperty("attributes");
        attributes.GetProperty("name").GetString().Should().Be("Attribute-only update applied");
        attributes.GetProperty("description").GetString().Should().Be("Description should survive");
        var geometry = feature.GetProperty("geometry");
        geometry.GetProperty("x").GetDouble().Should().BeApproximately(-122.35, 0.000001);
        geometry.GetProperty("y").GetDouble().Should().BeApproximately(37.77, 0.000001);
    }

    [IntegrationTest]
    [Operation(Operations.BulkDelete)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    public async Task DeleteFeatures_WithObjectIdsPayload_ReturnsDeleteResults()
    {
        var addPayload = """
            {
              "features": [
                {
                  "attributes": {
                    "name": "Delete target",
                    "description": "Before delete"
                  }
                }
              ]
            }
            """;

        var addResponse = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/addFeatures",
            new StringContent(addPayload, Encoding.UTF8, "application/json"));
        addResponse.Be200Ok();

        var addContent = await addResponse.Content.ReadAsStringAsync();
        var addResult = JsonSerializer.Deserialize(addContent, FeatureServerJsonContext.Default.ApplyEditsResponse);
        var objectId = addResult!.AddResults![0].ObjectId!.Value;

        var deletePayload = $$"""
            {
              "objectIds": [{{objectId}}],
              "f": "json"
            }
            """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/deleteFeatures",
            new StringContent(deletePayload, Encoding.UTF8, "application/json"));

        response.Be200Ok();

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.ApplyEditsResponse);
        result.Should().NotBeNull();
        result!.DeleteResults.Should().HaveCount(1);
        result.DeleteResults![0].Success.Should().BeTrue();
        result.DeleteResults[0].ObjectId.Should().Be(objectId);
    }

    [IntegrationTest]
    [Operation(Operations.BulkDelete)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    public async Task DeleteFeatures_WithMalformedObjectIdsDelimiter_ReturnsBadRequest()
    {
        var deletePayload = """
            {
              "objectIds": "1,,2",
              "f": "json"
            }
            """;

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/deleteFeatures",
            new StringContent(deletePayload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static long ReadObjectIdValue(object? value)
    {
        return value switch
        {
            null => throw new InvalidOperationException("ObjectId value is null."),
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.GetInt64(),
            JsonElement element when element.ValueKind == JsonValueKind.String &&
                long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            IConvertible convertible => Convert.ToInt64(convertible, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unsupported objectId value type: {value.GetType().Name}")
        };
    }

    #endregion
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcFeatures;

/// <summary>
/// Integration tests for enhanced OGC API Features functionality
/// Covers Issues #157 (MVP gaps), #56 (content negotiation), and related enhancements
/// </summary>
[Collection("Database")]
[Protocol(Protocols.OgcApiFeatures)]
[Operation(Operations.Query)]
public sealed class OgcFeaturesEnhancementsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestCollectionId = "0";

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Single Item Endpoint Tests (Issue #157)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithValidIds_ReturnsGeoJsonFeature()
    {
        // Arrange
        const long featureId = 1;

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items/{featureId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("Feature");
        json.RootElement.TryGetProperty("id", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("properties", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("geometry", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithInvalidCollectionId_ReturnsNotFound()
    {
        // Arrange
        const string invalidCollectionId = "nonexistent";
        const long featureId = 1;

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{invalidCollectionId}/items/{featureId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithInvalidFeatureId_ReturnsNotFound()
    {
        // Arrange
        const long nonexistentFeatureId = 99999;

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items/{nonexistentFeatureId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithInvalidFeatureIdFormat_ReturnsNotFound()
    {
        // Arrange
        const string invalidFeatureId = "not_a_number";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items/{invalidFeatureId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region CRS, Queryables, and GML Tests

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithCrsParameter_SwapsAxisOrder()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items?limit=1&crs=http://www.opengis.net/def/crs/EPSG/0/4326");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Content-Crs", out var contentCrsValues).Should().BeTrue();
        contentCrsValues!.First().Should().Contain("http://www.opengis.net/def/crs/EPSG/0/4326");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var firstFeature = json.RootElement.GetProperty("features").EnumerateArray().First();
        var coordinates = firstFeature.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().ToArray();

        coordinates.Should().HaveCount(2);
        coordinates[0].GetDouble().Should().Be(37.5);
        coordinates[1].GetDouble().Should().Be(-122.5);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithQueryableParameter_FiltersResults()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items?name={Uri.EscapeDataString("Test Feature")}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().NotBeEmpty();

        foreach (var feature in features)
        {
            feature.GetProperty("properties").GetProperty("name").GetString().Should().Be("Test Feature");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/queryables")]
    public async Task GetQueryables_ReturnsSchema()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/queryables");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        json.RootElement.TryGetProperty("properties", out var properties).Should().BeTrue();
        properties.TryGetProperty("name", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithGmlFormat_ReturnsGml()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items?f=gml&limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Contain("application/gml+xml");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("FeatureCollection");
        content.Should().Contain("gml");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithGmlFormat_ReturnsGml()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items/1?f=gml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Contain("application/gml+xml");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("gml");
        content.Should().Contain("id=");
    }

    #endregion

    #region Bbox Parameter Tests (Issue #157)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithValidBbox_ReturnsFilteredFeatures()
    {
        // Arrange - Use a worldwide bbox to ensure we get results
        var bbox = "-180,-90,180,90";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?bbox={bbox}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.TryGetProperty("features", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithInvalidBboxFormat_ReturnsBadRequest()
    {
        // Arrange - Invalid bbox with only 3 coordinates
        var invalidBbox = "-180,-90,180";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?bbox={invalidBbox}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("4 or 6");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithInvalidBboxValues_ReturnsBadRequest()
    {
        // Arrange - Invalid bbox where min latitude > max latitude
        var invalidBbox = "0,10,20,-10";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?bbox={invalidBbox}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("minimum latitude must be less than or equal to maximum latitude");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithOutOfRangeBbox_ReturnsBadRequest()
    {
        // Arrange - Bbox coordinates out of valid range
        var invalidBbox = "-200,-100,200,100";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?bbox={invalidBbox}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("coordinates are out of valid range");
    }

    #endregion

    #region Default Limit Tests (Issue #157)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithoutLimitParameter_UsesConfiguredDefaultLimit()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        var numberReturned = json.RootElement.GetProperty("numberReturned").GetInt32();

        var defaultLimit = new Honua.Core.Configuration.LimitsOptions().Query.DefaultRecordCount;
        features.Length.Should().BeLessThanOrEqualTo(defaultLimit);
        numberReturned.Should().BeLessThanOrEqualTo(defaultLimit);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithLimitExceedingMax_ReturnsBadRequest()
    {
        // Arrange - Limit exceeding maximum allowed
        var excessiveLimit = new Honua.Core.Configuration.LimitsOptions().Query.MaxRecordCount + 1;

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?limit={excessiveLimit}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Paging Links and TimeStamp Tests (Issue #157)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_ReturnsResponseWithPagingLinksAndTimeStamp()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?limit=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        // Check for required properties
        json.RootElement.TryGetProperty("timeStamp", out var timeStampElement).Should().BeTrue();
        json.RootElement.TryGetProperty("links", out var linksElement).Should().BeTrue();

        // Verify timeStamp is a valid ISO 8601 datetime
        timeStampElement.GetString().Should().NotBeNullOrEmpty();
        DateTime.TryParse(timeStampElement.GetString(), out _).Should().BeTrue();

        // Verify links array contains required link types
        var links = linksElement.EnumerateArray().ToArray();
        links.Should().NotBeEmpty();

        // Should have at least a self link
        var hasSelfLink = links.Any(link =>
            link.TryGetProperty("rel", out var rel) &&
            rel.GetString() == "self");
        hasSelfLink.Should().BeTrue();

        // Verify link structure
        foreach (var link in links)
        {
            link.TryGetProperty("href", out _).Should().BeTrue("Each link should have href");
            link.TryGetProperty("rel", out _).Should().BeTrue("Each link should have rel");
            link.TryGetProperty("type", out _).Should().BeTrue("Each link should have type");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithPagination_ReturnsNextAndPrevLinks()
    {
        // Act - Request with offset to potentially get next/prev links
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?limit=1&offset=1");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.TryGetProperty("links", out var linksElement).Should().BeTrue();
        var links = linksElement.EnumerateArray().ToArray();

        // Should have previous page link since offset > 0
        var hasPrevLink = links.Any(link =>
            link.TryGetProperty("rel", out var rel) &&
            rel.GetString() == "prev");
        hasPrevLink.Should().BeTrue();
    }

    #endregion

    #region OpenAPI Service Description Tests (Issue #157)

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /openapi.json")]
    public async Task GetOpenApiSpec_ReturnsValidOpenApiSpecification()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/openapi.json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().StartWith("application/vnd.oai.openapi+json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        // Verify OpenAPI structure
        json.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.0");
        json.RootElement.TryGetProperty("info", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("paths", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("components", out _).Should().BeTrue();

        // Verify base server path is documented
        json.RootElement.TryGetProperty("servers", out var serversElement).Should().BeTrue();
        serversElement.EnumerateArray()
            .Any(server =>
                server.TryGetProperty("url", out var url) &&
                url.GetString() == "/ogc/features")
            .Should()
            .BeTrue();

        // Verify key paths are documented (relative to server base)
        var paths = json.RootElement.GetProperty("paths");
        paths.TryGetProperty("/", out _).Should().BeTrue();
        paths.TryGetProperty("/conformance", out _).Should().BeTrue();
        paths.TryGetProperty("/collections", out _).Should().BeTrue();
        paths.TryGetProperty("/collections/{collectionId}/items", out _).Should().BeTrue();
        paths.TryGetProperty("/collections/{collectionId}/items/{featureId}", out _).Should().BeTrue();
    }

    #endregion

    #region Content Negotiation Tests (Issue #56)

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithJsonFormat_ReturnsJsonResponse()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        json.RootElement.TryGetProperty("features", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithGeoJsonFormat_ReturnsGeoJsonResponse()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?f=geojson");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithHtmlFormat_ReturnsHtmlResponse()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?f=html");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("<html");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithGeoJsonAcceptHeader_ReturnsGeoJsonResponse()
    {
        // Arrange
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Accept", "application/geo+json");

        // Act
        var response = await client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithJsonAcceptHeader_ReturnsJsonResponse()
    {
        // Arrange
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Accept", "application/json");

        // Act
        var response = await client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_FormatParameterOverridesAcceptHeader()
    {
        // Arrange - Accept header says JSON but format parameter says GeoJSON
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Accept", "application/json");

        // Act
        var response = await client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?f=geojson");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features")]
    public async Task GetLandingPage_WithContentNegotiation_ReturnsCorrectFormat()
    {
        // Act with format parameter
        var response = await _fixture.Client.GetAsync("/ogc/features?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithContentNegotiation_ReturnsCorrectFormat()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items/1?f=json");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.TryGetProperty("id", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("properties", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("geometry", out var geomProperty).Should().BeTrue();

        geomProperty.ValueKind.Should().Be(JsonValueKind.Object);
        geomProperty.TryGetProperty("type", out _).Should().BeTrue();
    }

    #endregion

    #region DateTime Parameter Tests (Issue #157 - Placeholder)

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithDateTimeParameter_FiltersFeatures()
    {
        // Arrange
        var datetime = "2023-01-01T00:00:00Z/2023-01-10T00:00:00Z";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?datetime={datetime}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        var ids = features
            .Select(feature =>
            {
                var idProperty = feature.GetProperty("id");
                // Handle both string and number cases for id field
                if (idProperty.ValueKind == JsonValueKind.String)
                {
                    return long.Parse(idProperty.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    return idProperty.GetInt64();
                }
            })
            .OrderBy(id => id)
            .ToArray();

        ids.Should().Equal(1, 2);
    }

    #endregion
}

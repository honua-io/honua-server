// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Integration tests for enhanced OGC API Features functionality
/// Covers Issues #157 (MVP gaps), #56 (content negotiation), and related enhancements
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
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
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithCrsParameter_SwapsAxisOrder()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items/1?crs=http://www.opengis.net/def/crs/EPSG/0/4326");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Content-Crs", out var contentCrsValues).Should().BeTrue();
        contentCrsValues!.First().Should().Contain("http://www.opengis.net/def/crs/EPSG/0/4326");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var coordinates = json.RootElement.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().ToArray();

        coordinates.Should().HaveCount(2);
        coordinates[0].GetDouble().Should().Be(37.5);
        coordinates[1].GetDouble().Should().Be(-122.5);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithCrs84Alias_ReturnsContentCrs()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items?limit=1&crs=CRS84");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Content-Crs", out var contentCrsValues).Should().BeTrue();
        contentCrsValues!.First().Should().Contain(OgcFeaturesUtilities.Crs84Uri);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    public async Task CreateFeature_WithContentCrsAlias_ReturnsCreated()
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.5, 37.5]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "CRS84 alias feature"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/ogc/features/collections/{TestCollectionId}/items")
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/geo+json"))
        };
        request.Headers.TryAddWithoutValidation("Content-Crs", "CRS84");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
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
    [Endpoint("GET /ogc/features/collections/{collectionId}/queryables")]
    public async Task GetQueryables_WithSchemaJsonAcceptHeader_ReturnsSchemaJson()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/schema+json;q=1.0"));
        client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;q=0.5"));

        var response = await client.GetAsync($"/ogc/features/collections/{TestCollectionId}/queryables");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/schema+json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("$schema").GetString().Should().Be("https://json-schema.org/draft/2020-12/schema");
        json.RootElement.GetProperty("$id").GetString()
            .Should().EndWith($"/ogc/features/collections/{TestCollectionId}/queryables");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    public async Task GetCollection_IncludesItemsLinksForAllSupportedFormats()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();

        var itemLinkTypes = links
            .Where(link => link.TryGetProperty("rel", out var rel) && rel.GetString() == "items")
            .Select(link => link.GetProperty("type").GetString())
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        itemLinkTypes.Should().Contain("application/geo+json");
        itemLinkTypes.Should().Contain("application/json");
        itemLinkTypes.Should().Contain("application/gml+xml;version=3.2");
        itemLinkTypes.Should().Contain("text/html");

        links.Any(link =>
                link.TryGetProperty("rel", out var rel) &&
                rel.GetString() == RelationTypes.Queryables &&
                link.TryGetProperty("type", out var type) &&
                type.GetString() == "application/schema+json")
            .Should()
            .BeTrue();

        links.Any(link =>
                link.TryGetProperty("rel", out var rel) &&
                rel.GetString() == RelationTypes.Map &&
                link.TryGetProperty("href", out var href) &&
                href.GetString() != null &&
                href.GetString()!.Contains($"/ogc/maps/collections/{TestCollectionId}/map", StringComparison.Ordinal))
            .Should()
            .BeTrue();
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
        content.Should().Contain("xsi:schemaLocation=");
        content.Should().Contain(OgcFeaturesUtilities.GmlApplicationSchemaPath);
        content.Should().Contain("gml:id=\"geom_");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/features/schemas/honua-ogcapi-features.xsd")]
    public async Task GetGmlApplicationSchema_ReturnsFeatureSchema()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/schemas/honua-ogcapi-features.xsd");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Contain("targetNamespace=\"http://www.opengis.net/ogcapi-features-1/1.0\"");
        content.Should().Contain("substitutionGroup=\"gml:AbstractFeature\"");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithGmlFormat_IncludesCollectionMetadataAttributes()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items?f=gml&limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("numberMatched=\"");
        content.Should().Contain("numberReturned=\"2\"");
        content.Should().Contain("timeStamp=\"");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithGml4326Crs_UsesLatitudeLongitudeCoordinates()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items?f=gml&limit=1&crs=http://www.opengis.net/def/crs/EPSG/0/4326");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("37.5 -122.5");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithGmlCrs84_UsesLongitudeLatitudeCoordinates()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items?f=gml&limit=1&crs=CRS84");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("-122.5 37.5");
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

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithFormatAndCrs_PreservesRepresentationLinks()
    {
        var crs = "http://www.opengis.net/def/crs/EPSG/0/4326";
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items/1?f=json&crs={Uri.EscapeDataString(crs)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();
        var representationLinks = links
            .Where(link =>
                link.TryGetProperty("rel", out var rel) &&
                (rel.GetString() == RelationTypes.Self || rel.GetString() == RelationTypes.Alternate))
            .ToArray();

        representationLinks.Should().NotBeEmpty();
        representationLinks.Should().OnlyContain(link =>
            link.GetProperty("href").GetString()!.Contains(
                "crs=http%3A%2F%2Fwww.opengis.net%2Fdef%2Fcrs%2FEPSG%2F0%2F4326",
                StringComparison.Ordinal));
        representationLinks.Should().OnlyContain(link =>
            link.GetProperty("href").GetString()!.Contains("f=", StringComparison.Ordinal));
        representationLinks.Should().Contain(link =>
            link.GetProperty("rel").GetString() == RelationTypes.Self &&
            link.GetProperty("href").GetString()!.Contains("f=json", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithGmlFormat_ReturnsSingleFeatureDocument()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/features/collections/{TestCollectionId}/items/1?f=gml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("<app:Feature");
        content.Should().Contain("gml:id=");
        content.Should().NotContain("<wfs:FeatureCollection");
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
        content.Should().Contain("4 or 6 comma-separated values");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_With3dBbox_ReturnsBadRequest()
    {
        // Arrange - 3D bbox (minx, miny, minz, maxx, maxy, maxz)
        var bbox = "-180,-90,-10,180,90,10";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?bbox={bbox}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("3D bounding boxes are not supported");
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

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithDatelineCrossingBbox_ReturnsResults()
    {
        // Arrange - Crosses antimeridian (minX > maxX) while still covering sample data
        var bbox = "170,-90,-50,90";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?bbox={bbox}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var features = json.RootElement.GetProperty("features").EnumerateArray().ToArray();
        features.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithBbox_ExcludesNullGeometryFeatures()
    {
        // Arrange - insert a feature without geometry and query with a bbox filter
        // OGC API Features Part 1, Section 7.15.4: when bbox is provided, only features
        // with a spatial geometry that intersects the bounding box shall be in the result set.
        var name = $"Null Geometry {Guid.NewGuid():N}";
        await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, name);
        var bbox = "-180,-90,180,90";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?bbox={bbox}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var features = json.RootElement.GetProperty("features").EnumerateArray();

        features.Any(feature =>
        {
            if (!feature.TryGetProperty("properties", out var props))
            {
                return false;
            }

            return props.TryGetProperty("name", out var nameProp)
                && string.Equals(nameProp.GetString(), name, StringComparison.Ordinal);
        }).Should().BeFalse();
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

        var hasQueryablesLink = links.Any(link =>
            link.TryGetProperty("rel", out var rel) &&
            rel.GetString() == RelationTypes.Queryables);
        hasQueryablesLink.Should().BeTrue();

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
    public async Task GetOpenApiSpec_RootRoute_ReturnsValidOpenApiSpecification()
    {
        var response = await _fixture.Client.GetAsync("/openapi.json");

        await AssertOpenApiSpecAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/api")]
    public async Task GetOpenApiSpec_OgcFeaturesApiRoute_ReturnsValidOpenApiSpecification()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/api");

        await AssertOpenApiSpecAsync(response);
    }

    private static async Task AssertOpenApiSpecAsync(HttpResponseMessage response)
    {
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

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /openapi.json")]
    public async Task GetOpenApiSpec_DocumentsWriteSecurityAndConditionalHeaders()
    {
        var response = await _fixture.Client.GetAsync("/openapi.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        var components = json.RootElement.GetProperty("components");
        var securitySchemes = components.GetProperty("securitySchemes");
        securitySchemes.TryGetProperty("ApiKeyAuth", out _).Should().BeTrue();
        securitySchemes.TryGetProperty("BearerAuth", out _).Should().BeTrue();

        var itemCollectionPath = json.RootElement.GetProperty("paths").GetProperty("/collections/{collectionId}/items");
        var postOperation = itemCollectionPath.GetProperty("post");
        postOperation.GetProperty("security").EnumerateArray().Should().NotBeEmpty();
        postOperation.GetProperty("parameters").EnumerateArray()
            .Any(parameter => parameter.GetProperty("name").GetString() == "Content-Crs")
            .Should()
            .BeTrue();
        postOperation.GetProperty("responses").TryGetProperty("401", out _).Should().BeTrue();
        postOperation.GetProperty("responses").TryGetProperty("403", out _).Should().BeTrue();
        var postCreatedResponse = postOperation.GetProperty("responses").GetProperty("201");
        postCreatedResponse.GetProperty("headers").TryGetProperty("Location", out _).Should().BeTrue();
        postCreatedResponse.GetProperty("headers").TryGetProperty("ETag", out _).Should().BeTrue();
        postCreatedResponse.GetProperty("headers").TryGetProperty("Content-Crs", out _).Should().BeTrue();

        var featureItemPath = json.RootElement.GetProperty("paths").GetProperty("/collections/{collectionId}/items/{featureId}");
        var putOperation = featureItemPath.GetProperty("put");
        putOperation.GetProperty("security").EnumerateArray().Should().NotBeEmpty();
        putOperation.GetProperty("parameters").EnumerateArray()
            .Any(parameter => parameter.GetProperty("name").GetString() == "Content-Crs")
            .Should()
            .BeTrue();
        putOperation.GetProperty("parameters").EnumerateArray()
            .Any(parameter => parameter.GetProperty("name").GetString() == "If-Match")
            .Should()
            .BeTrue();
        putOperation.GetProperty("responses").TryGetProperty("401", out _).Should().BeTrue();
        putOperation.GetProperty("responses").TryGetProperty("403", out _).Should().BeTrue();
        putOperation.GetProperty("responses").TryGetProperty("412", out _).Should().BeTrue();
        var putOkResponse = putOperation.GetProperty("responses").GetProperty("200");
        putOkResponse.GetProperty("headers").TryGetProperty("ETag", out _).Should().BeTrue();
        putOkResponse.GetProperty("headers").TryGetProperty("Content-Crs", out _).Should().BeTrue();

        var patchOperation = featureItemPath.GetProperty("patch");
        patchOperation.GetProperty("security").EnumerateArray().Should().NotBeEmpty();
        patchOperation.GetProperty("parameters").EnumerateArray()
            .Any(parameter => parameter.GetProperty("name").GetString() == "Content-Crs")
            .Should()
            .BeTrue();
        patchOperation.GetProperty("parameters").EnumerateArray()
            .Any(parameter => parameter.GetProperty("name").GetString() == "If-Match")
            .Should()
            .BeTrue();
        var patchContent = patchOperation.GetProperty("requestBody").GetProperty("content");
        patchContent.TryGetProperty("application/geo+json", out _).Should().BeTrue();
        patchContent.TryGetProperty("application/json", out _).Should().BeTrue();
        patchContent.TryGetProperty("application/merge-patch+json", out _).Should().BeTrue();
        patchOperation.GetProperty("responses").TryGetProperty("401", out _).Should().BeTrue();
        patchOperation.GetProperty("responses").TryGetProperty("403", out _).Should().BeTrue();
        patchOperation.GetProperty("responses").TryGetProperty("412", out _).Should().BeTrue();
        var patchOkResponse = patchOperation.GetProperty("responses").GetProperty("200");
        patchOkResponse.GetProperty("headers").TryGetProperty("ETag", out _).Should().BeTrue();
        patchOkResponse.GetProperty("headers").TryGetProperty("Content-Crs", out _).Should().BeTrue();

        var deleteOperation = featureItemPath.GetProperty("delete");
        deleteOperation.GetProperty("security").EnumerateArray().Should().NotBeEmpty();
        deleteOperation.GetProperty("responses").TryGetProperty("401", out _).Should().BeTrue();
        deleteOperation.GetProperty("responses").TryGetProperty("403", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /openapi.json")]
    public async Task GetOpenApiSpec_DocumentsQueryablesSchemaJsonNegotiation()
    {
        var response = await _fixture.Client.GetAsync("/openapi.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var queryablesOperation = json.RootElement
            .GetProperty("paths")
            .GetProperty("/collections/{collectionId}/queryables")
            .GetProperty("get");

        queryablesOperation.GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .TryGetProperty("application/schema+json", out _)
            .Should()
            .BeTrue();

        var fParameter = queryablesOperation.GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "f");

        fParameter.GetProperty("schema")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .Contain(["schemajson", "schema+json"]);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features/conformance")]
    public async Task GetConformance_DoesNotAdvertiseGmlSf0()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features/conformance");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("conformsTo")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .NotContain("http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/gml-sf0");
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
    public async Task GetItems_WithCsvFormat_ReturnsCsvResponse()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items?f=csv&limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("id");
        content.Should().Contain("geometry");
        content.Should().Contain("\"\"coordinates\"\":");
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
    public async Task GetItems_WithWeightedAcceptHeader_UsesHighestQualityMatch()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/gml+xml;version=3.2;q=0.1"));
        client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;q=1.0"));

        var response = await client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task GetItems_WithGeoJsonRejectedAndWildcardAllowed_UsesAlternateFeatureFormat()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/ogc/features/collections/{TestCollectionId}/items?limit=1");
        request.Headers.TryAddWithoutValidation("Accept", "application/geo+json;q=0, */*;q=1");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/geo+json");
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
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/features")]
    public async Task GetLandingPage_IncludesDatasetMapLink()
    {
        var response = await _fixture.Client.GetAsync("/ogc/features?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();

        links.Any(link =>
                link.TryGetProperty("rel", out var rel) &&
                rel.GetString() == RelationTypes.Map &&
                link.TryGetProperty("href", out var href) &&
                href.GetString() != null &&
                href.GetString()!.Contains("/ogc/maps/map", StringComparison.Ordinal))
            .Should()
            .BeTrue();
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

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task GetSingleItem_WithCsvFormat_ReturnsCsvResponse()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/features/collections/{TestCollectionId}/items/1?f=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("id");
        content.Should().Contain("geometry");
        content.Should().Contain("\"\"coordinates\"\":");
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

        // Layer 0 has TimeInfo {start: timestamp, end: event_date}, so the
        // datetime filter applies interval-intersection semantics shared with
        // GeoServices REST query?time= (see #379 docs/temporal-animation-api.md):
        // a row matches when its [timestamp, COALESCE(event_date, timestamp)]
        // interval overlaps [2023-01-01, 2023-01-10]. F1 (2023-01-02 → 2024-01-01),
        // F2 (2023-01-05 → 2024-06-15), and F4 (2022-12-31 → 2024-10-15) all
        // overlap; F3 starts 2023-02-10 (after end), F5 collapses to 2023-01-20
        // (after end).
        ids.Should().Equal(1, 2, 4);
    }

    #endregion
}

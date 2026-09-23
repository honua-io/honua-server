// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

[Collection("Database.OgcApiTiles")]
[Protocol(TestProtocols.OgcApiMaps)]
public class OgcMapsBasicTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0; // Use existing test layer
    private static readonly string[] OptionalLinkFieldNames = ["rel", "type", "title"];

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static double[] ReadLandingBbox(JsonDocument json)
    {
        var bbox = json.RootElement.GetProperty("extent").GetProperty("spatial").GetProperty("bbox")
            .EnumerateArray().First().EnumerateArray()
            .Select(value => value.GetDouble()).ToArray();
        bbox.Should().HaveCount(4);
        var isWholeWorld = Math.Abs(bbox[0] + 180d) <= 1e-9
            && Math.Abs(bbox[1] + 90d) <= 1e-9
            && Math.Abs(bbox[2] - 180d) <= 1e-9
            && Math.Abs(bbox[3] - 90d) <= 1e-9;
        isWholeWorld.Should().BeFalse(
            "an omitted or real dataset extent must not be replaced with the whole world");
        return bbox;
    }

    private async Task<string> StoreCollectionStyleAsync()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        snapshot.Index.ResourcesByStorageLayerId.TryGetValue(TestLayerId, out var resource).Should().BeTrue();
        resource.Should().NotBeNull();
        resource!.Metadata.Name.Should().NotBeNullOrWhiteSpace();

        var catalog = _fixture.GetService<ILayerStyleCatalog>();
        await catalog.SetMapLibreStyleAsync(
            TestLayerId,
            "{\"version\":8,\"sources\":{},\"layers\":[]}");
        return resource.Metadata.Name!;
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps")]
    [Operation(Operations.Metadata)]
    public async Task GetLandingPage_BasicRequest_ReturnsLandingPage()
    {
        var response = await _fixture.Client.GetAsync("/ogc/maps");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("title").GetString().Should().Be("Honua OGC API Maps");
        json.RootElement.GetProperty("links").EnumerateArray()
            .Select(link => link.GetProperty("href").GetString())
            .Should()
            .Contain(href => href != null && href.EndsWith("/ogc/maps/openapi.json", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps")]
    [Operation(Operations.Metadata)]
    public async Task GetLandingPage_AdvertisesTheSeededDatasetExtent()
    {
        var response = await _fixture.Client.GetAsync("/ogc/maps");
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        using var json = JsonDocument.Parse(content);

        // Layer 0 is the only seeded Maps resource that declares a bbox: -123, 37, -122, 38 in CRS84.
        var bbox = ReadLandingBbox(json);
        bbox[0].Should().BeApproximately(-123, 1e-6);
        bbox[1].Should().BeApproximately(37, 1e-6);
        bbox[2].Should().BeApproximately(-122, 1e-6);
        bbox[3].Should().BeApproximately(38, 1e-6);
        json.RootElement.GetProperty("crs").EnumerateArray().Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps")]
    [Operation(Operations.Metadata)]
    public async Task GetLandingPage_MixedCrsExtent_AgreesWithPostGisBoundaryOracle()
    {
        const double projectedMinX = 530000;
        const double projectedMinY = 180000;
        const double projectedMaxX = 531000;
        const double projectedMaxY = 181000;
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var resource = snapshot.Graph.Resources.Single(candidate => candidate.Metadata.Id == "res-layer-1");
        var original = resource.Spatial;
        _fixture.UpdateV2ResourceMetadata(1, spatial: new MetadataV2ResourceSpatial
        {
            SpatialReference = new MetadataV2SpatialReference { Srid = 27700, Crs = "EPSG:27700" },
            GeometryType = MetadataV2GeometryType.Point,
            PrimaryGeometryField = "shape",
            Bbox = new MetadataV2Bbox
            {
                West = projectedMinX,
                South = projectedMinY,
                East = projectedMaxX,
                North = projectedMaxY,
            },
        });

        try
        {
            var response = await _fixture.Client.GetAsync("/ogc/maps");
            var content = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, content);
            using var json = JsonDocument.Parse(content);
            var bbox = ReadLandingBbox(json);

            await using var connection = await _fixture.Postgres.GetConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ST_XMin(g), ST_YMin(g), ST_XMax(g), ST_YMax(g)
                FROM (
                    SELECT ST_Transform(
                        ST_Segmentize(
                            ST_Boundary(ST_MakeEnvelope(@minX, @minY, @maxX, @maxY, @fromSrid)),
                            250),
                        @toSrid) AS g
                ) transformed
                """;
            command.Parameters.AddWithValue("minX", projectedMinX);
            command.Parameters.AddWithValue("minY", projectedMinY);
            command.Parameters.AddWithValue("maxX", projectedMaxX);
            command.Parameters.AddWithValue("maxY", projectedMaxY);
            command.Parameters.AddWithValue("fromSrid", 27700);
            command.Parameters.AddWithValue("toSrid", 4326);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            var oracle = (
                MinX: reader.GetDouble(0),
                MinY: reader.GetDouble(1),
                MaxX: reader.GetDouble(2),
                MaxY: reader.GetDouble(3));

            const double seededMinX = -123;
            const double seededMinY = 37;
            const double seededMaxX = -122;
            const double seededMaxY = 38;
            bbox[0].Should().BeApproximately(Math.Min(seededMinX, oracle.MinX), 0.02);
            bbox[1].Should().BeApproximately(Math.Min(seededMinY, oracle.MinY), 0.02);
            bbox[2].Should().BeApproximately(Math.Max(seededMaxX, oracle.MaxX), 0.02);
            bbox[3].Should().BeApproximately(Math.Max(seededMaxY, oracle.MaxY), 0.02);
            (bbox[2] - bbox[0]).Should().BeGreaterThan(10,
                "the transformed British National Grid box must widen the seeded California extent");
        }
        finally
        {
            _fixture.UpdateV2ResourceMetadata(1, spatial: original);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/map")]
    [Operation(Operations.Metadata)]
    public async Task DatasetMap_WithExplicitBbox_DoesNotFailAsAnUnmappedServerError()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/map?bbox=-122.6,37.4,-122.4,37.6&bbox-crs=http://www.opengis.net/def/crs/OGC/1.3/CRS84&f=png");
        ((int)response.StatusCode).Should().NotBe((int)HttpStatusCode.InternalServerError);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps")]
    [Operation(Operations.Metadata)]
    public async Task GetLandingPage_SecondAnonymousRead_IsNotASharedCacheReplay()
    {
        using var first = await _fixture.Client.GetAsync("/ogc/maps");
        using var second = await _fixture.Client.GetAsync("/ogc/maps");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.Age.Should().BeNull(
            "the landing page is per-principal and must not be stored in the shared output cache");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps")]
    [Operation(Operations.Metadata)]
    public async Task GetLandingPage_LinkObjects_OmitNullOptionalFields()
    {
        // The OGC API Maps `link` schema types `templated` as boolean and
        // `hreflang` as string. Emitting `"templated": null` / `"hreflang": null`
        // for unset optional fields is non-conformant, so the serializer must
        // omit them rather than write null. (Validated by the OGC API Maps
        // conformance gate against the published bundled OpenAPI.)
        var response = await _fixture.Client.GetAsync("/ogc/maps");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().NotBeEmpty();

        foreach (var link in links)
        {
            // Required field always present.
            link.TryGetProperty("href", out _).Should().BeTrue();

            // Optional fields, when present, must carry the correct JSON type —
            // never an explicit null.
            if (link.TryGetProperty("templated", out var templated))
            {
                templated.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
            }

            if (link.TryGetProperty("hreflang", out var hreflang))
            {
                hreflang.ValueKind.Should().Be(JsonValueKind.String);
            }

            foreach (var name in OptionalLinkFieldNames.Where(name => link.TryGetProperty(name, out _)))
            {
                link.GetProperty(name).ValueKind.Should().NotBe(JsonValueKind.Null);
            }
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/openapi.json")]
    [Operation(Operations.Metadata)]
    public async Task GetOpenApiSpec_BasicRequest_ReturnsOpenApiDocument()
    {
        var response = await _fixture.Client.GetAsync("/ogc/maps/openapi.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.oai.openapi+json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("openapi").GetString().Should().NotBeNullOrWhiteSpace();
        json.RootElement.GetProperty("paths").TryGetProperty("/ogc/maps", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/openapi.json")]
    [Operation(Operations.Metadata)]
    public async Task GetOpenApiSpec_DocumentsSecuritySchemesAndProtectedResponses()
    {
        var response = await _fixture.Client.GetAsync("/ogc/maps/openapi.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var components = json.RootElement.GetProperty("components");
        var securitySchemes = components.GetProperty("securitySchemes");
        securitySchemes.TryGetProperty("ApiKeyAuth", out _).Should().BeTrue();
        securitySchemes.TryGetProperty("BearerAuth", out _).Should().BeTrue();

        var collectionMap = json.RootElement.GetProperty("paths")
            .GetProperty("/ogc/maps/collections/{collectionId}/map")
            .GetProperty("get");
        collectionMap.TryGetProperty("security", out var security).Should().BeTrue();
        security.ValueKind.Should().Be(JsonValueKind.Array);
        collectionMap.GetProperty("responses").TryGetProperty("401", out _).Should().BeTrue();
        collectionMap.GetProperty("responses").TryGetProperty("402", out _).Should().BeTrue();
        collectionMap.GetProperty("responses").TryGetProperty("403", out _).Should().BeTrue();

        var datasetMap = json.RootElement.GetProperty("paths")
            .GetProperty("/ogc/maps/map")
            .GetProperty("get");
        datasetMap.GetProperty("responses").TryGetProperty("402", out _).Should().BeTrue();

        var styledCollectionMap = json.RootElement.GetProperty("paths")
            .GetProperty("/ogc/maps/collections/{collectionId}/styles/{styleId}/map")
            .GetProperty("get");
        styledCollectionMap.TryGetProperty("security", out _).Should().BeTrue();
        styledCollectionMap.GetProperty("parameters")
            .EnumerateArray()
            .Any(parameter => parameter.TryGetProperty("$ref", out var reference)
                && reference.GetString() == "#/components/parameters/datetime")
            .Should()
            .BeTrue();
        styledCollectionMap.GetProperty("responses").TryGetProperty("402", out _).Should().BeTrue();
        styledCollectionMap.GetProperty("responses").TryGetProperty("501", out _).Should().BeTrue();

        var tilesets = json.RootElement.GetProperty("paths")
            .GetProperty("/ogc/maps/collections/{collectionId}/map/tiles")
            .GetProperty("get");
        tilesets.GetProperty("responses").TryGetProperty("401", out _).Should().BeTrue();
        tilesets.GetProperty("responses").TryGetProperty("403", out _).Should().BeTrue();

        components.GetProperty("parameters")
            .GetProperty("datetime")
            .GetProperty("description")
            .GetString()
            .Should()
            .Contain("402 Payment Required")
            .And.Contain("raster.temporal-mosaic");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/conformance")]
    [Operation(Operations.Metadata)]
    public async Task GetConformance_BasicRequest_ReturnsConformanceClasses()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/ogc/maps/conformance");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        // Verify conformance response structure
        json.RootElement.TryGetProperty("conformsTo", out var conformsTo).Should().BeTrue();
        conformsTo.EnumerateArray().Should().NotBeEmpty();

        // Verify that it includes OGC API - Maps conformance classes
        var conformanceClasses = conformsTo.EnumerateArray()
            .Select(c => c.GetString())
            .ToArray();

        conformanceClasses.Should().Contain(c => c != null && c.Contains("ogcapi-maps"));
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}")]
    [Operation(Operations.Metadata)]
    public async Task GetCollection_MapServingCollection_ReturnsDescriptionWhoseMapLinksRender()
    {
        // #4991: GDAL's OGCAPI driver (QGIS's OGC API - Maps provider) reads the collection
        // resource before it requests /map, so a collection that renders must also describe
        // itself. GDAL takes the map URL only from the http:// relation with a media type; for any
        // other link it keeps whichever comes last, so without it QGIS requested the
        // rel=alternate HTML link as the map.
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();

        var links = root.GetProperty("links").EnumerateArray().ToArray();
        var mapLinks = links
            .Where(link => link.GetProperty("rel").GetString() is "https://www.opengis.net/def/rel/ogc/1.0/map" or "[ogc-rel:map]" or "http://www.opengis.net/def/rel/ogc/1.0/map")
            .ToArray();
        mapLinks.Select(link => link.GetProperty("rel").GetString()).Should().BeEquivalentTo(
            ["https://www.opengis.net/def/rel/ogc/1.0/map", "[ogc-rel:map]", "http://www.opengis.net/def/rel/ogc/1.0/map"]);
        links.Should().Contain(link =>
            link.GetProperty("rel").GetString() == "self"
            && link.GetProperty("href").GetString()!.EndsWith($"/ogc/maps/collections/{root.GetProperty("id").GetString()}", StringComparison.Ordinal));

        foreach (var mapLink in mapLinks)
        {
            mapLink.GetProperty("type").GetString().Should().Be("image/png");
            var mapPath = new Uri(mapLink.GetProperty("href").GetString()!).AbsolutePath;
            mapPath.Should().EndWith("/map");

            // The advertised link must resolve: follow it the way GDAL does, with a bbox window.
            var map = await _fixture.Client.GetAsync($"{mapPath}?bbox=-180,-90,180,90&width=64&height=64");
            map.StatusCode.Should().Be(HttpStatusCode.OK, $"the advertised map link {mapPath} must render");
            map.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }

        // GDAL georeferences the map from extent.spatial.bbox; it must be the resource extent.
        var resource = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[TestLayerId];
        var bbox = resource.ReadBbox();
        if (bbox is null)
        {
            root.TryGetProperty("extent", out _).Should().BeFalse("a resource without an extent advertises none");
        }
        else
        {
            var advertised = root.GetProperty("extent").GetProperty("spatial").GetProperty("bbox")[0]
                .EnumerateArray().Select(value => value.GetDouble()).ToArray();
            advertised.Should().HaveCount(4);
            advertised[0].Should().BeLessThanOrEqualTo(advertised[2]);
            advertised[1].Should().BeLessThanOrEqualTo(advertised[3]);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}")]
    [Operation(Operations.Metadata)]
    public async Task GetCollection_NonExistentCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync("/ogc/maps/collections/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}")]
    [Operation(Operations.Metadata)]
    public async Task GetCollection_ProtectedCollection_RefusesAnonymousWith401AndAdmitsEntitledCaller()
    {
        // #4991: a protected collection answers anonymous callers with 401, on the collection
        // resource and on its map, instead of the 404 that told clients it did not exist.
        const string entitledRole = "maps-reader";
        const string referer = "https://ogcapi-maps-collection-proof.example/";
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureWebHost(builder =>
        {
            // Displace the development-authentication bypass, which makes every caller an admin.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await fixture.InitializeAsync();
        fixture.UpdateV2ResourceMetadata(
            TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [entitledRole] });

        var token = (await fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest(
                "maps-analyst",
                "maps-analyst",
                TenantId: null,
                Roles: [entitledRole],
                PortalTokenClientType.Referer,
                referer,
                DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None)).Token;

        var collectionPath = $"/ogc/maps/collections/{TestLayerId}";
        var mapPath = $"{collectionPath}/map?bbox=-180,-90,180,90&width=64&height=64";
        foreach (var path in new[] { collectionPath, mapPath })
        {
            using var anonymousClient = fixture.CreateClient();
            using var anonymous = await anonymousClient.GetAsync(path);
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"anonymous {path}");

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Referrer = new Uri(referer);
            using var entitledClient = fixture.CreateClient();
            using var entitled = await entitledClient.SendAsync(request);
            entitled.StatusCode.Should().Be(HttpStatusCode.OK, $"entitled {path}: {await entitled.Content.ReadAsStringAsync()}");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_WithValidParameters_ReturnsMap()
    {
        // Arrange
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_WithStringCollectionId_ReturnsMap()
    {
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/Test%20Layer/map{queryParams}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_WhenSuccessful_IncludesContentHeaders()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Content-Bbox").Should().BeTrue();
        response.Headers.TryGetValues("Content-Bbox", out var bboxValues).Should().BeTrue();
        bboxValues.Should().NotBeNull();
        using var enumerator = bboxValues!.GetEnumerator();
        enumerator.MoveNext().Should().BeTrue();
        enumerator.Current.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_UnknownFormat_ReturnsBadRequest()
    {
        // Arrange - "json" is not a valid OGC Maps format
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=json";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    [Operation(Operations.Render)]
    public async Task GetStyledMap_VectorCollectionTiff_ReturnsCleanErrorNeverPngLabeledTiff()
    {
        // Regression for #2365: the Skia styled-vector renderer (ADR-0048) has no TIFF
        // encoder and silently fell back to PNG, while the response Content-Type was set
        // from the requested format — so f=tiff on a vector collection returned PNG bytes
        // mislabeled as image/tiff, corrupting GDAL/rasterio clients. TIFF is only encodable
        // on the GDAL raster-coverage path, so the styled-vector path must reject it cleanly.
        // Layer 0 is a seeded vector (Point) collection, so this exercises the Skia path.
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=tiff";
        var styleId = await StoreCollectionStyleAsync();

        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/styles/{Uri.EscapeDataString(styleId)}/map{queryParams}");

        // The invariant: never return 200 with image/tiff carrying PNG bytes. A clean 4xx is required.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("image/tiff");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/map")]
    [Operation(Operations.Render)]
    public async Task GetDatasetMap_WithCollections_ReturnsMap()
    {
        // Arrange
        var queryParams = $"?collections={TestLayerId}&bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/map")]
    [Operation(Operations.Render)]
    public async Task GetDatasetMap_WithoutCollections_ReturnsMap()
    {
        // Arrange
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/map")]
    [Operation(Operations.Render)]
    public async Task GetDatasetMap_WithMisalignedRasterCollections_ReturnsMap()
    {
        const string rasterName = "ogc-maps-misaligned-grid-2487";
        var dataSource = _fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();

        try
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO honua.raster_data (layer_id, name, description, raster)
                    VALUES (
                        1,
                        @name,
                        'Offset raster grid for OGC dataset-map regression coverage',
                        ST_AddBand(
                            ST_MakeEmptyRaster(64, 64, -122.49, 37.84, 0.00234375, -0.0021875, 0, 0, 4326),
                            '8BUI'::text,
                            64,
                            0));
                    """;
                insert.Parameters.AddWithValue("name", rasterName);
                await insert.ExecuteNonQueryAsync();
            }

            var response = await _fixture.Client.GetAsync(
                "/ogc/maps/map?collections=0,1&bbox=-123,37,-122,38&width=256&height=256&f=png");

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "dataset rasters must be normalized to a shared grid before the PostGIS mosaic (#2487)");
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
            (await response.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM honua.raster_data WHERE name = @name;";
            cleanup.Parameters.AddWithValue("name", rasterName);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    [Operation(Operations.Render)]
    public async Task GetStyledMap_WithValidStyle_ReachesStyledMapEndpoint()
    {
        // Arrange
        var styleId = "default";
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}/styles/{styleId}/map{queryParams}");

        // Assert
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.NotFound,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    [Operation(Operations.Render)]
    public async Task GetStyledMap_VectorCollection_RendersPngInsteadOf501()
    {
        // The styled-map endpoint routes vector collections through the Skia rendering
        // pipeline (ADR-0048) instead of the raster-only path that returns 501. Layer 0
        // is a seeded vector (Point) collection, so a styled request must produce a PNG.
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";
        var styleId = await StoreCollectionStyleAsync();

        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/styles/{Uri.EscapeDataString(styleId)}/map{queryParams}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.StatusCode.Should().NotBe(HttpStatusCode.NotImplemented);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().NotBeEmpty();

        // PNG signature: 89 50 4E 47 0D 0A 1A 0A.
        bytes.Should().HaveCountGreaterThan(8);
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50);
        bytes[2].Should().Be(0x4E);
        bytes[3].Should().Be(0x47);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map/tiles")]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSets_ValidCollection_ReturnsTileSetMetadata()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}/map/tiles");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        json.RootElement.TryGetProperty("tilesets", out var tilesets).Should().BeTrue();
        tilesets.ValueKind.Should().Be(JsonValueKind.Array);
        json.RootElement.TryGetProperty("links", out _).Should().BeTrue();
        tilesets.GetArrayLength().Should().BeGreaterThan(0);

        var firstTileSet = tilesets.EnumerateArray().First();
        firstTileSet.TryGetProperty("crs", out _).Should().BeTrue();
        firstTileSet.TryGetProperty("tileMatrixSetURI", out _).Should().BeTrue();
        firstTileSet.TryGetProperty("links", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map/tiles")]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSets_TilingSchemeLinksResolve()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}/map/tiles");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var tilingLinks = json.RootElement.GetProperty("tilesets").EnumerateArray()
            .SelectMany(tileSet => tileSet.GetProperty("links").EnumerateArray())
            .Where(link => link.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme")
            .Select(link => link.GetProperty("href").GetString())
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .ToArray();

        tilingLinks.Should().NotBeEmpty();

        foreach (var href in tilingLinks)
        {
            var tilingResponse = await _fixture.Client.GetAsync(href);
            tilingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map/tiles/{tileMatrixSetId}")]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSet_ValidCollectionAndTileMatrixSet_ReturnsTileSetMetadata()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map/tiles/WebMercatorQuad");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("tileMatrixSetId").GetString().Should().Be("WebMercatorQuad");
        var links = json.RootElement.GetProperty("links").EnumerateArray().ToArray();
        links.Should().Contain(link => link.GetProperty("rel").GetString() == "self");
        links.Should().Contain(link => link.GetProperty("rel").GetString() == "item");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_InvalidCollectionId_ReturnsNotFound()
    {
        // Arrange
        var invalidCollectionId = "invalid";
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{invalidCollectionId}/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_NonExistentCollection_ReturnsNotFound()
    {
        // Arrange
        var nonExistentCollectionId = 99999;
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{nonExistentCollectionId}/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

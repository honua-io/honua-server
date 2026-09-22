// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Dataset-map landing discovery, CRS and caller-visible extent regressions.
/// </summary>
[Collection("Database.OgcApiTiles")]
[Protocol(TestProtocols.OgcApiMaps)]
public sealed class OgcMapsLandingDatasetMetadataTests
{
    private const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string Mercator = "http://www.opengis.net/def/crs/EPSG/0/3857";
    private const string Role = "maps-dataset-reader";
    private const string Referer = "https://maps-dataset-metadata.example/";
    private readonly ITestOutputHelper _output;

    public OgcMapsLandingDatasetMetadataTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps")]
    [Endpoint("GET /ogc/maps/map")]
    public async Task Landing_MixedCrsDataset_DescribesTheSameAccessibleRenderViewport()
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddCollection(builder, "geographic", 10, Spatial(4326, -10, 20, 10, 30));
        AddCollection(builder, "projected", 11, Spatial(3857, 1113194.9079327357, 0, 2226389.8158654715, 1118889.9748579594));
        var renderer = Substitute.For<IRasterMapRenderer>();
        MapRenderRequest? rendered = null;
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                rendered = call.Arg<MapRenderRequest>();
                return Task.FromResult(new RasterResult { Data = [137, 80, 78, 71], ContentType = "image/png", Width = 32, Height = 32 });
            });
        await using var fixture = CreateFixture(builder.Build(), renderer);
        await fixture.InitializeAsync();
        using var landing = await ReadLandingAsync(fixture.Client);
        AssertBbox(landing.RootElement, [-10, 0, 20, 30]);
        landing.RootElement.GetProperty("crs").EnumerateArray().Select(value => value.GetString())
            .Should().Equal(Crs84);
        landing.RootElement.TryGetProperty("storageCrs", out _).Should().BeFalse("a mixed dataset has no single storage CRS");

        var links = landing.RootElement.GetProperty("links").EnumerateArray().ToArray();
        foreach (var relation in new[] { "https://www.opengis.net/def/rel/ogc/1.0/map", "http://www.opengis.net/def/rel/ogc/1.0/map" })
        {
            var link = links.Single(item => item.GetProperty("rel").GetString() == relation);
            link.GetProperty("type").GetString().Should().Be("image/png");
            var path = new Uri(link.GetProperty("href").GetString()!).AbsolutePath;
            path.Should().Be("/ogc/maps/map");
            using var response = await fixture.Client.GetAsync(path + "?width=32&height=32");
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        rendered.Should().NotBeNull();
        rendered!.Value.BoundingBox.Should().HaveCount(4);
        for (var index = 0; index < 4; index++)
        {
            rendered.Value.BoundingBox[index].Should().BeApproximately(new double[] { -10, 0, 20, 30 }[index], 1e-7);
        }
        rendered.Value.ResolvedLayers!.Select(layer => layer.ResourceId).Should().Equal("geographic", "projected");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps")]
    public async Task Landing_UniformProjectedDataset_TransformsExtentAndAdvertisesNativeCrs()
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddCollection(builder, "projected", 10, Spatial(3857, 1113194.9079327357, 0, 2226389.8158654715, 1118889.9748579594));
        await using var fixture = CreateFixture(builder.Build());
        await fixture.InitializeAsync();
        using var landing = await ReadLandingAsync(fixture.Client);
        AssertBbox(landing.RootElement, [10, 0, 20, 10]);
        landing.RootElement.GetProperty("crs").EnumerateArray().Select(value => value.GetString())
            .Should().BeEquivalentTo(Crs84, Mercator);
        landing.RootElement.GetProperty("storageCrs").GetString().Should().Be(Mercator);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps")]
    public async Task Landing_CollidingDisabledBinding_UsesTheMapsResourceExtent()
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddCollection(builder, "image-only", 10, Spatial(4326, -170, -80, 170, 80), mapsEnabled: false);
        AddCollection(builder, "maps", 10, Spatial(4326, 1, 2, 3, 4));
        await using var fixture = CreateFixture(builder.Build());
        await fixture.InitializeAsync();
        using var landing = await ReadLandingAsync(fixture.Client);
        AssertBbox(landing.RootElement, [1, 2, 3, 4]);
    }

    [IntegrationTheory]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Landing_ProtectedExtent_AuthenticatedResponseDoesNotLeakToAnonymous(bool protectService)
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddCollection(builder, "public", 10, Spatial(4326, 1, 2, 3, 4));
        AddCollection(builder, "protected", 11,
            Spatial(3857, 11131949.079327356, 6446275.841017161, 12245143.987260092, 8399737.889818357),
            protectedResource: !protectService, protectedService: protectService);
        await using var fixture = CreateFixture(builder.Build());
        await fixture.InitializeAsync();
        var token = (await fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest("maps-reader", "maps-reader", TenantId: null, Roles: [Role],
                PortalTokenClientType.Referer, Referer, DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None)).Token;
        using var entitled = fixture.CreateClient();
        entitled.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        entitled.DefaultRequestHeaders.Referrer = new Uri(Referer);
        using (var landing = await ReadLandingAsync(entitled))
        {
            AssertBbox(landing.RootElement, [1, 2, 110, 60]);
            landing.RootElement.GetProperty("crs").EnumerateArray().Select(value => value.GetString())
                .Should().Equal(Crs84);
        }
        using var anonymous = fixture.CreateClient();
        using (var landing = await ReadLandingAsync(anonymous))
        {
            AssertBbox(landing.RootElement, [1, 2, 3, 4]);
            landing.RootElement.GetProperty("crs").EnumerateArray().Select(value => value.GetString())
                .Should().BeEquivalentTo(Crs84, "http://www.opengis.net/def/crs/EPSG/0/4326");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps")]
    public async Task Landing_OnlyProtectedCollections_DoesNotExposeTheirBounds()
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddCollection(builder, "protected", 10, Spatial(4326, 100, 50, 110, 60), protectedResource: true);
        await using var fixture = CreateFixture(builder.Build());
        await fixture.InitializeAsync();
        using var anonymous = fixture.CreateClient();
        using var landing = await ReadLandingAsync(anonymous);
        landing.RootElement.TryGetProperty("extent", out _).Should().BeFalse();
        landing.RootElement.TryGetProperty("storageCrs", out _).Should().BeFalse();
        landing.RootElement.GetProperty("crs").GetArrayLength().Should().Be(0);
    }

    [IntegrationTheory]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps")]
    [InlineData(4326, true)]
    [InlineData(27700, false)]
    public async Task Landing_CollectionWithoutKnownBounds_DoesNotInventWorldExtent(int srid, bool supportsCrs84)
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddCollection(builder, "unknown-bounds", 10, new MetadataV2ResourceSpatial
        {
            SpatialReference = new MetadataV2SpatialReference { Srid = srid }
        });
        await using var fixture = CreateFixture(builder.Build());
        await fixture.InitializeAsync();
        using var landing = await ReadLandingAsync(fixture.Client);
        landing.RootElement.TryGetProperty("extent", out _).Should().BeFalse();
        landing.RootElement.GetProperty("crs").EnumerateArray().Select(value => value.GetString())
            .Contains(Crs84).Should().Be(supportsCrs84,
                "an unbounded projected resource provides no envelope with which to establish CRS84 support");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps")]
    public async Task Landing_UntransformableMixedCrs_PreservesDiscoveryWithoutAnInventedExtent()
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddCollection(builder, "geographic", 10, Spatial(4326, -10, 20, 10, 30));
        // A genuinely unavailable SRID is distinct from a provider-supported projected CRS.
        AddCollection(builder, "unavailable-grid", 11, Spatial(999999, 500000, 100000, 600000, 200000));
        await using var fixture = CreateFixture(builder.Build());
        await fixture.InitializeAsync();
        using var landing = await ReadLandingAsync(fixture.Client);
        landing.RootElement.GetProperty("title").GetString().Should().Be("Honua OGC API Maps");
        landing.RootElement.TryGetProperty("extent", out _).Should().BeFalse();
        landing.RootElement.TryGetProperty("storageCrs", out _).Should().BeFalse();
        landing.RootElement.GetProperty("crs").GetArrayLength().Should().Be(0,
            "CRS84 must not be advertised when the provider cannot transform the mixed dataset");
        using var response = await fixture.Client.GetAsync("/ogc/maps/map?width=32&height=32");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unavailable default transform is a request limitation, not a server error or partial viewport");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps")]
    [Endpoint("GET /ogc/maps/map")]
    public async Task Landing_BritishGridDataset_UsesPostGisExtentForDiscoveryAndDefaultRendering()
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddCollection(builder, "geographic", 10, Spatial(4326, -2, 50, -1, 51));
        AddCollection(builder, "british-grid", 11, Spatial(27700, 500000, 100000, 600000, 200000));
        var renderer = Substitute.For<IRasterMapRenderer>();
        MapRenderRequest? rendered = null;
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                rendered = call.Arg<MapRenderRequest>();
                return Task.FromResult(new RasterResult { Data = [137, 80, 78, 71], ContentType = "image/png", Width = 32, Height = 32 });
            });
        await using var fixture = CreateFixture(builder.Build(), renderer);
        await fixture.InitializeAsync();
        // Independent SQL boundary oracle, rather than the implementation's sampled-point query.
        await using var connection = await fixture.GetService<IAdoNetDatabaseConnectionProvider>()
            .OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ST_XMin(bounds), ST_YMin(bounds), ST_XMax(bounds), ST_YMax(bounds)
            FROM (SELECT Box2D(ST_Transform(ST_Segmentize(
                ST_ExteriorRing(ST_MakeEnvelope(500000,100000,600000,200000,27700)),
                3125),4326)) AS bounds) projected
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var expected = new[] { Math.Min(-2, reader.GetDouble(0)), Math.Min(50, reader.GetDouble(1)),
            Math.Max(-1, reader.GetDouble(2)), Math.Max(51, reader.GetDouble(3)) };
        using var landingResponse = await fixture.Client.GetAsync("/ogc/maps");
        var landingBody = await landingResponse.Content.ReadAsStringAsync();
        using var response = await fixture.Client.GetAsync("/ogc/maps/map?width=32&height=32");
        var mapBody = await response.Content.ReadAsStringAsync();
        _output.WriteLine("Landing response: " + landingBody);
        _output.WriteLine("Dataset map response: " + mapBody);
        // Observe both routes before asserting, so missing metadata cannot mask a renderer failure.
        response.StatusCode.Should().Be(HttpStatusCode.OK, mapBody);
        landingResponse.StatusCode.Should().Be(HttpStatusCode.OK, landingBody);
        using var landing = JsonDocument.Parse(landingBody);
        // The shared provider samples four segments per edge; the independent oracle uses 32.
        AssertBbox(landing.RootElement, expected);
        landing.RootElement.GetProperty("crs").EnumerateArray().Select(value => value.GetString())
            .Should().Equal(Crs84);
        rendered.Should().NotBeNull();
        for (var index = 0; index < expected.Length; index++)
        {
            rendered!.Value.BoundingBox[index].Should().BeApproximately(expected[index], 1e-7);
        }
        var advertised = landing.RootElement.GetProperty("extent").GetProperty("spatial")
            .GetProperty("bbox")[0].EnumerateArray().Select(value => value.GetDouble()).ToArray();
        rendered!.Value.BoundingBox.Should().Equal(advertised);
        rendered.Value.ResolvedLayers!.Select(layer => layer.ResourceId).Should().Equal("geographic", "british-grid");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task DatasetMap_ExplicitBbox_DoesNotComputeAnUnusedMixedCrsDefault()
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddCollection(builder, "geographic", 10, Spatial(4326, -2, 50, -1, 51));
        AddCollection(builder, "british-grid", 11, Spatial(27700, 500000, 100000, 600000, 200000));
        var renderer = Substitute.For<IRasterMapRenderer>();
        MapRenderRequest? rendered = null;
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                rendered = call.Arg<MapRenderRequest>();
                return Task.FromResult(new RasterResult { Data = [137, 80, 78, 71], ContentType = "image/png", Width = 32, Height = 32 });
            });
        var transformService = Substitute.For<ICoordinateTransformService>();
        await using var fixture = CreateFixture(builder.Build(), renderer)
            .ConfigureServices(services =>
            {
                services.RemoveAll<ICoordinateTransformService>();
                services.AddSingleton(transformService);
            });
        await fixture.InitializeAsync();
        using var response = await fixture.Client.GetAsync("/ogc/maps/map?bbox=-3,49,2,53&width=32&height=32");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        rendered.Should().NotBeNull();
        rendered!.Value.BoundingBox.Should().Equal(-3, 49, 2, 53);
        await transformService.DidNotReceiveWithAnyArgs().TransformExtentAsync(0, 0, 0, 0, 0, 0, CancellationToken.None);
    }

    private static WebAppFixture CreateFixture(MetadataV2Graph graph, IRasterMapRenderer? renderer = null)
        => new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Cache:ResponseCachingEnabled", "true");
            })
            .ConfigureServices(services =>
            {
                services.PostConfigure<GeocodingConfiguration>(options =>
                    options.Providers.Nominatim = options.Providers.Nominatim with { Enabled = false });
                services.RemoveAll<IMetadataV2GraphProvider>();
                services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph));
                if (renderer is not null)
                {
                    services.RemoveAll<IRasterMapRenderer>();
                    services.AddSingleton(renderer);
                }
            });

    private static void AddCollection(TestMetadataV2GraphBuilder builder, string id, int layerId,
        MetadataV2ResourceSpatial spatial, bool mapsEnabled = true,
        bool protectedResource = false, bool protectedService = false)
    {
        var open = new AccessPolicy { AllowAnonymous = true };
        var restricted = new AccessPolicy { AllowAnonymous = false, AllowedRoles = [Role] };
        builder.AddResource(id, id, MetadataV2ResourceType.FeatureDataset, spatial: spatial,
                accessPolicy: protectedResource ? restricted : open)
            .AddStorageBinding(id + "-binding", id, "public.features", storageLayerId: layerId)
            .AddService(id + "-service", id + "-service", protocols: mapsEnabled ? ["OGC-API-Maps"] : ["ImageServer"],
                accessPolicy: protectedService ? restricted : open)
            .AddPublication(id + "-publication", id + "-service", id, layerIndex: layerId,
                storageBindingId: id + "-binding", publicationType: MetadataV2PublicationType.OgcCollection);
    }

    private static MetadataV2ResourceSpatial Spatial(int srid, double west, double south, double east, double north)
        => new()
        {
            SpatialReference = new MetadataV2SpatialReference { Srid = srid },
            Bbox = new MetadataV2Bbox { West = west, South = south, East = east, North = north }
        };

    private static async Task<JsonDocument> ReadLandingAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/ogc/maps");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static void AssertBbox(JsonElement landing, double[] expected, double tolerance = 1e-7)
    {
        landing.TryGetProperty("extent", out var extent).Should().BeTrue(landing.GetRawText());
        var spatial = extent.GetProperty("spatial");
        spatial.GetProperty("crs").GetString().Should().Be(Crs84);
        var boxes = spatial.GetProperty("bbox").EnumerateArray().ToArray();
        boxes.Should().ContainSingle();
        var actual = boxes[0].EnumerateArray().Select(value => value.GetDouble()).ToArray();
        actual.Should().HaveCount(4);
        for (var index = 0; index < expected.Length; index++)
        {
            actual[index].Should().BeApproximately(expected[index], tolerance);
        }
    }
}

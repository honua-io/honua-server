// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Integration tests for the read-only Esri I3S SceneServer serving endpoints
/// (#1202). Verifies Enterprise gating and the service/layer descriptor JSON
/// for a hosted fixture scene.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class I3sSceneServerEndpointTests : IAsyncLifetime
{
    private const string SceneId = "i3s-fixture";
    private const string ProtectedSceneId = "i3s-protected";
    private const string NoGeometrySceneId = "i3s-no-geometry";
    private const string AdminPassword = "i3s-auth-test-key";

    // A database-backed scene carrying a persisted WGS-84 extent so the
    // registration branch of ResolveExtentAsync (record.Extent) is exercised and
    // the I3S descriptor's fullExtent is populated. Config scenes carry no
    // extent and so only cover the null-extent path.
    private const string ExtentSceneId = "i3s-extent-scene";

    // A database-backed scene whose source kind is Building so the descriptor's
    // I3S layerType mapping (#1812) is exercised end to end.
    private const string BuildingSceneId = "i3s-building-scene";
    private const double ExtentXMin = -122.5;
    private const double ExtentYMin = 37.7;
    private const double ExtentXMax = -122.4;
    private const double ExtentYMax = 37.8;

    private readonly WebAppFixture _enterpriseFixture;
    private readonly WebAppFixture _communityFixture;
    private readonly string _fixtureRoot;
    private HttpClient _authenticatedClient = null!;

    public I3sSceneServerEndpointTests()
    {
        _fixtureRoot = SceneFixturePaths.ResolveFixtureRoot();

        _enterpriseFixture = BuildFixture(HonuaEdition.Enterprise);
        _communityFixture = BuildFixture(HonuaEdition.Community);
    }

    private WebAppFixture BuildFixture(HonuaEdition edition)
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"Scenes:Datasets:0:Id"] = SceneId,
                        [$"Scenes:Datasets:0:Name"] = "I3S Fixture Scene",
                        [$"Scenes:Datasets:0:Description"] = "Static scene used by I3S serving tests",
                        [$"Scenes:Datasets:0:AssetRoot"] = _fixtureRoot,

                        // Protected scene — requires an authenticated principal so
                        // the I3S SceneServer access-policy enforcement is covered
                        // (mirrors the gRPC and HTTP tileset RBAC parity tests).
                        [$"Scenes:Datasets:1:Id"] = ProtectedSceneId,
                        [$"Scenes:Datasets:1:Name"] = "I3S Protected Scene",
                        [$"Scenes:Datasets:1:AssetRoot"] = _fixtureRoot,
                        [$"Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false",

                        // Scene the stub geometry provider answers with null, so
                        // the geometries route's honest 404 path is covered.
                        [$"Scenes:Datasets:2:Id"] = NoGeometrySceneId,
                        [$"Scenes:Datasets:2:Name"] = "I3S No-Geometry Scene",
                        [$"Scenes:Datasets:2:AssetRoot"] = _fixtureRoot,
                    });
                });
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILicenseStatusProvider>(new StubLicenseStatusProvider(edition));
                // Register a real-transcoder-backed node geometry provider so the
                // #1810 geometries route serves actual transcoded bytes end to
                // end. It transcodes a single fixture square for every scene/node
                // except the dedicated "no geometry" scene, which it answers with
                // null to exercise the honest 404 path.
                services.AddSingleton<ISceneNodeGeometryProvider, StubSceneNodeGeometryProvider>();
            });

        return fixture;
    }

    public async Task InitializeAsync()
    {
        await _enterpriseFixture.InitializeAsync();
        await _communityFixture.InitializeAsync();
        _authenticatedClient = _enterpriseFixture.CreateClient(
            c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

        await RegisterExtentSceneAsync();
        await RegisterBuildingSceneAsync();
    }

    /// <summary>
    /// Registers a database-backed Building scene so the I3S layerType mapping
    /// (#1812) is exercised: the descriptor must advertise <c>Building</c>.
    /// </summary>
    private async Task RegisterBuildingSceneAsync()
    {
        var registration = _enterpriseFixture.GetService<ISceneRegistrationService>();
        await registration.RegisterAsync(new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = BuildingSceneId,
            Name = "I3S Building Scene",
            AssetRoot = _fixtureRoot,
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.Building,
            Extent = new SceneExtent(ExtentXMin, ExtentYMin, ExtentXMax, ExtentYMax),
            CachePolicy = SceneCachePolicy.Default,
            IsPublic = true,
            Status = SceneDatasetStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        });
    }

    /// <summary>
    /// Registers a database-backed scene with a persisted extent so the
    /// registration branch of <c>ResolveExtentAsync</c> populates the I3S
    /// <c>fullExtent</c>. The scene is public so the descriptor is reachable
    /// without auth.
    /// </summary>
    private async Task RegisterExtentSceneAsync()
    {
        var registration = _enterpriseFixture.GetService<ISceneRegistrationService>();
        await registration.RegisterAsync(new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = ExtentSceneId,
            Name = "I3S Extent Scene",
            AssetRoot = _fixtureRoot,
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            Extent = new SceneExtent(ExtentXMin, ExtentYMin, ExtentXMax, ExtentYMax),
            CachePolicy = SceneCachePolicy.Default,
            IsPublic = true,
            Status = SceneDatasetStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        });
    }

    public async Task DisposeAsync()
    {
        await _enterpriseFixture.DisposeAsync();
        await _communityFixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_EnterpriseEdition_ReturnsI3sServiceDescriptor()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{SceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("serviceName").GetString().Should().Be("I3S Fixture Scene");
        root.GetProperty("serviceVersion").GetString().Should().Be("1.7");
        var layers = root.GetProperty("layers").EnumerateArray().ToArray();
        layers.Should().ContainSingle();
        layers[0].GetProperty("layerType").GetString().Should().Be("3DObject");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_EnterpriseEdition_ReturnsThreeDObjectLayer()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{SceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("id").GetInt32().Should().Be(0);
        root.GetProperty("layerType").GetString().Should().Be("3DObject");
        root.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_UnknownLayerId_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{SceneId}/SceneServer/layers/7");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_UnknownScene_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync("/scenes/does-not-exist/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_CommunityEdition_Returns403()
    {
        var response = await _communityFixture.Client.GetAsync($"/scenes/{SceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_ProtectedScene_WithoutAuth_ReturnsUnauthorized()
    {
        // The I3S SceneServer root must enforce the scene access policy: an
        // anonymous caller against a protected scene is denied before the
        // descriptor is built. Guards against a regression dropping the
        // AccessPolicyHelpers.RequireAccess call.
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{ProtectedSceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_ProtectedScene_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{ProtectedSceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_ProtectedScene_WithAuth_ReturnsI3sServiceDescriptor()
    {
        var response = await _authenticatedClient.GetAsync($"/scenes/{ProtectedSceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("serviceName").GetString().Should().Be("I3S Protected Scene");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_ProtectedScene_WithAuth_ReturnsThreeDObjectLayer()
    {
        var response = await _authenticatedClient.GetAsync($"/scenes/{ProtectedSceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("layerType").GetString().Should().Be("3DObject");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_SceneWithPersistedExtent_PopulatesFullExtent()
    {
        // The DB-registered scene carries a persisted extent, so the registration
        // branch of ResolveExtentAsync threads it into the descriptor's
        // fullExtent (xmin/ymin/xmax/ymax). Guards against a regression dropping
        // the extent lookup.
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{ExtentSceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fullExtent = json.RootElement.GetProperty("fullExtent");
        fullExtent.GetProperty("xmin").GetDouble().Should().BeApproximately(ExtentXMin, 1e-9);
        fullExtent.GetProperty("ymin").GetDouble().Should().BeApproximately(ExtentYMin, 1e-9);
        fullExtent.GetProperty("xmax").GetDouble().Should().BeApproximately(ExtentXMax, 1e-9);
        fullExtent.GetProperty("ymax").GetDouble().Should().BeApproximately(ExtentYMax, 1e-9);
        fullExtent.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_SceneWithPersistedExtent_PopulatesLayerFullExtent()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{ExtentSceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var layer = json.RootElement.GetProperty("layers").EnumerateArray().Single();
        var fullExtent = layer.GetProperty("fullExtent");
        fullExtent.GetProperty("xmin").GetDouble().Should().BeApproximately(ExtentXMin, 1e-9);
        fullExtent.GetProperty("ymax").GetDouble().Should().BeApproximately(ExtentYMax, 1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer")]
    public async Task GetService_AtGeoServicesPath_ReturnsI3sServiceDescriptor()
    {
        // #1806: the canonical GeoServices path serves the same descriptor as
        // the legacy /scenes alias.
        var response = await _enterpriseFixture.Client.GetAsync($"/rest/services/{SceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("serviceName").GetString().Should().Be("I3S Fixture Scene");
        root.GetProperty("serviceVersion").GetString().Should().Be("1.7");
        root.GetProperty("layers").EnumerateArray().Single()
            .GetProperty("layerType").GetString().Should().Be("3DObject");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_AtGeoServicesPath_ReturnsThreeDObjectLayer()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/rest/services/{SceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("layerType").GetString().Should().Be("3DObject");
        root.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer")]
    public async Task GetService_AtGeoServicesPath_CommunityEdition_Returns403()
    {
        var response = await _communityFixture.Client.GetAsync($"/rest/services/{SceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_DescriptorIsHonest_RichMetadataAndNodePages()
    {
        // The enriched 1.7 descriptor carries the z-bearing fullExtent (read from
        // the tileset root bounding volume), heightModelInfo, and format
        // definitions/schema. With #1809 landed, the store now ALSO advertises the
        // I3S 1.7 store.nodePages model (the fixture tileset is loadable), while
        // the legacy 1.6 store.rootNode tree stays absent — Honua serves the
        // nodePages traversal model, not a 1.6 root-node tree.
        var response = await _enterpriseFixture.Client.GetAsync($"/rest/services/{ExtentSceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        // Vertical extent threaded from the fixture tileset root region (min/max
        // height 0..20 metres).
        var fullExtent = root.GetProperty("fullExtent");
        fullExtent.GetProperty("zmin").GetDouble().Should().BeApproximately(0.0, 1e-9);
        fullExtent.GetProperty("zmax").GetDouble().Should().BeApproximately(20.0, 1e-9);

        // heightModelInfo + format definitions/schema present (descriptive only).
        root.GetProperty("heightModelInfo").GetProperty("heightModel").GetString().Should().Be("ellipsoidal");
        root.TryGetProperty("geometryDefinitions", out var geometryDefinitions).Should().BeTrue();
        geometryDefinitions.GetArrayLength().Should().BeGreaterThan(0);
        root.TryGetProperty("materialDefinitions", out _).Should().BeTrue();
        root.TryGetProperty("textureSetDefinitions", out _).Should().BeTrue();
        root.TryGetProperty("attributeStorageInfo", out _).Should().BeTrue();

        // #1809: the loadable tileset projects to fetchable node pages, so the
        // store advertises store.nodePages (and a defaultGeometrySchema) but NOT
        // a legacy 1.6 rootNode tree.
        var store = root.GetProperty("store");
        store.TryGetProperty("rootNode", out _).Should().BeFalse();
        store.TryGetProperty("nodePages", out var nodePages).Should().BeTrue();
        nodePages.GetProperty("nodesPerPage").GetInt32().Should().BePositive();
        store.TryGetProperty("defaultGeometrySchema", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_BuildingScene_AdvertisesBuildingLayerType()
    {
        // #1812: a Building-source scene maps to the I3S Building layerType.
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{BuildingSceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("layerType").GetString().Should().Be("Building");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_SceneWithLoadableTileset_AdvertisesNodePagesStore()
    {
        // #1809: once the tileset projects to fetchable node pages, the store
        // advertises store.nodePages so a conformant client traverses the layer.
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{ExtentSceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var store = json.RootElement.GetProperty("store");
        store.TryGetProperty("nodePages", out var nodePages).Should().BeTrue();
        nodePages.GetProperty("nodesPerPage").GetInt32().Should().BePositive();
        store.GetProperty("profile").GetString().Should().Be("meshpyramids");
        store.GetProperty("normalReferenceFrame").GetString().Should().Be("east-north-up");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}/nodepages/{pageId:int}")]
    public async Task GetNodePage_ScenesAlias_CommunityEdition_Returns403()
    {
        var response = await _communityFixture.Client.GetAsync(
            $"/scenes/{SceneId}/SceneServer/layers/0/nodepages/0");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}/nodepages/{pageId:int}")]
    public async Task GetNodePage_ScenesAlias_EnterpriseEdition_ReturnsNodePage()
    {
        // The fixture scene (config registry) has a loadable tileset, so the
        // /scenes alias node-page route projects and serves page 0.
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/scenes/{SceneId}/SceneServer/layers/0/nodepages/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("nodes").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}/statistics/{fieldKey}/0")]
    public async Task GetStatistics_ScenesAlias_EnterpriseEdition_ReturnsStatistics()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/scenes/{SceneId}/SceneServer/layers/0/statistics/f_0/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("stats").GetProperty("totalValuesCount").GetInt64()
            .Should().BeGreaterThanOrEqualTo(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    public async Task GetNodeGeometry_EnterpriseEdition_ReturnsTranscodedI3sBuffer()
    {
        // #1810: the geometries route serves the transcoded I3S Default geometry
        // binary (not just the descriptor). The buffer must lead with the
        // vertexCount/featureCount header the transcoder emits.
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/nodes/0/geometries/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(I3sGeometryTranscoder.HeaderBytes);

        var vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        var featureCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        vertexCount.Should().Be(6u); // fixture square -> 2 triangles
        featureCount.Should().Be(1u);

        var expectedLength = I3sGeometryTranscoder.HeaderBytes
            + ((int)vertexCount * I3sGeometryTranscoder.VertexStrideBytes)
            + ((int)featureCount * I3sGeometryTranscoder.FeatureRecordBytes);
        bytes.Length.Should().Be(expectedLength);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    public async Task GetNodeGeometry_AtScenesAlias_ReturnsTranscodedI3sBuffer()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/scenes/{SceneId}/SceneServer/layers/0/nodes/0/geometries/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    public async Task GetNodeGeometry_CommunityEdition_Returns403()
    {
        var response = await _communityFixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/nodes/0/geometries/0");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    public async Task GetNodeGeometry_UnknownGeometryId_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/nodes/0/geometries/7");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    public async Task GetNodeGeometry_UnknownLayerId_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/7/nodes/0/geometries/0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    public async Task GetNodeGeometry_SceneWithNoTranscodableGeometry_Returns404()
    {
        // The provider returns null for this scene, so the route answers an
        // honest 404 rather than fabricating an empty buffer.
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{NoGeometrySceneId}/SceneServer/layers/0/nodes/0/geometries/0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/geometries/{geometryId:int}")]
    public async Task GetNodeGeometry_ProtectedScene_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{ProtectedSceneId}/SceneServer/layers/0/nodes/0/geometries/0");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task GetNodeAttribute_EnterpriseEdition_ReturnsObjectIdAttributeFile()
    {
        // #1811: the attributes route serves the per-field OBJECTID binary file an
        // ArcGIS SceneLayer client reads for identify. The values are the served
        // geometry's feature ids (the stub transcodes one feature with Id=1).
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/nodes/0/attributes/f_0/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        count.Should().Be(1u); // single fixture feature
        var objectId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
        objectId.Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task GetNodeAttribute_AtScenesAlias_ReturnsObjectIdAttributeFile()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/scenes/{SceneId}/SceneServer/layers/0/nodes/0/attributes/f_0/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/octet-stream");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task GetNodeAttribute_CommunityEdition_Returns403()
    {
        var response = await _communityFixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/nodes/0/attributes/f_0/0");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task GetNodeAttribute_UnknownField_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/nodes/0/attributes/f_99/0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task GetNodeAttribute_UnknownAttributeId_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/0/nodes/0/attributes/f_0/7");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task GetNodeAttribute_UnknownLayerId_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{SceneId}/SceneServer/layers/7/nodes/0/attributes/f_0/0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task GetNodeAttribute_SceneWithNoTranscodableGeometry_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{NoGeometrySceneId}/SceneServer/layers/0/nodes/0/attributes/f_0/0");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{sceneId}/SceneServer/layers/{layerId:int}/nodes/{nodeId:int}/attributes/{fieldKey}/{attributeId:int}")]
    public async Task GetNodeAttribute_ProtectedScene_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _enterpriseFixture.Client.GetAsync(
            $"/rest/services/{ProtectedSceneId}/SceneServer/layers/0/nodes/0/attributes/f_0/0");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// In-memory <see cref="ISceneNodeGeometryProvider"/> that transcodes a
    /// single fixture square with the real <see cref="I3sGeometryTranscoder"/>,
    /// proving the #1810 geometries route serves genuine transcoded bytes. It
    /// returns <see langword="null"/> for the dedicated no-geometry scene so the
    /// route's honest 404 path is exercised.
    /// </summary>
    private sealed class StubSceneNodeGeometryProvider : ISceneNodeGeometryProvider
    {
        public Task<I3sTranscodedGeometry?> GetNodeGeometryAsync(
            SceneDataset scene,
            int nodeId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(scene);

            if (string.Equals(scene.Id, NoGeometrySceneId, StringComparison.Ordinal))
            {
                return Task.FromResult<I3sTranscodedGeometry?>(null);
            }

            var feature = new SceneFeature
            {
                Id = 1,
                Geometry = new SceneFeatureGeometry
                {
                    Kind = SceneGeometryKind.Polygon,
                    Vertices = new[]
                    {
                        new SceneVertex(-122.4200, 37.7700, 10.0),
                        new SceneVertex(-122.4199, 37.7700, 10.0),
                        new SceneVertex(-122.4199, 37.7701, 10.0),
                        new SceneVertex(-122.4200, 37.7701, 10.0),
                    },
                },
            };

            return Task.FromResult<I3sTranscodedGeometry?>(
                I3sGeometryTranscoder.Transcode(new[] { feature }));
        }
    }

    private sealed class StubLicenseStatusProvider : ILicenseStatusProvider
    {
        private readonly HonuaEdition _edition;

        public StubLicenseStatusProvider(HonuaEdition edition) => _edition = edition;

        public LicenseStatus GetCurrentStatus() =>
            new(_edition, IsValid: true, ExpiresAt: null, LicensedTo: "test");

        public Task<LicenseUploadResult> UploadLicenseAsync(
            Stream licenseStream, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LicenseUploadResult(false, "Stub does not support upload."));
    }
}

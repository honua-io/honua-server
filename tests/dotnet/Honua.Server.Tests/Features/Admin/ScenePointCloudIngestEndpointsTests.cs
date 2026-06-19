// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Tests.Features.Infrastructure.Scene;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the Enterprise-gated LAS/LAZ/COPC point-cloud scene
/// ingest endpoint (#1201). Covers the HTTP-surface contract — admin
/// authentication, the Enterprise entitlement gate, malformed/compressed-upload
/// rejection — and the end-to-end happy path that ingests a synthetic LAS
/// fixture and serves the resulting 3D Tiles point tileset.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public class ScenePointCloudIngestEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-pcloud-ingest-admin-key";
    private const string IngestUrl = "/api/v1/admin/scenes/ingest/pointcloud";

    private readonly WebAppFixture _fixture;
    private readonly string _outputRoot;
    private HttpClient _client = null!;

    public ScenePointCloudIngestEndpointsTests()
    {
        // Generated tilesets must land in a temp directory rather than the
        // server's default content-root-relative "scenes-generated" folder, so
        // an end-to-end ingest never leaks artifacts into the source tree.
        _outputRoot = Path.Combine(Path.GetTempPath(), $"honua-pcloud-it-{Guid.NewGuid():N}");
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .WithTestLicense(HonuaEdition.Enterprise)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("SceneGeneration:OutputRoot", _outputRoot);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    private static MultipartFormDataContent BuildUpload(byte[] document, params (string Key, string Value)[] fields)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(document);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "cloud.las");
        foreach (var (key, value) in fields)
        {
            content.Add(new StringContent(value, Encoding.UTF8), key);
        }
        return content;
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/pointcloud")]
    public async Task Ingest_Unauthenticated_Returns401()
    {
        using var unauth = _fixture.CreateClient();
        using var upload = BuildUpload(PointCloudSceneFixtures.ColoredGridGeographic());

        var response = await unauth.PostAsync(IngestUrl, upload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/pointcloud")]
    public async Task Ingest_EmptyFile_Returns400()
    {
        using var upload = BuildUpload(Array.Empty<byte>());

        var response = await _client.PostAsync(IngestUrl, upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/pointcloud")]
    public async Task Ingest_MalformedDocument_Returns400()
    {
        using var upload = BuildUpload(Encoding.UTF8.GetBytes("this is not a LAS point cloud"));

        var response = await _client.PostAsync(IngestUrl, upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/pointcloud")]
    public async Task Ingest_LazCompressed_Returns400()
    {
        using var upload = BuildUpload(
            PointCloudSceneFixtures.MarkCompressed(PointCloudSceneFixtures.ColoredGridGeographic()),
            ("sceneId", "pcloud-laz-rejected"));

        var response = await _client.PostAsync(IngestUrl, upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/pointcloud")]
    public async Task Ingest_ValidLas_Returns201AndServesTileset()
    {
        using var upload = BuildUpload(
            PointCloudSceneFixtures.ColoredGridGeographic(),
            ("sceneId", "pcloud-ingest-it"),
            ("displayName", "Point Cloud Ingest IT"),
            ("editionGate", "enterprise"));

        var response = await _client.PostAsync(IngestUrl, upload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PointCloudIngestResponse>();
        Assert.NotNull(body);
        Assert.Equal("pcloud-ingest-it", body!.SceneId);
        Assert.Equal(256, body.PointCount);
        Assert.True(body.TileCount >= 1);
        Assert.True(body.HasColor);
        Assert.EndsWith("/scenes/pcloud-ingest-it/tileset.json", body.TilesetUrl);

        // The registered tileset must be servable through the standard scene path.
        var tileset = await _client.GetAsync("/scenes/pcloud-ingest-it/tileset.json");
        Assert.Equal(HttpStatusCode.OK, tileset.StatusCode);
        var tilesetBody = await tileset.Content.ReadAsStringAsync();
        Assert.Contains("geometricError", tilesetBody);

        // A referenced .pnts tile must resolve through the asset-path serving route.
        var tile = await _client.GetAsync("/scenes/pcloud-ingest-it/points_0000.pnts");
        Assert.Equal(HttpStatusCode.OK, tile.StatusCode);
        var tileBytes = await tile.Content.ReadAsByteArrayAsync();
        // "pnts" magic in little-endian leading bytes.
        Assert.True(tileBytes.Length >= 4);
        Assert.Equal((byte)'p', tileBytes[0]);
        Assert.Equal((byte)'n', tileBytes[1]);
        Assert.Equal((byte)'t', tileBytes[2]);
        Assert.Equal((byte)'s', tileBytes[3]);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/pointcloud")]
    public async Task Ingest_LiteralRoute_AcceptsValidUpload()
    {
        // Issues the POST against the literal route string so the API-surface
        // drift scanner (EndpointRegistryDriftTests) can match this method body
        // to the "POST /api/v1/admin/scenes/ingest/pointcloud" registry entry; the
        // other tests in this class reach the route through the IngestUrl
        // constant, which the source scanner cannot resolve.
        using var upload = BuildUpload(
            PointCloudSceneFixtures.ColoredGridGeographic(),
            ("sceneId", "pcloud-ingest-literal"),
            ("editionGate", "enterprise"));

        var response = await _client.PostAsync("/api/v1/admin/scenes/ingest/pointcloud", upload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/pointcloud")]
    public async Task Ingest_NonEnterpriseEdition_Returns402()
    {
        // A fresh fixture without the Enterprise entitlement: the admin operator
        // is authenticated but the entitlement gate must deny the ingest.
        var proFixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });
        await proFixture.InitializeAsync();
        try
        {
            using var proClient = proFixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            using var upload = BuildUpload(PointCloudSceneFixtures.SinglePointGeographic(), ("sceneId", "pro-denied"));

            var response = await proClient.PostAsync(IngestUrl, upload);

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        }
        finally
        {
            await proFixture.DisposeAsync();
        }
    }
}

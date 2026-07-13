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
/// Integration tests for the Enterprise-gated CityGML/BIM scene ingest endpoint
/// (#1207). Covers the HTTP-surface contract — admin authentication, the
/// Enterprise entitlement gate, malformed-upload rejection — and the end-to-end
/// happy path that ingests a CityGML fixture and serves the resulting tileset.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public class SceneBimIngestEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-bim-ingest-admin-key";
    private const string IngestUrl = "/api/v1/admin/scenes/ingest/citygml";

    private readonly WebAppFixture _fixture;
    private readonly string _outputRoot;
    private HttpClient _client = null!;

    public SceneBimIngestEndpointsTests()
    {
        // Generated tilesets must land in a temp directory rather than the
        // server's default content-root-relative "scenes-generated" folder, so
        // an end-to-end ingest never leaks artifacts into the source tree.
        // The second segment is a generated "honua-bim-it-{guid}" literal, so it
        // can never be rooted and silently discard Path.GetTempPath().
        _outputRoot = Path.Combine(Path.GetTempPath(), $"honua-bim-it-{Guid.NewGuid():N}");
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
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        content.Add(fileContent, "file", "building.gml");
        foreach (var (key, value) in fields)
        {
            content.Add(new StringContent(value, Encoding.UTF8), key);
        }
        return content;
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/citygml")]
    public async Task Ingest_Unauthenticated_Returns401()
    {
        using var unauth = _fixture.CreateClient();
        using var upload = BuildUpload(CityGmlSceneFixtures.SingleBuildingGeographic());

        var response = await unauth.PostAsync(IngestUrl, upload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/citygml")]
    public async Task Ingest_EmptyFile_Returns400()
    {
        using var upload = BuildUpload(Array.Empty<byte>());

        var response = await _client.PostAsync(IngestUrl, upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/citygml")]
    public async Task Ingest_MalformedDocument_Returns400()
    {
        using var upload = BuildUpload(Encoding.UTF8.GetBytes("this is not citygml"));

        var response = await _client.PostAsync(IngestUrl, upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/citygml")]
    public async Task Ingest_ValidCityGml_Returns201AndServesTileset()
    {
        using var upload = BuildUpload(
            CityGmlSceneFixtures.SingleBuildingGeographic(),
            ("sceneId", "bim-ingest-it"),
            ("displayName", "BIM Ingest IT"),
            ("editionGate", "enterprise"));

        var response = await _client.PostAsync(IngestUrl, upload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CityGmlIngestResponse>();
        Assert.NotNull(body);
        Assert.Equal("bim-ingest-it", body!.SceneId);
        Assert.Equal(1, body.BuildingCount);
        Assert.Equal(4, body.SurfaceCount);
        Assert.Contains("Architectural", body.Disciplines);
        Assert.Contains("Structural", body.Disciplines);
        Assert.EndsWith("/scenes/bim-ingest-it/tileset.json", body.TilesetUrl);

        // The registered tileset must be servable through the standard scene path.
        var tileset = await _client.GetAsync("/scenes/bim-ingest-it/tileset.json");
        Assert.Equal(HttpStatusCode.OK, tileset.StatusCode);
        var tilesetBody = await tileset.Content.ReadAsStringAsync();
        Assert.Contains("geometricError", tilesetBody);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/citygml")]
    public async Task Ingest_LiteralRoute_AcceptsValidUpload()
    {
        // Issues the POST against the literal route string so the API-surface
        // drift scanner (EndpointRegistryDriftTests) can match this method body
        // to the "POST /api/v1/admin/scenes/ingest/citygml" registry entry; the
        // other tests in this class reach the route through the IngestUrl
        // constant, which the source scanner cannot resolve.
        using var upload = BuildUpload(
            CityGmlSceneFixtures.SingleBuildingGeographic(),
            ("sceneId", "bim-ingest-literal"),
            ("editionGate", "enterprise"));

        var response = await _client.PostAsync("/api/v1/admin/scenes/ingest/citygml", upload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/citygml")]
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
            using var upload = BuildUpload(CityGmlSceneFixtures.SingleBuildingGeographic(), ("sceneId", "pro-denied"));

            var response = await proClient.PostAsync(IngestUrl, upload);

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        }
        finally
        {
            await proFixture.DisposeAsync();
        }
    }
}

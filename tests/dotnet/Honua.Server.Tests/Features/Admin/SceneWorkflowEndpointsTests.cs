// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Scene.Generation;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public class SceneWorkflowEndpointsTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-workflow-admin-key";
    private const string SampleObj = "v -5 0 -5\nv 5 0 -5\nv 5 0 5\nv -5 10 0\nf 1 2 4\nf 2 3 4\nf 3 1 4\n";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _outputRoot = Path.Join(Path.GetTempPath(), $"scene-workflow-it-{Guid.NewGuid():N}");
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public SceneWorkflowEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
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

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/archive")]
    [Endpoint("GET /api/v1/admin/scenes/assets/{sceneId}")]
    public async Task IngestArchive_ValidZip_Returns201AndProjectsAssetLifecycleView()
    {
        var sceneId = $"archive-it-{Guid.NewGuid():N}"[..24];
        using var upload = BuildFileUpload(BuildValidTilesetZip(), "tileset.zip", "application/zip", ("sceneId", sceneId));

        using var response = await _client.PostAsync("/api/v1/admin/scenes/ingest/archive", upload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SceneArchiveIngestResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(sceneId, body!.SceneId);
        Assert.EndsWith($"/scenes/{sceneId}/tileset.json", body.TilesetUrl);
        Assert.EndsWith($"/rest/services/{sceneId}/SceneServer", body.I3sUrl);

        using var tileset = await _client.GetAsync($"/scenes/{sceneId}/tileset.json");
        Assert.Equal(HttpStatusCode.OK, tileset.StatusCode);

        using var assetResponse = await _client.GetAsync($"/api/v1/admin/scenes/assets/{sceneId}");
        Assert.Equal(HttpStatusCode.OK, assetResponse.StatusCode);
        var asset = await assetResponse.Content.ReadFromJsonAsync<SceneAssetLifecycleResponse>(JsonOptions);
        Assert.NotNull(asset);
        Assert.Equal(sceneId, asset!.SceneId);
        Assert.Equal(1, asset.PublishedRevision);
        Assert.True(asset.ByteSize > 0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/ingest/model")]
    [Endpoint("GET /api/v1/admin/scenes/jobs")]
    [Endpoint("GET /api/v1/admin/scenes/jobs/{jobId}")]
    [Endpoint("GET /api/v1/admin/scenes/assets")]
    public async Task IngestModel_AsyncSubmission_Returns202AndCompletesWithLifecycleView()
    {
        var sceneId = $"model-it-{Guid.NewGuid():N}"[..24];
        using var upload = BuildFileUpload(
            Encoding.UTF8.GetBytes(SampleObj),
            "tower.obj",
            "text/plain",
            ("sceneId", sceneId),
            ("longitude", "-157.8581"),
            ("latitude", "21.3069"),
            ("height", "10"),
            ("async", "true"));

        using var accepted = await _client.PostAsync("/api/v1/admin/scenes/ingest/model", upload);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.NotNull(accepted.Headers.Location);

        var jobAccepted = await accepted.Content.ReadFromJsonAsync<SceneJobAcceptedResponse>(JsonOptions);
        Assert.NotNull(jobAccepted);
        Assert.Equal("model", jobAccepted!.SourceKind);
        Assert.Contains(jobAccepted.JobId, accepted.Headers.Location!.ToString());

        SceneJobStatusResponse? finalStatus = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var poll = await _client.GetAsync($"/api/v1/admin/scenes/jobs/{jobAccepted.JobId}");
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
            finalStatus = await poll.Content.ReadFromJsonAsync<SceneJobStatusResponse>(JsonOptions);
            if (finalStatus?.Status is "succeeded" or "failed")
            {
                break;
            }
            await Task.Delay(50);
        }

        Assert.NotNull(finalStatus);
        Assert.Equal("succeeded", finalStatus!.Status);
        Assert.Equal(sceneId, finalStatus.SceneId);
        Assert.EndsWith($"/scenes/{sceneId}/tileset.json", finalStatus.TilesetUrl);

        using var jobsList = await _client.GetAsync("/api/v1/admin/scenes/jobs");
        Assert.Equal(HttpStatusCode.OK, jobsList.StatusCode);
        var jobs = await jobsList.Content.ReadFromJsonAsync<SceneJobStatusResponse[]>(JsonOptions);
        Assert.Contains(jobs!, j => j.JobId == jobAccepted.JobId);

        using var assetsList = await _client.GetAsync("/api/v1/admin/scenes/assets");
        Assert.Equal(HttpStatusCode.OK, assetsList.StatusCode);
        var assets = await assetsList.Content.ReadFromJsonAsync<SceneAssetLifecycleResponse[]>(JsonOptions);
        Assert.Contains(assets!, a => a.SceneId == sceneId && a.SourceKind == "model" && a.JobStatus == "succeeded");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/generate")]
    [Endpoint("GET /api/v1/admin/scenes/jobs/{jobId}")]
    public async Task GenerateScene_AsyncFlag_Returns202WithStatusLocation()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/scenes/generate",
            new GenerateSceneRequest { LayerId = int.MaxValue, Async = true },
            JsonOptions);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var accepted = await response.Content.ReadFromJsonAsync<SceneJobAcceptedResponse>(JsonOptions);
        Assert.NotNull(accepted);
        Assert.Equal("feature_layer", accepted!.SourceKind);
    }

    private static MultipartFormDataContent BuildFileUpload(
        byte[] contentBytes,
        string fileName,
        string mediaType,
        params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(contentBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", fileName);
        foreach (var (key, value) in fields)
        {
            form.Add(new StringContent(value, Encoding.UTF8), key);
        }

        return form;
    }

    private static byte[] BuildValidTilesetZip()
    {
        var glb = ObjToGlbConverter.Convert(Encoding.UTF8.GetBytes(SampleObj)).GlbBytes;
        const double degToRad = Math.PI / 180.0;
        var tileset = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $$"""
            {
              "asset": { "version": "1.1" },
              "geometricError": 100.0,
              "root": {
                "boundingVolume": { "region": [{{-157.86 * degToRad}}, {{21.30 * degToRad}}, {{-157.85 * degToRad}}, {{21.31 * degToRad}}, 0.0, 20.0] },
                "geometricError": 100.0,
                "refine": "ADD",
                "children": [
                  {
                    "boundingVolume": { "region": [{{-157.86 * degToRad}}, {{21.30 * degToRad}}, {{-157.85 * degToRad}}, {{21.31 * degToRad}}, 0.0, 20.0] },
                    "geometricError": 0.0,
                    "content": { "uri": "0.glb" }
                  }
                ]
              }
            }
            """);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e1 = zip.CreateEntry("tileset.json");
            using (var s1 = e1.Open()) s1.Write(Encoding.UTF8.GetBytes(tileset));
            var e2 = zip.CreateEntry("0.glb");
            using (var s2 = e2.Open()) s2.Write(glb);
        }

        return ms.ToArray();
    }
}

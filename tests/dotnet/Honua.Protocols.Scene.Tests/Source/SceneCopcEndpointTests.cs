// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Scene;

[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class SceneCopcEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-copc-test-key";
    private const string SigningKey = "test-copc-signing-key-aBcD1234ZxYw5678";
    private const string PublicCopcSceneId = "public-copc";
    private const string ProtectedCopcSceneId = "protected-copc";

    private readonly WebAppFixture _fixture;
    private readonly string _copcDir;
    private readonly byte[] _copcBytes;
    private HttpClient _authenticatedClient = null!;

    public SceneCopcEndpointTests()
    {
        var sourceFixture = RepositoryPaths.Resolve(
            "tests", "dotnet", "Honua.Worker.Gdal.Tests", "Fixtures", "PointCloudProof", "genuine-7.copc.laz");
        _copcBytes = File.ReadAllBytes(sourceFixture);
        _copcDir = Path.Join(Path.GetTempPath(), $"honua-copc-ep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_copcDir);
        File.WriteAllBytes(Path.Join(_copcDir, "sample.copc.laz"), _copcBytes);

        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scenes:Datasets:0:Id"] = PublicCopcSceneId,
                        ["Scenes:Datasets:0:Name"] = "Public COPC",
                        ["Scenes:Datasets:0:AssetRoot"] = _copcDir,
                        ["Scenes:Datasets:0:TilesetFileName"] = "sample.copc.laz",
                        ["Scenes:Datasets:0:ServingFormat"] = "copc",
                        ["Scenes:Datasets:0:ContentKind"] = "point-cloud",

                        ["Scenes:Datasets:1:Id"] = ProtectedCopcSceneId,
                        ["Scenes:Datasets:1:Name"] = "Protected COPC",
                        ["Scenes:Datasets:1:AssetRoot"] = _copcDir,
                        ["Scenes:Datasets:1:TilesetFileName"] = "sample.copc.laz",
                        ["Scenes:Datasets:1:ServingFormat"] = "copc",
                        ["Scenes:Datasets:1:ContentKind"] = "point-cloud",
                        ["Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false",

                        ["Scenes:ObjectOrigin:MaxRangeReadBytes"] = "512",
                        ["Scenes:ObjectOrigin:MaxCopcFullResponseBytes"] = "512",
                        ["Honua:SceneAccessSigning:SigningKey"] = SigningKey,
                        ["Honua:SceneAccessSigning:TokenTtlMinutes"] = "15"
                    });
                });
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _authenticatedClient = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public async Task DisposeAsync()
    {
        _authenticatedClient?.Dispose();
        await _fixture.DisposeAsync();
        if (Directory.Exists(_copcDir))
        {
            Directory.Delete(_copcDir, recursive: true);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("HEAD /scenes/{sceneId}/copc")]
    public async Task HeadCopc_PublicScene_ReturnsAcceptRangesAndExactContentLength()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, $"/scenes/{PublicCopcSceneId}/copc");
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.AcceptRanges.Should().Contain("bytes");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.laszip+copc");
        response.Content.Headers.ContentLength.Should().Be(_copcBytes.Length);
        response.Headers.ETag.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/copc")]
    public async Task GetCopc_ValidByteRange_Returns206PartialContentWithExactBytes()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/scenes/{PublicCopcSceneId}/copc");
        request.Headers.Range = new RangeHeaderValue(0, 374);

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Headers.AcceptRanges.Should().Contain("bytes");
        response.Content.Headers.ContentRange?.From.Should().Be(0);
        response.Content.Headers.ContentRange?.To.Should().Be(374);
        response.Content.Headers.ContentRange?.Length.Should().Be(_copcBytes.Length);

        var slice = await response.Content.ReadAsByteArrayAsync();
        slice.Should().Equal(_copcBytes.AsSpan(0, 375).ToArray());
        Encoding.ASCII.GetString(slice, 0, 4).Should().Be("LASF");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/copc")]
    public async Task GetCopc_StaleIfRange_UsesFullResponseLimit()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/scenes/{PublicCopcSceneId}/copc");
        request.Headers.Range = new RangeHeaderValue(0, 63);
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"stale\""));

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task GetSceneAsset_Copc_RequiresDedicatedBoundedRoute()
    {
        using var tileset = await _fixture.Client.GetAsync($"/scenes/{PublicCopcSceneId}/tileset.json");
        using var asset = await _fixture.Client.GetAsync($"/scenes/{PublicCopcSceneId}/sample.copc.laz");

        tileset.StatusCode.Should().Be(HttpStatusCode.NotFound);
        asset.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/copc")]
    public async Task GetCopc_InvalidRange_Returns416RangeNotSatisfiable()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/scenes/{PublicCopcSceneId}/copc");
        request.Headers.Range = new RangeHeaderValue(_copcBytes.Length + 100, _copcBytes.Length + 200);

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        response.Content.Headers.ContentRange?.Length.Should().Be(_copcBytes.Length);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/copc")]
    public async Task GetCopc_RangeExceedingByteCeiling_Returns413PayloadTooLarge()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/scenes/{PublicCopcSceneId}/copc");
        request.Headers.Range = new RangeHeaderValue(0, 600);

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /scenes/{sceneId}/copc")]
    public async Task GetCopc_ProtectedScene_RequiresAuthOrAccessEnvelope()
    {
        using var unauthReq = new HttpRequestMessage(HttpMethod.Get, $"/scenes/{ProtectedCopcSceneId}/copc");
        unauthReq.Headers.Range = new RangeHeaderValue(0, 63);
        var unauth = await _fixture.Client.SendAsync(unauthReq);
        unauth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var issueReq = new HttpRequestMessage(HttpMethod.Post, $"/scenes/{ProtectedCopcSceneId}/access-envelope");
        var issueResp = await _authenticatedClient.SendAsync(issueReq);
        issueResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await issueResp.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("token").GetString()!;

        using var authReq = new HttpRequestMessage(HttpMethod.Get, $"/scenes/{ProtectedCopcSceneId}/copc");
        authReq.Headers.Add("X-Honua-Token", token);
        authReq.Headers.Range = new RangeHeaderValue(0, 63);
        var authorized = await _fixture.Client.SendAsync(authReq);

        authorized.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await authorized.Content.ReadAsByteArrayAsync()).Should().Equal(_copcBytes.AsSpan(0, 64).ToArray());
    }
}

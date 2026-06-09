// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// HTTP integration coverage for the OpenUSD scene manifest export endpoint
/// (<c>GET /scenes/{sceneId}/exports/openusd/stage.usda</c>): deterministic
/// <c>.usda</c> output and ETag, Pro/Enterprise gating, protected-scene auth
/// and private caching, conditional requests, validation failures, and that
/// OpenUSD is not advertised as a runtime scene capability.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class SceneOpenUsdManifestEndpointTests
{
    private const string AdminPassword = "openusd-manifest-test-key";

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/exports/openusd/stage.usda")]
    public async Task GetOpenUsdManifest_PublicConstructionScene_ReturnsDeterministicUsdaManifest()
    {
        var fixtureRoot = NvidiaConstructionFixturePaths.ResolveFixtureRoot();
        var fixture = await CreateFixtureAsync(
            fixtureRoot,
            NvidiaConstructionFixturePaths.MainSceneId,
            NvidiaConstructionFixturePaths.MainTilesetFileName).ConfigureAwait(false);

        try
        {
            var first = await fixture.Client.GetAsync(
                $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/exports/openusd/stage.usda").ConfigureAwait(false);
            var second = await fixture.Client.GetAsync(
                $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/exports/openusd/stage.usda").ConfigureAwait(false);

            first.StatusCode.Should().Be(HttpStatusCode.OK);
            second.StatusCode.Should().Be(HttpStatusCode.OK);
            first.Content.Headers.ContentType?.MediaType.Should().Be("model/vnd.usda");
            first.Headers.ETag.Should().NotBeNull();
            second.Headers.ETag!.Tag.Should().Be(first.Headers.ETag!.Tag);
            first.Headers.CacheControl?.Public.Should().BeTrue();

            var content = await first.Content.ReadAsStringAsync().ConfigureAwait(false);
            content.Should().StartWith("#usda 1.0");
            content.Should().Contain("defaultPrim = \"HonuaScene\"");
            content.Should().Contain("metersPerUnit = 1");
            content.Should().Contain("upAxis = \"Z\"");
            content.Should().Contain("honua:exportSchemaVersion = \"1.0\"");
            content.Should().Contain("honua:sourceFormat = \"OGC 3D Tiles\"");
            content.Should().Contain("honua:nativeUsdGeometry = \"false\"");
            content.Should().Contain("honua:projectId = \"nvidia-construction-demo-2026\"");
            content.Should().Contain("honua:observationCount = 5");
            content.Should().Contain("Observation_obs_001");
            content.Should().Contain("/scenes/nvidia-construction/tileset.json");
            content.Should().Contain("/scenes/nvidia-construction/observations.json");
            content.Should().NotContain("evidenceUris");
            content.Should().NotContain(fixtureRoot);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/exports/openusd/stage.usda")]
    public async Task GetOpenUsdManifest_ProtectedSceneRequiresAuthAndReturnsPrivateCache()
    {
        var fixtureRoot = NvidiaConstructionFixturePaths.ResolveFixtureRoot();
        var fixture = await CreateFixtureAsync(
            fixtureRoot,
            NvidiaConstructionFixturePaths.MainSceneId,
            NvidiaConstructionFixturePaths.MainTilesetFileName,
            protectedScene: true).ConfigureAwait(false);

        try
        {
            var anonymous = await fixture.Client.GetAsync(
                $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/exports/openusd/stage.usda").ConfigureAwait(false);
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using var authenticated = fixture.CreateClient(client =>
                client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            var response = await authenticated.GetAsync(
                $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/exports/openusd/stage.usda").ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl?.Private.Should().BeTrue();
            response.Headers.Vary.Should().Contain("Authorization");
            response.Headers.Vary.Should().Contain("X-API-Key");

            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            content.Should().Contain("honua:access = \"protected\"");
            content.Should().Contain("/scenes/nvidia-construction/access-envelope");
            content.Should().NotContain(AdminPassword);
            content.Should().NotContain("Bearer ");
            content.Should().NotContain("X-API-Key");
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/exports/openusd/stage.usda")]
    public async Task GetOpenUsdManifest_CommunityEdition_ReturnsForbidden()
    {
        var fixtureRoot = NvidiaConstructionFixturePaths.ResolveFixtureRoot();
        var fixture = await CreateFixtureAsync(
            fixtureRoot,
            NvidiaConstructionFixturePaths.MainSceneId,
            NvidiaConstructionFixturePaths.MainTilesetFileName,
            edition: HonuaEdition.Community).ConfigureAwait(false);

        try
        {
            var response = await fixture.Client.GetAsync(
                $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/exports/openusd/stage.usda").ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/exports/openusd/stage.usda")]
    public async Task GetOpenUsdManifest_MissingScene_ReturnsNotFound()
    {
        var fixtureRoot = NvidiaConstructionFixturePaths.ResolveFixtureRoot();
        var fixture = await CreateFixtureAsync(
            fixtureRoot,
            NvidiaConstructionFixturePaths.MainSceneId,
            NvidiaConstructionFixturePaths.MainTilesetFileName).ConfigureAwait(false);

        try
        {
            var response = await fixture.Client.GetAsync(
                "/scenes/missing-scene/exports/openusd/stage.usda").ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/exports/openusd/stage.usda")]
    public async Task GetOpenUsdManifest_WithMatchingIfNoneMatch_ReturnsNotModified()
    {
        var fixtureRoot = NvidiaConstructionFixturePaths.ResolveFixtureRoot();
        var fixture = await CreateFixtureAsync(
            fixtureRoot,
            NvidiaConstructionFixturePaths.MainSceneId,
            NvidiaConstructionFixturePaths.MainTilesetFileName).ConfigureAwait(false);

        try
        {
            var first = await fixture.Client.GetAsync(
                $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/exports/openusd/stage.usda").ConfigureAwait(false);
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            first.Headers.ETag.Should().NotBeNull();

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/scenes/{NvidiaConstructionFixturePaths.MainSceneId}/exports/openusd/stage.usda");
            request.Headers.IfNoneMatch.Add(first.Headers.ETag!);

            var response = await fixture.Client.SendAsync(request).ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.NotModified);
            response.Headers.ETag!.Tag.Should().Be(first.Headers.ETag!.Tag);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/exports/openusd/stage.usda")]
    public async Task GetOpenUsdManifest_MissingDeclaredObservationSidecar_ReturnsBadRequest()
    {
        var tempRoot = CreateTempSceneRoot("""
            {
              "asset": { "version": "1.1" },
              "extras": {
                "bounds": { "west": -1, "south": 1, "east": 2, "north": 3 },
                "observationsLayer": { "sidecarUri": "observations.json" }
              },
              "root": {
                "boundingVolume": { "region": [ -0.017, 0.017, 0.034, 0.052, 0, 10 ] }
              }
            }
            """);
        var fixture = await CreateFixtureAsync(tempRoot, "missing-sidecar").ConfigureAwait(false);

        try
        {
            var response = await fixture.Client.GetAsync(
                "/scenes/missing-sidecar/exports/openusd/stage.usda").ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /scenes/{sceneId}/exports/openusd/stage.usda")]
    public async Task GetOpenUsdManifest_InvalidCoordinateMetadata_ReturnsBadRequest()
    {
        var tempRoot = CreateTempSceneRoot("""
            {
              "asset": { "version": "1.1" },
              "extras": {
                "bounds": { "west": -1, "south": 1, "east": 2, "north": 95 }
              },
              "root": {
                "boundingVolume": { "region": [ -0.017, 0.017, 0.034, 0.052, 0, 10 ] }
              }
            }
            """);
        var fixture = await CreateFixtureAsync(tempRoot, "invalid-coordinates").ConfigureAwait(false);

        try
        {
            var response = await fixture.Client.GetAsync(
                "/scenes/invalid-coordinates/exports/openusd/stage.usda").ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /api/scenes/{sceneId}")]
    public async Task GetSceneMetadata_DoesNotAdvertiseOpenUsdAsRuntimeCapability()
    {
        var fixtureRoot = NvidiaConstructionFixturePaths.ResolveFixtureRoot();
        var fixture = await CreateFixtureAsync(
            fixtureRoot,
            NvidiaConstructionFixturePaths.MainSceneId,
            NvidiaConstructionFixturePaths.MainTilesetFileName).ConfigureAwait(false);

        try
        {
            var response = await fixture.Client.GetAsync(
                $"/api/scenes/{NvidiaConstructionFixturePaths.MainSceneId}").ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var capabilities = json.RootElement.GetProperty("capabilities")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            capabilities.Should().Equal("3d-tiles");
            json.RootElement.GetRawText().Should().NotContain("openusd");
            json.RootElement.GetRawText().Should().NotContain("usda");
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(
        string assetRoot,
        string sceneId,
        string tilesetFileName = "tileset.json",
        bool protectedScene = false,
        HonuaEdition edition = HonuaEdition.Pro)
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseStatusProvider>(new StubLicenseStatusProvider(edition))
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        [$"Scenes:Datasets:0:Id"] = sceneId,
                        [$"Scenes:Datasets:0:Name"] = "NVIDIA Demo Santa Clara Construction Site",
                        [$"Scenes:Datasets:0:AssetRoot"] = assetRoot,
                        [$"Scenes:Datasets:0:TilesetFileName"] = tilesetFileName
                    };

                    if (protectedScene)
                    {
                        values[$"Scenes:Datasets:0:AccessPolicy:AllowAnonymous"] = "false";
                    }

                    configBuilder.AddInMemoryCollection(values);
                });
            });

        await fixture.InitializeAsync().ConfigureAwait(false);
        return fixture;
    }

    private static string CreateTempSceneRoot(string tilesetJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "honua-openusd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "tileset.json"), tilesetJson);
        return root;
    }

    private sealed class StubLicenseStatusProvider(HonuaEdition edition) : ILicenseStatusProvider
    {
        public LicenseStatus GetCurrentStatus() => new(
            edition,
            IsValid: true,
            ExpiresAt: null,
            LicensedTo: null);

        public Task<LicenseUploadResult> UploadLicenseAsync(
            Stream licenseStream,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LicenseUploadResult(true, "Test license."));
    }
}

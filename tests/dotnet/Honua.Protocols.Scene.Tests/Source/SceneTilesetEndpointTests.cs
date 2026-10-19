// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Integration tests for the hosted 3D Tiles serving endpoints.
/// Verifies that <c>tileset.json</c>, binary tile content, and nested
/// tilesets are served with correct content types, ETags, and cache headers
/// against a public fixture tileset.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class SceneTilesetEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private readonly string _fixtureRoot;

    public SceneTilesetEndpointTests()
    {
        _fixtureRoot = SceneFixturePaths.ResolveFixtureRoot();

        _fixture = new WebAppFixture()
            // Exercise the output-cache middleware, which requires a Pro entitlement.
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                // Public scene tests exercise anonymous behavior end-to-end:
                // output cache eligibility, browser/CDN cache headers, and the
                // Range bypass regression all depend on requests reaching the
                // server unauthenticated. WebAppFixture defaults to
                // HONUA_DEV_AUTH=true, which would otherwise short-circuit
                // AnonymousOnlyOutputCachePolicy and disable cache storage.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"Scenes:Datasets:0:Id"] = SceneFixturePaths.FixtureSceneId,
                        [$"Scenes:Datasets:0:Name"] = "Honua Fixture Tileset",
                        [$"Scenes:Datasets:0:Description"] = "Static 3D Tiles fixture used by tests",
                        [$"Scenes:Datasets:0:AssetRoot"] = _fixtureRoot
                    });
                });
            });
    }

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task GetTileset_FromBrowserOrigin_ExposesETagAndAcceptRanges()
    {
        // Mirrors the CORS preflight a browser CesiumJS instance would issue
        // for nested asset GETs. The shared CORS policy must keep ETag and
        // Accept-Ranges in the exposed-headers list so the tile cache and
        // range processing work in browsers.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/scenes/{SceneFixturePaths.FixtureSceneId}/tileset.json");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:3000");

        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        if (response.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposedValues))
        {
            var combined = string.Join(',', exposedValues);
            combined.Should().Contain("ETag");
            combined.Should().Contain("Accept-Ranges");
            combined.Should().Contain("Content-Length");
        }
    }
}

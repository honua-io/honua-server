// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

/// <summary>
/// Catalog registration tests for the Esri I3S SceneServer (#1807). A
/// registered scene is discoverable as a <c>type:"SceneServer"</c> directory
/// entry at the canonical <c>/rest/services/{id}/SceneServer</c> path for
/// Enterprise editions, and is omitted entirely from the open-core catalog.
/// </summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GeoservicesCatalog)]
public sealed class GeoservicesCatalogSceneServerTests
{
    private const string SceneId = "catalog-scene";
    private const string SceneName = "Catalog Scene";

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_Enterprise_ListsRegisteredSceneServer()
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise);
        await fixture.InitializeAsync();
        try
        {
            await RegisterSceneAsync(fixture);

            var response = await fixture.Client.GetAsync("/rest/services?f=json");
            response.Be200Ok();

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var sceneServers = payload.RootElement
                .GetProperty("services")
                .EnumerateArray()
                .Where(service =>
                    service.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "SceneServer", StringComparison.Ordinal))
                .ToArray();

            sceneServers.Should().ContainSingle();
            sceneServers[0].GetProperty("name").GetString().Should().Be(SceneName);
            sceneServers[0].GetProperty("url").GetString().Should()
                .MatchRegex(@".*/rest/services/catalog-scene/SceneServer$");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_OpenCore_OmitsSceneServer()
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);
        await fixture.InitializeAsync();
        try
        {
            await RegisterSceneAsync(fixture);

            var response = await fixture.Client.GetAsync("/rest/services?f=json");
            response.Be200Ok();

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var hasSceneServer = payload.RootElement
                .GetProperty("services")
                .EnumerateArray()
                .Any(service =>
                    service.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "SceneServer", StringComparison.Ordinal));

            hasSceneServer.Should().BeFalse(
                "SceneServer is Enterprise-gated and must be omitted from the open-core catalog.");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task RegisterSceneAsync(WebAppFixture fixture)
    {
        var registration = fixture.GetService<ISceneRegistrationService>();
        await registration.RegisterAsync(new SceneDatasetRecord
        {
            DatasetId = Guid.NewGuid(),
            Id = SceneId,
            Name = SceneName,
            AssetRoot = AppContext.BaseDirectory,
            TilesetFileName = "tileset.json",
            DatasetType = SceneDatasetType.HostedTiles,
            CachePolicy = SceneCachePolicy.Default,
            IsPublic = true,
            Status = SceneDatasetStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        });
    }
}

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
/// <remarks>
/// honua-server#1568 (signature 2): the scene registry persists to the literal,
/// process-global <c>honua.scene_datasets</c> table — it is schema-qualified, so the
/// per-test <c>search_path</c> isolation that scopes the rest of the catalog does NOT
/// scope it. These tests therefore run in a serialized collection
/// (<c>Database.GeoServicesSceneCatalog</c>) and truncate the global table around each
/// case so a fixed-id scene cannot collide on <c>scene_datasets_id_unique</c> with, or
/// leak its directory entry into, a peer test. The <c>ContainSingle()</c> assertion is
/// preserved — the truncation makes the global registry deterministic rather than
/// weakening the check.
/// </remarks>
[Collection("Database.GeoServicesSceneCatalog")]
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
            await ResetSceneRegistryAsync(fixture);
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
            await ResetSceneRegistryAsync(fixture);
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
            await ResetSceneRegistryAsync(fixture);
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
            await ResetSceneRegistryAsync(fixture);
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

    // The scene registry is the one catalog read/write path that targets the literal,
    // schema-qualified honua.scene_datasets table instead of the schema-partitioned
    // in-memory Metadata v2 graph, so it is shared process-globally across every parallel
    // test schema. Clear it around each case so the global registry state is deterministic
    // for this serialized collection (honua-server#1568).
    private static Task ResetSceneRegistryAsync(WebAppFixture fixture)
    {
        // honua.scene_datasets is schema-qualified to the literal global honua schema, so
        // the search_path the fixture sets is irrelevant here — any connection on the shared
        // data source clears the one table every scene-catalog test contends on.
        return fixture.Postgres.ExecuteAsync("TRUNCATE TABLE honua.scene_datasets;");
    }
}

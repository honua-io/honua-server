// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

/// <summary>
/// Hermetic unit tests for the wired CityGML/BIM ingest executor (#1207). They
/// exercise the full parse → tile → register → promote pipeline against a temp
/// output root and an in-memory registration stub, proving the produced tileset
/// is written to a servable asset root with Building Scene Layer semantics
/// without standing up Postgres or an HTTP host.
/// </summary>
public sealed class CityGmlScenePublishExecutorTests : IDisposable
{
    private readonly string _outputRoot;
    private readonly StubRegistrationService _registration;
    private readonly CityGmlScenePublishExecutor _executor;

    public CityGmlScenePublishExecutorTests()
    {
        _outputRoot = Path.Combine(Path.GetTempPath(), $"honua-citygml-{Guid.NewGuid():N}");
        _registration = new StubRegistrationService();

        var options = Options.Create(new SceneGenerationServerOptions
        {
            OutputRoot = _outputRoot,
            GeneratorTag = "honua-test-generator/1.0"
        });

        _executor = new CityGmlScenePublishExecutor(
            new TestHostEnvironment(),
            options,
            NullLogger<CityGmlScenePublishExecutor>.Instance,
            _registration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    [UnitTest]
    public async Task Ingest_SingleBuilding_WritesServableTilesetAndRegistersDataset()
    {
        var outcome = await _executor.IngestAsync(
            new CityGmlSceneIngestRequest(
                CityGmlSceneFixtures.SingleBuildingGeographic(),
                SceneId: "bim-tower",
                DisplayName: "BIM Tower",
                EditionGate: "enterprise"),
            CancellationToken.None);

        outcome.SceneId.Should().Be("bim-tower");
        outcome.BuildingCount.Should().Be(1);
        outcome.SurfaceCount.Should().Be(4, "ground + roof + two walls each emit one BSL component.");
        outcome.TileCount.Should().Be(1);
        outcome.Disciplines.Should().Contain("Architectural").And.Contain("Structural");

        // The tileset.json and the GLB tile must exist under the promoted asset
        // root so the standard scene serving path can stream them.
        File.Exists(Path.Combine(outcome.AssetRoot, "tileset.json")).Should().BeTrue();
        File.Exists(Path.Combine(outcome.AssetRoot, "building_0000.glb")).Should().BeTrue();

        // No staging directory should survive a successful promotion.
        Directory.GetDirectories(_outputRoot, ".staging-*").Should().BeEmpty();

        _registration.Records.Should().ContainSingle();
        var record = _registration.Records[0];
        record.Id.Should().Be("bim-tower");
        record.Name.Should().Be("BIM Tower");
        record.AssetRoot.Should().Be(outcome.AssetRoot);
        record.TilesetFileName.Should().Be("tileset.json");
        record.DatasetType.Should().Be(SceneDatasetType.HostedTiles);
        record.Crs.Should().Be("EPSG:4979");
        record.EditionGate.Should().Be("enterprise");
        record.IsPublic.Should().BeTrue();
        record.RequiresAuth.Should().BeFalse();
        record.Status.Should().Be(SceneDatasetStatus.Active);
    }

    [UnitTest]
    public async Task Ingest_SameDocumentTwice_ProducesByteIdenticalTiles()
    {
        var first = await _executor.IngestAsync(
            new CityGmlSceneIngestRequest(CityGmlSceneFixtures.SingleBuildingGeographic(), SceneId: "det-a"),
            CancellationToken.None);
        var second = await _executor.IngestAsync(
            new CityGmlSceneIngestRequest(CityGmlSceneFixtures.SingleBuildingGeographic(), SceneId: "det-b"),
            CancellationToken.None);

        var glb1 = await File.ReadAllBytesAsync(Path.Combine(first.AssetRoot, "building_0000.glb"));
        var glb2 = await File.ReadAllBytesAsync(Path.Combine(second.AssetRoot, "building_0000.glb"));
        glb1.Should().Equal(glb2, "identical CityGML input must produce byte-identical GLB output.");

        var tileset1 = await File.ReadAllBytesAsync(Path.Combine(first.AssetRoot, "tileset.json"));
        var tileset2 = await File.ReadAllBytesAsync(Path.Combine(second.AssetRoot, "tileset.json"));
        tileset1.Should().Equal(tileset2, "identical CityGML input must produce byte-identical tileset.json output.");
    }

    [UnitTest]
    public async Task Ingest_RequiresAuth_RegistersProtectedScene()
    {
        var outcome = await _executor.IngestAsync(
            new CityGmlSceneIngestRequest(
                CityGmlSceneFixtures.SingleBuildingGeographic(),
                SceneId: "protected-bim",
                RequiresAuth: true),
            CancellationToken.None);

        outcome.SceneId.Should().Be("protected-bim");
        var record = _registration.Records.Single();
        record.RequiresAuth.Should().BeTrue();
        record.IsPublic.Should().BeFalse();
    }

    [UnitTest]
    public async Task Ingest_DuplicateSceneId_ThrowsRegistrationConflict()
    {
        await _executor.IngestAsync(
            new CityGmlSceneIngestRequest(CityGmlSceneFixtures.SingleBuildingGeographic(), SceneId: "dup"),
            CancellationToken.None);

        var act = async () => await _executor.IngestAsync(
            new CityGmlSceneIngestRequest(CityGmlSceneFixtures.SingleBuildingGeographic(), SceneId: "dup"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.SceneRegistrationConflict);
    }

    [UnitTest]
    public async Task Ingest_ProjectedCrs_ThrowsLayerCrsUnknown()
    {
        var act = async () => await _executor.IngestAsync(
            new CityGmlSceneIngestRequest(CityGmlSceneFixtures.SingleBuildingProjected(), SceneId: "projected"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.LayerCrsUnknown);

        // A rejected ingest must not leave any registration or output behind.
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task Ingest_InvalidCacheMaxAge_ThrowsOptionsInvalid()
    {
        var act = async () => await _executor.IngestAsync(
            new CityGmlSceneIngestRequest(
                CityGmlSceneFixtures.SingleBuildingGeographic(),
                SceneId: "bad-cache",
                CacheMaxAgeSeconds: -1),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
    }
}

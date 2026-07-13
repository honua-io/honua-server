// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Tiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Tiles;

/// <summary>
/// Replay-safety and bounded-grid tests for durable tile-export contract version 2.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class TileExportReplayPlanTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public void Build_SparseZoomLevels_RoundTripsWithoutExpandingRange()
    {
        var plan = CreateMapPlan() with { ZoomLevels = [0, 3, 7] };

        var spec = TileExportExecutionSpecBuilder.Build(plan);

        spec.ContractVersion.Should().Be(2);
        spec.Parameters[TileExportJobParameterKeys.ZoomLevels].Should().Be("0,3,7");
        TileExportExecutionSpecBuilder.TryParse(spec.Parameters, out var parsed, out var error).Should().BeTrue(error);
        parsed!.ZoomLevels.Should().Equal(0, 3, 7);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void Identity_SourceRevisionStyleVersionAndRasterSelection_ChangeIdentity()
    {
        var baseline = CreateMapPlan();
        var map = (TileExportMapSourceDescriptor)baseline.Source;
        var metadataChanged = baseline with { Source = map with { MetadataRevision = 43 } };
        var styleChanged = baseline with
        {
            Source = map with { Layers = [new("0", "default", 2)] }
        };
        var rasterA = CreateRasterPlan("membership-a");
        var rasterB = CreateRasterPlan("membership-b");

        var identities = new[] { baseline, metadataChanged, styleChanged }
            .Select(TileExportArtifactIdentity.Compute);
        identities.Should().OnlyHaveUniqueItems();
        TileExportArtifactIdentity.Compute(rasterA).Should().NotBe(TileExportArtifactIdentity.Compute(rasterB));
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void Build_MapAndRasterDescriptors_RoundTripAsBoundedParameters()
    {
        foreach (var plan in new[] { CreateMapPlan(), CreateRasterPlan("sha256:0123456789abcdef") })
        {
            var spec = TileExportExecutionSpecBuilder.Build(plan);

            spec.Parameters.Values.Should().OnlyContain(static value => value.Length < 1024);
            TileExportExecutionSpecBuilder.TryParse(spec.Parameters, out var parsed, out var error).Should().BeTrue(error);
            parsed.Should().BeEquivalentTo(plan);
        }
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void Identity_NoProviderWatermark_IsStableForRetryButDistinctAcrossSubmissions()
    {
        var first = CreateMapPlan(reuseScope: "submission-a", dataWatermark: null);
        var retry = first with { MaxArtifactBytes = first.MaxArtifactBytes / 2 };
        var unrelated = CreateMapPlan(reuseScope: "submission-b", dataWatermark: null);

        TileExportArtifactIdentity.Compute(retry).Should().Be(TileExportArtifactIdentity.Compute(first),
            "artifact admission limits are operational and do not change package bytes");
        TileExportArtifactIdentity.Compute(unrelated).Should().NotBe(TileExportArtifactIdentity.Compute(first));
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void Build_UnorderedDuplicateOrOutOfRangeZoomLevels_AreRejected()
    {
        var invalid = new[]
        {
            CreateMapPlan() with { ZoomLevels = [0, 3, 2] },
            CreateMapPlan() with { ZoomLevels = [1, 1] },
            CreateMapPlan() with { ZoomLevels = [31] }
        };

        foreach (var plan in invalid)
        {
            FluentActions.Invoking(() => TileExportExecutionSpecBuilder.Build(plan))
                .Should().Throw<ArgumentException>().WithMessage("*zoom levels*");
        }
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void TryParse_VersionOneRecord_IsRejectedForSafeResubmission()
    {
        var parameters = TileExportExecutionSpecBuilder.Build(CreateMapPlan()).Parameters
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        parameters[TileExportJobParameterKeys.ContractVersion] = "1";

        TileExportExecutionSpecBuilder.TryParse(parameters, out var plan, out var error).Should().BeFalse();

        plan.Should().BeNull();
        error.Should().Contain("v1").And.Contain("resubmission");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void Build_VersionTwoSpec_IsFencedFromVersionOneBackend()
    {
        var spec = TileExportExecutionSpecBuilder.Build(CreateMapPlan());
        var oldWorkerCapabilities = new BatchComputeBackendCapabilities { MaxSupportedContractVersion = 1 };

        spec.ContractVersion.Should().BeGreaterThan(oldWorkerCapabilities.MaxSupportedContractVersion);
        RuntimeProfiles.CanClaim(RuntimeProfiles.DefaultAccepted, spec.RuntimeProfile).Should().BeFalse(
            "v1 workers accept only the managed/default profile and must not claim a v2 record");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void GridPlanner_WholeWorldAtZoomThirty_CountsCheckedWithoutMaterializing()
    {
        var plan = CreateMapPlan() with
        {
            ZoomLevels = [30],
            West = -180,
            South = -90,
            East = 180,
            North = 90,
            MaxTiles = 1
        };

        var grid = TileExportGridPlanner.Create(plan);

        grid.TotalTileCount.Should().Be(1L << 60);
        grid.SelectedTileCount.Should().Be(1);
        grid.ExceededTransferLimit.Should().BeTrue();
        grid.Tiles.Should().ContainSingle().Which.Should().Be(new TileExportCoordinate(30, 0, 0));
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void GridPlanner_MaxTiles_TruncatesInDeterministicBundleOrder()
    {
        var plan = CreateMapPlan() with
        {
            ZoomLevels = [8],
            West = -180,
            South = 85,
            East = 180,
            North = 90,
            MaxTiles = 130
        };

        var grid = TileExportGridPlanner.Create(plan);
        var tiles = grid.Tiles.ToArray();

        tiles.Should().HaveCount(130);
        tiles[0].Should().Be(new TileExportCoordinate(8, 0, 0));
        tiles[127].Should().Be(new TileExportCoordinate(8, 0, 127));
        tiles[128].Should().Be(new TileExportCoordinate(8, 0, 128));
        tiles[129].Should().Be(new TileExportCoordinate(8, 0, 129));
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void GridPlanner_EastOnTileBoundary_ExcludesEastHandColumn()
    {
        var plan = CreateMapPlan() with
        {
            ZoomLevels = [1],
            West = -180,
            South = -90,
            East = 0,
            North = 90
        };

        var grid = TileExportGridPlanner.Create(plan);

        grid.TotalTileCount.Should().Be(2);
        grid.Tiles.Should().Equal(
            new TileExportCoordinate(1, 0, 0),
            new TileExportCoordinate(1, 1, 0));
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void GridPlanner_SouthOnTileBoundary_ExcludesSouthHandRow()
    {
        var plan = CreateMapPlan() with
        {
            ZoomLevels = [1],
            West = -180,
            South = 0,
            East = 180,
            North = 90
        };

        var grid = TileExportGridPlanner.Create(plan);

        grid.TotalTileCount.Should().Be(2);
        grid.Tiles.Should().Equal(
            new TileExportCoordinate(1, 0, 0),
            new TileExportCoordinate(1, 0, 1));
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void GridPlanner_WholeWorld_PreservesEveryTile()
    {
        var plan = CreateMapPlan() with
        {
            ZoomLevels = [1],
            West = -180,
            South = -90,
            East = 180,
            North = 90
        };

        var grid = TileExportGridPlanner.Create(plan);

        grid.TotalTileCount.Should().Be(4);
        grid.Tiles.Should().HaveCount(4).And.OnlyHaveUniqueItems();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExecuteAsync_UnavailablePinnedSource_FailsBeforeArtifactLookupOrGeneration()
    {
        var plan = CreateMapPlan();
        var storage = Substitute.For<ICloudFileStorage>();
        var producer = Substitute.For<ITileExportPackageProducer>();
        var fence = Substitute.For<ITileExportSourceFence>();
        fence.SourceKind.Returns(TileExportSourceKind.Map);
        fence.IsAvailableAsync(plan, Arg.Any<CancellationToken>()).Returns(false);
        var executor = new TileExportJobExecutor(
            storage,
            [producer],
            [fence],
            TimeProvider.System,
            NullLogger<TileExportJobExecutor>.Instance);

        var result = await executor.ExecuteAsync(JobFor(plan), Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Be("Pinned tile-export source is unavailable or has changed.");
        await storage.DidNotReceiveWithAnyArgs().GetMetadataAsync(default!, default);
        await producer.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, default);
    }

    private static TileExportJobPlan CreateMapPlan(
        string? reuseScope = null,
        string? dataWatermark = "provider-revision-9")
        => new()
        {
            SourceKind = TileExportSourceKind.Map,
            ResourceId = "world-basemap",
            Source = new TileExportMapSourceDescriptor(
                42,
                [new("0", "default", 1)],
                dataWatermark,
                reuseScope),
            ZoomLevels = [0, 2],
            West = -180,
            South = -85,
            East = 180,
            North = 85,
            TileImageFormat = "PNG",
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = 10_000,
            MaxArtifactBytes = 1024 * 1024,
            RetentionSeconds = 3600
        };

    private static TileExportJobPlan CreateRasterPlan(string fingerprint)
        => CreateMapPlan() with
        {
            SourceKind = TileExportSourceKind.Raster,
            Source = new TileExportRasterSourceDescriptor(
                42,
                "7",
                "method=by-attribute;sort=acquisition-date:desc",
                "2026-07-01T00:00:00Z/2026-07-02T00:00:00Z",
                fingerprint),
            TileImageFormat = "JPEG",
            PackageFormat = TileExportPackageFormat.Zip
        };

    private static ExecutionJobRecord JobFor(TileExportJobPlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        return new()
        {
            OperationId = "source-fence-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = TileExportExecutionSpecBuilder.Build(plan)
        };
    }
}

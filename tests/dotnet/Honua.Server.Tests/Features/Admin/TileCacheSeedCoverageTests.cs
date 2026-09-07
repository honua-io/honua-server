// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Formats;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Proves that a <c>seed</c> job driven by a <c>bbox</c> renders exactly the tiles inside the box
/// (honua-server#4421).
/// </summary>
/// <remarks>
/// <para>
/// <c>TileOperationExecutionCore.BuildTileCoordinates</c> — including the inclusive boundary loops
/// and the truncation predicate — was exercised for <c>delete</c> only. No seed, warm, archive or
/// publish job in the repository ever ran with a bbox, so the coordinate set a generation job
/// actually produces was unasserted: a job that seeded the whole world, or the wrong quadrant,
/// looked identical from the outside.
/// </para>
/// <para>
/// Every expected tile below is derived from the XYZ definition rather than from a previous run:
/// <c>x = floor((lon + 180) / 360 * 2^z)</c> and
/// <c>y = floor((1 - ln(tan(lat) + sec(lat)) / pi) / 2 * 2^z)</c>. The captured tiles come from the
/// tile provider the job dispatches to, so they are what the job asked to be rendered.
/// </para>
/// </remarks>
public sealed class TileCacheSeedCoverageTests
{
    [UnitTest]
    public async Task Seed_WithBboxInsideOneTile_RendersThatTileOnly()
    {
        // z=2 splits the world into 4x4. Longitudes -179..-100 are all in column 0
        // ((-179+180)/360*4 = 0.011 and (-100+180)/360*4 = 0.889). Latitudes 70..84 are all in
        // row 0 (0.895 and 0.170). So exactly one tile: 2/0/0.
        var rendered = await SeedAsync(minZoom: 2, maxZoom: 2, bbox: [-179d, 70d, -100d, 84d]);

        rendered.Should().BeEquivalentTo([(2, 0, 0)]);
    }

    [UnitTest]
    public async Task Seed_WithBboxSpanningTwoRows_RendersBothRows()
    {
        // Widening the southern edge to 40 degrees crosses into row 1
        // ((1 - ln(tan(40) + sec(40))/pi) / 2 * 4 = 1.51), so the same column now covers two tiles.
        var rendered = await SeedAsync(minZoom: 2, maxZoom: 2, bbox: [-179d, 40d, -100d, 84d]);

        rendered.Should().BeEquivalentTo([(2, 0, 0), (2, 0, 1)]);
    }

    [UnitTest]
    public async Task Seed_WithBboxOnTheOtherSideOfTheWorld_RendersOnlyThatQuadrant()
    {
        // Longitudes 100..170 fall in column 3 ((100+180)/360*4 = 3.11, (170+180)/360*4 = 3.89);
        // latitudes -60..-40 fall in row 2 (2.49) and row 2 again (-60 -> 2.86).
        var rendered = await SeedAsync(minZoom: 2, maxZoom: 2, bbox: [100d, -60d, 170d, -40d]);

        rendered.Should().BeEquivalentTo([(2, 3, 2)]);
    }

    [UnitTest]
    public async Task Seed_AcrossTwoZoomLevels_RendersTheBoxAtEachZoom()
    {
        // The same box at z=1 is one tile (column 0, row 0) and at z=2 is the tile above.
        var rendered = await SeedAsync(minZoom: 1, maxZoom: 2, bbox: [-179d, 70d, -100d, 84d]);

        rendered.Should().BeEquivalentTo([(1, 0, 0), (2, 0, 0)]);
    }

    [UnitTest]
    public async Task Seed_WithMaxTilesBelowTheBoxSize_TruncatesDeterministically()
    {
        // The full world at z=1 is four tiles; MaxTiles caps the produced set. The order is
        // column-major (x outer, y inner), so the first two are 1/0/0 and 1/0/1.
        var rendered = await SeedAsync(minZoom: 1, maxZoom: 1, bbox: null, maxTiles: 2);

        rendered.Should().HaveCount(2);
        rendered.Should().BeEquivalentTo([(1, 0, 0), (1, 0, 1)]);
    }

    [UnitTest]
    public async Task Seed_WithoutBbox_CoversTheWholeZoomLevel()
    {
        // The negative control for the bbox cases: with no box the job must cover the level, so a
        // bbox that was parsed and never applied would be indistinguishable from this.
        var rendered = await SeedAsync(minZoom: 1, maxZoom: 1, bbox: null);

        rendered.Should().BeEquivalentTo([(1, 0, 0), (1, 0, 1), (1, 1, 0), (1, 1, 1)]);
    }

    /// <summary>
    /// Runs a seed job and returns the (z, x, y) triples the job asked the tile provider to render.
    /// </summary>
    private static async Task<(int Z, int X, int Y)[]> SeedAsync(
        int minZoom,
        int maxZoom,
        double[]? bbox,
        int maxTiles = 500)
    {
        var rendered = new ConcurrentBag<(int Z, int X, int Y)>();
        var stub = new TileOperationJobServicePublishTests.StubCloudStorage();
        using var serviceProvider = TileOperationJobServicePublishTests.BuildScopeForSeed(
            stub,
            tileProvider => tileProvider.GetMvtTileAsync(
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<Honua.Core.Features.FeatureStore.Domain.FeatureQuery?>(),
                    Arg.Any<TileOptions>(),
                    Arg.Any<TileLimits>(),
                    Arg.Any<GridGeometry?>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    // GetMvtTileAsync(layerId, x, y, z, ...)
                    rendered.Add((call.ArgAt<int>(3), call.ArgAt<int>(1), call.ArgAt<int>(2)));
                    return Task.FromResult<byte[]?>(MvtTileBuilder.Canonical());
                }));

        var sut = TileOperationJobServicePublishTests.CreateSutForSeed(serviceProvider);
        var jobId = await sut.StartAsync(new TileOperationStartRequest
        {
            Operation = "seed",
            LayerId = 7,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            MaxTiles = maxTiles,
            Bbox = bbox
        });

        await sut.ProcessQueuedJobAsync(jobId);

        var progress = await sut.GetAsync(jobId);
        progress!.Status.Should().Be(OperationStatus.Completed, progress.ErrorMessage);
        return [.. rendered];
    }
}

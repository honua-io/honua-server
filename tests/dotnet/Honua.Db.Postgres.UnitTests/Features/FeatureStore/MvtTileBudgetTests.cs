// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.ObjectPool;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class MvtTileBudgetTests
{
    [Fact]
    public void Next_WalksTheLadderThenStops()
    {
        default(MvtTileBudgetStep).IsFull.Should().BeTrue();

        var step = MvtTileBudgetStep.Full;
        var seen = new List<(int Index, int? Cap, bool Omit)>();
        while (true)
        {
            seen.Add((step.Index, step.FeatureCap, step.OmitAttributes));
            if (step.Next() is not { } next)
            {
                break;
            }

            step = next;
        }

        seen.Should().Equal(
            (0, (int?)null, false),
            (1, (int?)null, false),
            (2, (int?)4_000, true),
            (3, (int?)800, true),
            (4, (int?)200, true),
            (5, (int?)40, true),
            (6, (int?)8, true),
            (7, (int?)1, true));
    }

    [Fact]
    public async Task ReadAsync_OverBudgetThenFit_StopsOnTheFittingPass()
    {
        var seen = new List<int>();
        var tile = await MvtTileBudgetFitter.ReadAsync(
            10,
            step =>
            {
                seen.Add(step.Index);
                return step.Index;
            },
            (index, max, _) => index < 2
                ? throw new TileSizeLimitExceededException(100, max)
                : Task.FromResult<byte[]?>([1, 2, 3]),
            CancellationToken.None);

        seen.Should().Equal(0, 1, 2);
        tile.Should().Equal(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task ReadAsync_FirstPassFits_DoesNotRetry()
    {
        var calls = 0;
        var tile = await MvtTileBudgetFitter.ReadAsync(
            10,
            _ =>
            {
                calls++;
                return 0;
            },
            (_, _, _) => Task.FromResult<byte[]?>([4]),
            CancellationToken.None);

        calls.Should().Be(1);
        tile.Should().Equal(new byte[] { 4 });
    }

    [Fact]
    public async Task ReadAsync_EmptyFullPass_ReturnsNullWithoutRetry()
    {
        var calls = 0;
        var tile = await MvtTileBudgetFitter.ReadAsync(
            10,
            _ =>
            {
                calls++;
                return 0;
            },
            (_, _, _) => Task.FromResult<byte[]?>(null),
            CancellationToken.None);

        calls.Should().Be(1);
        tile.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_EmptyThinningPass_Continues()
    {
        var seen = new List<int>();
        var tile = await MvtTileBudgetFitter.ReadAsync(
            10,
            step =>
            {
                seen.Add(step.Index);
                return step.Index;
            },
            (index, max, _) => index switch
            {
                0 => throw new TileSizeLimitExceededException(100, max),
                1 => Task.FromResult<byte[]?>([]),
                _ => Task.FromResult<byte[]?>([9])
            },
            CancellationToken.None);

        seen.Should().Equal(0, 1, 2);
        tile.Should().Equal(new byte[] { 9 });
    }

    [Fact]
    public async Task ReadAsync_ExhaustedLadder_RethrowsTheLastRefusal()
    {
        var terminal = new TileSizeLimitExceededException(9, 1);
        var thrown = await Assert.ThrowsAsync<TileSizeLimitExceededException>(() =>
            MvtTileBudgetFitter.ReadAsync(
                1,
                step => step.Index,
                (index, max, _) =>
                {
                    if (index == 7)
                    {
                        throw terminal;
                    }

                    throw new TileSizeLimitExceededException(index, max);
                },
                CancellationToken.None));

        thrown.Should().BeSameAs(terminal);
    }

    [Fact]
    public void BuildMvtTileQuery_FullPass_KeepsTheHistoricalSql()
    {
        var builder = CreateBuilder();
        var full = builder.BuildMvtTileQuery(
            1, 0, 0, 5,
            new FeatureQuery { SpatialReferenceSrid = 3857 },
            new TileOptions { SimplifyZoom = 0, TileBuffer = 0, TileExtent = 4096 },
            new TileLimits { MaxFeaturesPerTile = 0 });

        full.Sql.Should().Contain("ST_TileEnvelope");
        full.Sql.Should().NotContain("ST_SnapToGrid");
        full.Sql.Should().NotContain("ORDER BY");
        full.Sql.Should().Contain(DatabaseSchema.AttributesColumn);
    }

    [Fact]
    public void BuildMvtTileQuery_ThinningPass_SnapsAndThenDropsAttributes()
    {
        var builder = CreateBuilder();
        var options = new TileOptions { SimplifyZoom = 0, TileBuffer = 0, TileExtent = 4096 };
        var query = new FeatureQuery { SpatialReferenceSrid = 3857 };
        var limits = new TileLimits { MaxFeaturesPerTile = 0 };
        var step1 = MvtTileBudgetStep.Full.Next()!.Value;

        var snapped = builder.BuildMvtTileQuery(
            1, 0, 0, 5, query, options, limits, CoreGeometryStorageType.Geometry, null, step1);

        snapped.Sql.Should().Contain("ST_SnapToGrid");
        snapped.Sql.Should().Contain("ST_SimplifyPreserveTopology");
        snapped.Sql.Should().Contain("GREATEST");
        snapped.Sql.Should().NotContain("ORDER BY");
        snapped.Sql.Should().Contain(DatabaseSchema.AttributesColumn);

        var dropped = builder.BuildMvtTileQuery(
            1, 0, 0, 5, query, options, limits, CoreGeometryStorageType.Geometry, null, step1.Next()!.Value);

        dropped.Sql.Should().Contain("ST_SnapToGrid");
        dropped.Sql.Should().Contain("ORDER BY ST_Area");
        dropped.Sql.Should().Contain("LIMIT");
        dropped.Sql.Should().NotContain(DatabaseSchema.AttributesColumn);
    }

    [Fact]
    public void BuildH3TileQuery_ThinningPass_CapsCellsWithoutSnapping()
    {
        var builder = CreateBuilder();
        var options = new TileOptions { TileBuffer = 0, TileExtent = 4096 };
        var limits = new TileLimits { MaxFeaturesPerTile = 0 };
        var full = builder.BuildH3TileQuery(1, 0, 0, 0, 2, null, options, limits);

        full.Sql.Should().NotContain("ORDER BY COUNT(*)");
        full.Sql.Should().NotContain("ST_SnapToGrid");

        var thin = builder.BuildH3TileQuery(
            1, 0, 0, 0, 2, null, options, limits,
            CoreGeometryStorageType.Geometry,
            MvtTileBudgetStep.Full.Next()!.Value.Next()!.Value);

        thin.Sql.Should().Contain("ORDER BY COUNT(*) DESC, _h3cell::text ASC");
        thin.Sql.Should().NotContain("ST_SnapToGrid");
    }

    private static FeatureQueryBuilder CreateBuilder()
    {
        var pool = new DefaultObjectPoolProvider().Create<StringBuilder>(new FeatureStoreStringBuilderPooledObjectPolicy());
        return new FeatureQueryBuilder(pool, new GeometryProcessor());
    }
}

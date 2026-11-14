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
        var seen = new List<(int Index, double Scale, bool Omit)>();
        while (true)
        {
            seen.Add((step.Index, step.SimplifyScale, step.OmitAttributes));
            if (step.Next() is not { } next)
            {
                break;
            }

            step = next;
        }

        seen.Should().Equal(
            (0, 1d, false),
            (1, 0d, true),
            (2, 4d, true),
            (3, 8d, true),
            (4, 16d, true),
            (5, 32d, true),
            (6, 64d, true));
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
                step => step,
                (step, max, _) =>
                {
                    if (step.Next() is null)
                    {
                        throw terminal;
                    }

                    throw new TileSizeLimitExceededException(step.Index, max);
                },
                CancellationToken.None));

        thrown.Should().BeSameAs(terminal);
    }

    [Fact]
    public async Task ReadAsync_CollapsedThinningPasses_RefusesInsteadOfReturningEmpty()
    {
        var refusal = new TileSizeLimitExceededException(100, 10);
        var seen = new List<int>();
        var thrown = await Assert.ThrowsAsync<TileSizeLimitExceededException>(() =>
            MvtTileBudgetFitter.ReadAsync(
                10,
                step =>
                {
                    seen.Add(step.Index);
                    return step;
                },
                (step, _, _) => step.IsFull
                    ? throw refusal
                    : Task.FromResult<byte[]?>(null),
                CancellationToken.None));

        thrown.Should().BeSameAs(refusal);
        seen.Should().Equal(0, 1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public async Task ReadAsync_AttributeOnlyPassFits_PreservesOriginalGeometry()
    {
        var seen = new List<int>();
        var tile = await MvtTileBudgetFitter.ReadAsync(
            10,
            step =>
            {
                seen.Add(step.Index);
                return step;
            },
            (step, max, _) =>
            {
                if (step.IsFull)
                {
                    throw new TileSizeLimitExceededException(100, max);
                }

                return Task.FromResult<byte[]?>(step.SimplifyScale <= 0 ? [7] : null);
            },
            CancellationToken.None);

        seen.Should().Equal(0, 1);
        tile.Should().Equal(new byte[] { 7 });
    }

    [Fact]
    public void Tolerance_CustomGrid_UsesTileResolutionNotWebMercatorZoom()
    {
        var step = MvtTileBudgetStep.Full.Next()!.Value.Next()!.Value;
        const double span = 1_000d;
        const int extent = 4096;
        var tileUnit = span / extent;

        var custom = MvtTileBudget.Tolerance(0, span, extent, geographic: false, step, useWebMercatorZoomFloor: false);
        custom.Should().BeApproximately(tileUnit * step.SimplifyScale, 1e-9);

        var webMercator = MvtTileBudget.Tolerance(0, span, extent, geographic: false, step);
        webMercator.Should().BeApproximately(TileMath.GetSimplificationTolerance(0) * step.SimplifyScale, 1e-9);
    }

    [Fact]
    public void BuildMvtTileQuery_CustomGridRetry_SimplifiesAtGridResolution()
    {
        var builder = CreateBuilder();
        var grid = new GridGeometry
        {
            Id = "local-metre",
            Srid = 27700,
            IsGeographic = false,
            TopLeftX = 0,
            TopLeftY = 1_000,
            TileWidth = 256,
            TileHeight = 256,
            Levels = [new GridLevel(0, ScaleDenominator: 1, CellSize: 1_000d / 256d, MatrixWidth: 1, MatrixHeight: 1)]
        };
        var step = MvtTileBudgetStep.Full.Next()!.Value.Next()!.Value;
        var result = builder.BuildMvtTileQuery(
            1, 0, 0, 0,
            new FeatureQuery { SpatialReferenceSrid = 27700 },
            new TileOptions { SimplifyZoom = 0, TileBuffer = 0, TileExtent = 4096 },
            new TileLimits { MaxFeaturesPerTile = 0 },
            CoreGeometryStorageType.Geometry,
            grid,
            step);

        var expected = (1_000d / 4096d) * step.SimplifyScale;
        var numbers = result.WhereParameters.OfType<double>().ToList();
        result.Sql.Should().NotContain("ST_SnapToGrid");
        result.Sql.Should().Contain("ST_SimplifyPreserveTopology");
        numbers.Any(number => Math.Abs(number - expected) < 1e-6).Should().BeTrue();
        numbers.Any(number => Math.Abs(number - 4_000d) < 1e-3).Should().BeFalse();
    }

    [Fact]
    public void BuildMvtTileQuery_LastPass_PreservesAllFeaturesAndGeometryFallback()
    {
        var builder = CreateBuilder();
        var last = MvtTileBudgetStep.Full;
        while (last.Next() is { } next)
        {
            last = next;
        }

        var result = builder.BuildMvtTileQuery(
            1, 0, 0, 0,
            new FeatureQuery { SpatialReferenceSrid = 3857 },
            new TileOptions { SimplifyZoom = 0, TileBuffer = 0, TileExtent = 4096 },
            new TileLimits { MaxFeaturesPerTile = 0 },
            CoreGeometryStorageType.Geometry,
            gridGeometry: null,
            last);

        result.Sql.Should().NotContain("ST_SnapToGrid");
        result.Sql.Should().NotContain(DatabaseSchema.AttributesColumn);
        result.Sql.Should().NotContain("LIMIT");
        result.Sql.Should().Contain("COALESCE(ST_AsMVTGeom");
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
    public void BuildMvtTileQuery_Retries_OmitAttributesWithoutAdaptiveLimits()
    {
        var builder = CreateBuilder();
        var options = new TileOptions { SimplifyZoom = 0, TileBuffer = 0, TileExtent = 4096 };
        var query = new FeatureQuery { SpatialReferenceSrid = 3857 };
        var limits = new TileLimits { MaxFeaturesPerTile = 0 };
        var step = MvtTileBudgetStep.Full.Next()!.Value;
        while (true)
        {
            var retry = builder.BuildMvtTileQuery(
                1, 0, 0, 5, query, options, limits, CoreGeometryStorageType.Geometry, null, step);
            retry.Sql.Should().NotContain("ST_SnapToGrid");
            retry.Sql.Should().NotContain("GREATEST");
            retry.Sql.Should().NotContain("ORDER BY");
            retry.Sql.Should().NotContain("LIMIT");
            retry.Sql.Should().NotContain(DatabaseSchema.AttributesColumn);
            retry.Sql.Should().Contain(DatabaseSchema.ObjectIdColumn);
            if (step.Index == 1)
            {
                retry.Sql.Should().NotContain("ST_SimplifyPreserveTopology");
            }
            else
            {
                retry.Sql.Should().Contain("ST_SimplifyPreserveTopology");
                retry.Sql.Should().Contain("COALESCE(ST_AsMVTGeom");
            }

            if (step.Next() is not { } next)
            {
                break;
            }

            step = next;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void BuildH3TileQuery_ConfiguredPolicy_DoesNotAddAdaptiveCellCap(int configuredLimit)
    {
        var builder = CreateBuilder();
        var options = new TileOptions { TileBuffer = 0, TileExtent = 4096 };
        var limits = new TileLimits { MaxFeaturesPerTile = configuredLimit };
        var tile = builder.BuildH3TileQuery(1, 0, 0, 0, 2, null, options, limits);

        tile.Sql.Should().NotContain("ORDER BY COUNT(*)");
        tile.Sql.Should().NotContain("ST_SnapToGrid");
        if (configuredLimit == 0)
        {
            tile.Sql.Should().NotContain("LIMIT");
        }
        else
        {
            tile.Sql.Should().Contain("ORDER BY _h3cell LIMIT");
        }
    }

    private static FeatureQueryBuilder CreateBuilder()
    {
        var pool = new DefaultObjectPoolProvider().Create<StringBuilder>(new FeatureStoreStringBuilderPooledObjectPolicy());
        return new FeatureQueryBuilder(pool, new GeometryProcessor());
    }
}

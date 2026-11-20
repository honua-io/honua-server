// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Raster;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Raster;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class PostgresElevationServiceTests
{
    [Fact]
    public async Task QueryPointAsync_WhenPixelHasDataIsFalse_PreservesNullElevationDespiteBandValue()
    {
        var store = new StubNoDataRasterStore();
        var crsRegistry = new StubSupportedCrsRegistry();
        var connectionProvider = new StubUnusedConnectionProvider();
        var service = new PostgresElevationService(
            connectionProvider,
            crsRegistry,
            store,
            NullLogger<PostgresElevationService>.Instance);

        var result = await service.QueryPointAsync(
            layerId: 1,
            x: 10.0,
            y: 20.0,
            srid: 4326,
            mergeStrategy: RasterMergeStrategy.Newest);

        result.Elevation.Should().BeNull();
        result.NoData.Should().BeTrue();
        result.OutOfBounds.Should().BeFalse();
    }

    [Fact]
    public void IsRasterAlignmentFailure_ExactPostgisInvariant_ReturnsTrue()
    {
        var exception = CreateInternalError(
            "rt_raster_from_two_rasters: The two rasters provided do not have the same alignment",
            "rt_raster_from_two_rasters");

        PostgresElevationService.IsRasterAlignmentFailure(exception).Should().BeTrue();
    }

    [Fact]
    public void IsRasterAlignmentFailure_UnrelatedInternalErrorFromSameRoutine_ReturnsFalse()
    {
        var exception = CreateInternalError(
            "rt_raster_from_two_rasters: raster dimensions are invalid",
            "rt_raster_from_two_rasters");

        PostgresElevationService.IsRasterAlignmentFailure(exception).Should().BeFalse();
    }

    private static PostgresException CreateInternalError(string message, string? routine)
        => new(
            messageText: message,
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.InternalError,
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: null,
            tableName: null,
            columnName: null,
            dataTypeName: null,
            constraintName: null,
            file: null,
            line: null,
            routine: routine);

    private sealed class StubUnusedConnectionProvider : IAdoNetDatabaseConnectionProvider
    {
        public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubSupportedCrsRegistry : ICrsRegistry
    {
        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(new CrsDefinition("EPSG:4326", 4326, AxisOrder.EastNorth, true));

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(new CrsDefinition($"EPSG:{srid}", srid, AxisOrder.EastNorth, true));

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public ValueTask<CoordinateSystemEntry[]> ListCoordinateSystemsAsync(string? filter = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Array.Empty<CoordinateSystemEntry>());
    }

    private sealed class StubNoDataRasterStore : IRasterStore
    {
        private static readonly RasterInfo SampleRaster = new()
        {
            Id = 42,
            LayerId = 1,
            Name = "dem",
            Width = 10,
            Height = 10,
            BandCount = 2,
            Srid = 4326,
            PixelType = "32BF",
            NoDataValue = -9999
        };

        public Task<RasterInfo[]> ListRastersAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult<RasterInfo[]>([SampleRaster]);

        public Task<RasterInfo[]> QueryRastersAsync(int layerId, RasterSelectionQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<RasterInfo[]>([SampleRaster]);

        public Task<RasterPixelValue> IdentifyMosaicAsync(
            int layerId,
            IReadOnlyList<long> rasterIds,
            RasterMergeStrategy mergeStrategy,
            double x,
            double y,
            int srid,
            int[]? bands = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new RasterPixelValue
            {
                X = x,
                Y = y,
                HasData = false,
                BandValues = new Dictionary<int, double?> { [1] = -9999.0, [2] = 123.45 }
            });

        public Task<RasterInfo?> GetPrimaryRasterInfoAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RasterInfo?> GetRasterInfoByIdAsync(int layerId, long rasterId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RasterBandInfo[]> GetBandMetadataAsync(long rasterId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, RasterBandInfo[]>> GetBandMetadataBatchAsync(IReadOnlyList<long> rasterIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RasterPixelValue> IdentifyAsync(int layerId, double x, double y, int srid, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RasterExportResult> ExportAsync(int layerId, RasterExportOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RasterExportResult> ExportMosaicAsync(int layerId, IReadOnlyList<long> rasterIds, RasterMergeStrategy mergeStrategy, RasterExportOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RasterBandStatistics[]> ComputeStatisticsAsync(int layerId, IReadOnlyList<long> rasterIds, RasterMergeStrategy mergeStrategy, int[]? bands = null, byte[]? aoiWkb = null, int? aoiSrid = null, int? histogramBins = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RasterPixelValue[]> SamplePointsAsync(int layerId, IReadOnlyList<long> rasterIds, RasterMergeStrategy mergeStrategy, IReadOnlyList<(double X, double Y)> points, int srid, int[]? bands = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> StoreRasterAsync(int layerId, string name, byte[] wkbRaster, DateTimeOffset? acquisitionDate = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteRasterAsync(long rasterId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> DeleteLayerRastersAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> EstimateExportPixelCountAsync(int layerId, IReadOnlyList<long> rasterIds, RasterExportOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> EstimateStatisticsPixelCountAsync(int layerId, IReadOnlyList<long> rasterIds, byte[]? aoiWkb = null, int? aoiSrid = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, RasterSensorMetadata>> GetSensorMetadataAsync(IReadOnlyList<long> rasterIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ClippedBandVectorBatch> ReadClippedBandVectorsAsync(int layerId, IReadOnlyList<long> rasterIds, RasterMergeStrategy mergeStrategy, byte[] clipGeometryWkb, int? clipSrid, IReadOnlyList<int> bands, long maxPixels, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RasterColormapEntry[]> GetColormapAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> GetKeyPropertiesAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]?> GetRawRasterBytesAsync(int layerId, long rasterId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

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
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Raster;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class PostgresElevationServiceTests
{
    [Fact]
    public async Task QueryPointAsync_WhenPixelHasDataIsFalse_PreservesNullElevationDespiteBandValue()
    {
        var sampleRaster = new RasterInfo
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
        var store = Substitute.For<IRasterStore>();
        store.ListRastersAsync(1, Arg.Any<CancellationToken>()).Returns([sampleRaster]);
        store.QueryRastersAsync(1, Arg.Any<RasterSelectionQuery>(), Arg.Any<CancellationToken>()).Returns([sampleRaster]);
        store.IdentifyMosaicAsync(
                1,
                Arg.Any<long[]>(),
                RasterMergeStrategy.Newest,
                10.0,
                20.0,
                4326,
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<RasterMosaicOrdering>(),
                Arg.Any<RasterMosaicAttributeSort?>(),
                Arg.Any<CancellationToken>())
            .Returns(new PixelValueResult
            {
                X = 10.0,
                Y = 20.0,
                Srid = 4326,
                HasData = false,
                BandValues = new Dictionary<int, object?> { [1] = -9999.0, [2] = 123.45 }
            });
        var crsRegistry = Substitute.For<ICrsRegistry>();
        crsRegistry.IsSridSupportedAsync(4326, Arg.Any<CancellationToken>()).Returns(true);
        crsRegistry.ResolveBySridAsync(4326, Arg.Any<CancellationToken>())
            .Returns(new CrsDefinition("EPSG:4326", 4326, AxisOrder.EastNorth, true));
        var connectionProvider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
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
}

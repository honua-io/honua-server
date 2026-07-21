// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Postgres.Features.Raster;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

public sealed class PostgresElevationServiceTests
{
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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Npgsql;
using NpgsqlTypes;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.Geometries;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private async Task<IReadOnlyList<ImportValidationIssue>> ValidateImportSridsAsync(
        int sourceSrid,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        if (sourceSrid <= 0)
        {
            return
            [
                ImportValidationIssue.Create(
                    ImportValidationErrorCodes.SourceSridUnsupported,
                    $"Source SRID {sourceSrid} is not supported.",
                    field: nameof(ImportRequest.SourceSrid))
            ];
        }

        if (targetSrid <= 0)
        {
            return
            [
                ImportValidationIssue.Create(
                    ImportValidationErrorCodes.TargetSridUnsupported,
                    $"Target SRID {targetSrid} is not supported.",
                    field: nameof(ImportRequest.TargetSrid))
            ];
        }

        if (!await _crsDetectionService.ValidateSridAsync(sourceSrid, cancellationToken))
        {
            return
            [
                ImportValidationIssue.Create(
                    ImportValidationErrorCodes.SourceSridUnsupported,
                    $"Source SRID {sourceSrid} is not registered in spatial_ref_sys.",
                    field: nameof(ImportRequest.SourceSrid))
            ];
        }

        if (!await _crsDetectionService.ValidateSridAsync(targetSrid, cancellationToken))
        {
            return
            [
                ImportValidationIssue.Create(
                    ImportValidationErrorCodes.TargetSridUnsupported,
                    $"Target SRID {targetSrid} is not registered in spatial_ref_sys.",
                    field: nameof(ImportRequest.TargetSrid))
            ];
        }

        if (sourceSrid != targetSrid && !await CanTransformAsync(sourceSrid, targetSrid, cancellationToken))
        {
            return
            [
                ImportValidationIssue.Create(
                    ImportValidationErrorCodes.ProjectionUnsupported,
                    $"Source SRID {sourceSrid} cannot be transformed to target SRID {targetSrid}.",
                    field: nameof(ImportRequest.TargetSrid))
            ];
        }

        return [];
    }

    private async Task<bool> CanTransformAsync(int sourceSrid, int targetSrid, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT ST_Transform(ST_SetSRID(ST_MakePoint(0, 0), @source_srid), @target_srid) IS NOT NULL",
                connection);
            command.Parameters.Add("source_srid", NpgsqlDbType.Integer).Value = sourceSrid;
            command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is bool transformed && transformed;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    private static (bool HasZ, bool HasM) DetectZm(NtsGeometry geometry)
    {
        var filter = new ZmCoordinateFilter();
        geometry.Apply(filter);
        return (filter.HasZ, filter.HasM);
    }

    private sealed class ZmCoordinateFilter : ICoordinateSequenceFilter
    {
        public bool HasZ { get; private set; }

        public bool HasM { get; private set; }

        public bool Done => HasZ && HasM;

        public bool GeometryChanged => false;

        public void Filter(CoordinateSequence seq, int i)
        {
            if (!HasZ && seq.HasZ && !double.IsNaN(seq.GetZ(i)))
            {
                HasZ = true;
            }

            if (!HasM && seq.HasM && !double.IsNaN(seq.GetM(i)))
            {
                HasM = true;
            }
        }
    }

    private static string? ValidateGeometry(NtsGeometry geometry)
    {
        // Validate coordinate values (NaN/Infinity) without copying the coordinate array.
        if (!ValidateCoordinates(geometry))
        {
            return "Geometry contains invalid coordinates (NaN or Infinity)";
        }

        return null;
    }

    private static bool ValidateCoordinates(NtsGeometry geometry)
    {
        var filter = new FiniteCoordinateFilter();
        geometry.Apply(filter);
        return filter.AllFinite;
    }

    private sealed class FiniteCoordinateFilter : ICoordinateSequenceFilter
    {
        public bool AllFinite { get; private set; } = true;

        public bool Done => !AllFinite;

        public bool GeometryChanged => false;

        public void Filter(CoordinateSequence seq, int i)
        {
            var x = seq.GetX(i);
            var y = seq.GetY(i);
            if (double.IsNaN(x) || double.IsInfinity(x)
                || double.IsNaN(y) || double.IsInfinity(y))
            {
                AllFinite = false;
                return;
            }

            if (seq.HasZ)
            {
                var z = seq.GetZ(i);
                if (!double.IsNaN(z) && double.IsInfinity(z))
                {
                    AllFinite = false;
                }
            }
        }
    }

    private static void ValidateTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be null or empty", nameof(tableName));

        if (tableName.Length > 63)
            throw new ArgumentException("Table name exceeds PostgreSQL identifier limit of 63 characters", nameof(tableName));

        if (!System.Text.RegularExpressions.Regex.IsMatch(tableName, @"^[a-zA-Z][a-zA-Z0-9_]*$"))
            throw new ArgumentException("Table name must start with a letter and contain only letters, digits, and underscores", nameof(tableName));

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER", "TABLE", "INDEX", "VIEW", "DATABASE", "SCHEMA"
        };

        if (keywords.Contains(tableName))
            throw new ArgumentException(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "Table name '{0}' conflicts with SQL keywords", tableName),
                nameof(tableName));
    }
}

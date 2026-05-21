// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Npgsql;
using NpgsqlTypes;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.Geometries;

namespace Honua.Postgres.Features.Import;

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

        if (!await _crsDetectionService.ValidateSridAsync(sourceSrid))
        {
            return
            [
                ImportValidationIssue.Create(
                    ImportValidationErrorCodes.SourceSridUnsupported,
                    $"Source SRID {sourceSrid} is not registered in spatial_ref_sys.",
                    field: nameof(ImportRequest.SourceSrid))
            ];
        }

        if (!await _crsDetectionService.ValidateSridAsync(targetSrid))
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

    private static bool HasZ(NtsGeometry geometry)
        => geometry.Coordinates.Any(coordinate => !double.IsNaN(coordinate.Z));

    /// <summary>
    /// Validates a geometry against configured limits.
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    private string? ValidateGeometry(NtsGeometry geometry)
    {
        // Count vertices
        var vertexCount = CountVertices(geometry);
        if (vertexCount > _limits.MaxVertices)
        {
            return $"Vertex count ({vertexCount:N0}) exceeds maximum allowed ({_limits.MaxVertices:N0})";
        }

        // Count rings for polygon geometries
        var ringCount = CountRings(geometry);
        if (ringCount > _limits.MaxRings)
        {
            return $"Ring count ({ringCount:N0}) exceeds maximum allowed ({_limits.MaxRings:N0})";
        }

        // Validate coordinate values
        if (!ValidateCoordinates(geometry))
        {
            return "Geometry contains invalid coordinates (NaN or Infinity)";
        }

        return null;
    }

    /// <summary>
    /// Counts the total number of vertices in a geometry.
    /// </summary>
    private static int CountVertices(NtsGeometry geometry)
    {
        return geometry.NumPoints;
    }

    /// <summary>
    /// Counts the total number of rings in polygon geometries.
    /// </summary>
    private static int CountRings(NtsGeometry geometry)
    {
        return geometry switch
        {
            Polygon polygon => 1 + polygon.NumInteriorRings,
            MultiPolygon multiPolygon => multiPolygon.Geometries
                .OfType<Polygon>()
                .Sum(p => 1 + p.NumInteriorRings),
            GeometryCollection collection => collection.Geometries
                .Sum(CountRings),
            _ => 0
        };
    }

    /// <summary>
    /// Validates that all coordinates in the geometry are finite numbers.
    /// </summary>
    private static bool ValidateCoordinates(NtsGeometry geometry)
    {
        foreach (var coord in geometry.Coordinates)
        {
            if (double.IsNaN(coord.X) || double.IsInfinity(coord.X) ||
                double.IsNaN(coord.Y) || double.IsInfinity(coord.Y))
            {
                return false;
            }

            // Check Z if present
            if (!double.IsNaN(coord.Z) && double.IsInfinity(coord.Z))
            {
                return false;
            }
        }

        return true;
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

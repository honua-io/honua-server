// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Implements three-layer geometry validation and repair.
/// Layer 1: Esri JSON input validation
/// Layer 2: WKB structure validation
/// Layer 3: PostGIS topology validation
/// </summary>
internal sealed class GeometryValidator : IGeometryValidator
{
    private readonly GeometryValidationOptions _options;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<GeometryValidator> _logger;
    private readonly WKBReader _wkbReader = new();

    public GeometryValidator(
        IOptions<LimitsOptions> limitsOptions,
        IDatabaseConnectionProvider connectionProvider,
        ILogger<GeometryValidator> logger)
    {
        _options = limitsOptions?.Value?.Validation ?? new GeometryValidationOptions();
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public GeometryValidationResult ValidateEsriJson(object? geometry)
    {
        if (geometry == null)
        {
            return GeometryValidationResult.NullGeometry(_options.AllowNullGeometry);
        }

        if (geometry is not GeoServicesGeometry geoServicesGeometry)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.InvalidJsonStructure,
                "Geometry must be a valid GeoServices JSON object");
        }

        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        // Validate point geometry
        if (geoServicesGeometry.X.HasValue || geoServicesGeometry.Y.HasValue)
        {
            ValidatePointCoordinates(geoServicesGeometry, errors);
        }

        // Validate envelope geometry
        if (geoServicesGeometry.Xmin.HasValue || geoServicesGeometry.Ymin.HasValue ||
            geoServicesGeometry.Xmax.HasValue || geoServicesGeometry.Ymax.HasValue)
        {
            ValidateEnvelopeCoordinates(geoServicesGeometry, errors);
        }

        // Validate multipoint geometry
        if (geoServicesGeometry.Points != null)
        {
            ValidateMultiPointCoordinates(geoServicesGeometry.Points, errors, warnings);
        }

        // Validate path (linestring) geometry
        if (geoServicesGeometry.Paths != null)
        {
            ValidatePathCoordinates(geoServicesGeometry.Paths, errors, warnings);
        }

        // Validate ring (polygon) geometry
        if (geoServicesGeometry.Rings != null)
        {
            ValidateRingCoordinates(geoServicesGeometry.Rings, errors, warnings);
        }

        if (errors.Count > 0)
        {
            return new GeometryValidationResult
            {
                IsValid = false,
                Errors = errors.ToImmutableList(),
                Warnings = warnings.ToImmutableList()
            };
        }

        return new GeometryValidationResult
        {
            IsValid = true,
            Warnings = warnings.ToImmutableList()
        };
    }

    /// <inheritdoc />
    public GeometryValidationResult ValidateWkb(byte[]? wkb)
    {
        if (wkb == null || wkb.Length == 0)
        {
            return GeometryValidationResult.NullGeometry(_options.AllowNullGeometry);
        }

        // Check WKB size limit
        if (wkb.Length > _options.MaxWkbSize)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.WkbTooLarge,
                $"WKB size ({wkb.Length:N0} bytes) exceeds maximum allowed ({_options.MaxWkbSize:N0} bytes)");
        }

        try
        {
            var geometry = _wkbReader.Read(wkb);

            if (geometry == null || geometry.IsEmpty)
            {
                return GeometryValidationResult.Failure(
                    ValidationErrorCode.EmptyGeometry,
                    "Geometry is empty");
            }

            // Count vertices
            var vertexCount = CountVertices(geometry);
            if (vertexCount > _options.MaxVertices)
            {
                return GeometryValidationResult.Failure(
                    ValidationErrorCode.TooManyVertices,
                    $"Geometry has {vertexCount:N0} vertices, exceeds maximum allowed ({_options.MaxVertices:N0})");
            }

            // Count rings for polygons
            var ringCount = CountRings(geometry);
            if (ringCount > _options.MaxRings)
            {
                return GeometryValidationResult.Failure(
                    ValidationErrorCode.TooManyRings,
                    $"Geometry has {ringCount:N0} rings, exceeds maximum allowed ({_options.MaxRings:N0})");
            }

            var stats = new GeometryStats
            {
                GeometryType = geometry.GeometryType,
                VertexCount = vertexCount,
                RingCount = ringCount,
                WkbSize = wkb.Length,
                HasZ = geometry.Coordinate?.Z is not null && !double.IsNaN(geometry.Coordinate.Z),
                HasM = geometry.Coordinate is CoordinateM,
                Srid = geometry.SRID > 0 ? geometry.SRID : null
            };

            return GeometryValidationResult.Success(stats);
        }
        catch (Exception ex) when (ex is ParseException or FormatException)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.InvalidWkbStructure,
                $"Invalid WKB structure: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableTopologyValidation)
        {
            return GeometryValidationResult.Success();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.ValidationTimeout);

        try
        {
            await using var connection = await _connectionProvider.GetConnectionAsync(cts.Token);

            // Check if geometry is valid using PostGIS ST_IsValid
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT ST_IsValid($1::geometry), ST_IsValidReason($1::geometry)";
            cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });

            await using var reader = await cmd.ExecuteReaderAsync(cts.Token);

            if (await reader.ReadAsync(cts.Token))
            {
                var isValid = reader.GetBoolean(0);
                var reason = reader.IsDBNull(1) ? null : reader.GetString(1);

                if (isValid)
                {
                    return GeometryValidationResult.Success();
                }

                var errorCode = MapPostGisReasonToErrorCode(reason);
                return GeometryValidationResult.Failure(errorCode, reason ?? "Geometry is topologically invalid");
            }

            return GeometryValidationResult.Success();
        }
        catch (OperationCanceledException)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.ValidationTimeout,
                $"Topology validation timed out after {_options.ValidationTimeout.TotalSeconds} seconds");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Topology validation failed");
            return GeometryValidationResult.Failure(
                ValidationErrorCode.InvalidTopology,
                $"Topology validation error: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableAutoRepair)
        {
            return GeometryRepairResult.Failed("Auto-repair is disabled");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.ValidationTimeout);

        try
        {
            await using var connection = await _connectionProvider.GetConnectionAsync(cts.Token);

            // First get the validation reason
            string? originalReason = null;
            await using (var reasonCmd = connection.CreateCommand())
            {
                reasonCmd.CommandText = "SELECT ST_IsValidReason($1::geometry)";
                reasonCmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
                var result = await reasonCmd.ExecuteScalarAsync(cts.Token);
                originalReason = result as string;
            }

            // Apply ST_MakeValid to repair
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    ST_AsBinary(repaired),
                    ST_GeometryType(repaired),
                    ST_GeometryType($1::geometry) as original_type,
                    ST_IsEmpty(repaired)
                FROM (SELECT ST_MakeValid($1::geometry) as repaired) sub";
            cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });

            await using var reader = await cmd.ExecuteReaderAsync(cts.Token);

            if (await reader.ReadAsync(cts.Token))
            {
                var isEmpty = reader.GetBoolean(3);
                if (isEmpty)
                {
                    return GeometryRepairResult.Failed(
                        "Repair resulted in empty geometry",
                        originalReason);
                }

                var repairedWkb = reader.IsDBNull(0) ? null : (byte[])reader[0];
                if (repairedWkb == null)
                {
                    return GeometryRepairResult.Failed("Repair produced null result", originalReason);
                }

                var repairedType = reader.GetString(1);
                var originalType = reader.GetString(2);
                var typeChanged = !string.Equals(repairedType, originalType, StringComparison.OrdinalIgnoreCase);

                var repairs = new List<string>();
                if (originalReason != null && !originalReason.StartsWith("Valid Geometry", StringComparison.OrdinalIgnoreCase))
                {
                    repairs.Add($"Fixed: {originalReason}");
                }

                if (typeChanged)
                {
                    repairs.Add($"Geometry type changed from {originalType} to {repairedType}");
                }

                _logger.LogDebug("Geometry repaired: {OriginalReason}", originalReason);

                return new GeometryRepairResult
                {
                    Success = true,
                    RepairedWkb = repairedWkb,
                    RepairsApplied = repairs.ToImmutableList(),
                    OriginalValidationReason = originalReason,
                    GeometryTypeChanged = typeChanged
                };
            }

            return GeometryRepairResult.Failed("No result from repair operation", originalReason);
        }
        catch (OperationCanceledException)
        {
            return GeometryRepairResult.Failed($"Repair timed out after {_options.ValidationTimeout.TotalSeconds} seconds");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geometry repair failed");
            return GeometryRepairResult.Failed($"Repair error: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<GeometryValidationResult> ValidateCompleteAsync(byte[] wkb, CancellationToken cancellationToken = default)
    {
        // Layer 2: WKB validation
        var wkbResult = ValidateWkb(wkb);
        if (!wkbResult.IsValid)
        {
            return wkbResult;
        }

        // Accept mode - skip topology validation
        if (_options.Mode == ValidationMode.Accept)
        {
            return wkbResult;
        }

        // Layer 3: Topology validation
        var topologyResult = await ValidateTopologyAsync(wkb, cancellationToken);

        if (topologyResult.IsValid)
        {
            return new GeometryValidationResult
            {
                IsValid = true,
                Stats = wkbResult.Stats
            };
        }

        // Strict mode - reject invalid geometry
        if (_options.Mode == ValidationMode.Strict)
        {
            return topologyResult;
        }

        // Repair mode - attempt to fix
        var repairResult = await RepairAsync(wkb, cancellationToken);

        if (!repairResult.Success)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.RepairFailed,
                repairResult.ErrorMessage ?? "Geometry repair failed");
        }

        // Validate repaired geometry passes topology check
        if (repairResult.RepairedWkb != null)
        {
            var repairedTopology = await ValidateTopologyAsync(repairResult.RepairedWkb, cancellationToken);
            if (!repairedTopology.IsValid)
            {
                return GeometryValidationResult.Failure(
                    ValidationErrorCode.RepairFailed,
                    "Repaired geometry still invalid: " + string.Join(", ", repairedTopology.Errors.Select(e => e.Message)));
            }
        }

        // Return success with repaired geometry
        var warnings = new List<ValidationIssue>();
        foreach (var repair in repairResult.RepairsApplied)
        {
            warnings.Add(new ValidationIssue(ValidationErrorCode.None, repair));
        }

        return GeometryValidationResult.SuccessWithRepair(repairResult.RepairedWkb!, warnings);
    }

    #region Private Validation Helpers

    private void ValidatePointCoordinates(GeoServicesGeometry geometry, List<ValidationIssue> errors)
    {
        if (!geometry.X.HasValue || !geometry.Y.HasValue)
        {
            errors.Add(new ValidationIssue(ValidationErrorCode.MissingCoordinates, "Point requires both X and Y coordinates"));
            return;
        }

        ValidateSingleCoordinate(geometry.X.Value, "X", errors);
        ValidateSingleCoordinate(geometry.Y.Value, "Y", errors);

        if (geometry.Z.HasValue)
        {
            ValidateSingleCoordinate(geometry.Z.Value, "Z", errors);
        }
    }

    private void ValidateEnvelopeCoordinates(GeoServicesGeometry geometry, List<ValidationIssue> errors)
    {
        if (!geometry.Xmin.HasValue || !geometry.Ymin.HasValue ||
            !geometry.Xmax.HasValue || !geometry.Ymax.HasValue)
        {
            errors.Add(new ValidationIssue(ValidationErrorCode.MissingCoordinates, "Envelope requires xmin, ymin, xmax, ymax"));
            return;
        }

        ValidateSingleCoordinate(geometry.Xmin.Value, "xmin", errors);
        ValidateSingleCoordinate(geometry.Ymin.Value, "ymin", errors);
        ValidateSingleCoordinate(geometry.Xmax.Value, "xmax", errors);
        ValidateSingleCoordinate(geometry.Ymax.Value, "ymax", errors);

        if (geometry.Xmin.Value > geometry.Xmax.Value)
        {
            errors.Add(new ValidationIssue(ValidationErrorCode.InvalidCoordinates, "xmin must be less than or equal to xmax"));
        }

        if (geometry.Ymin.Value > geometry.Ymax.Value)
        {
            errors.Add(new ValidationIssue(ValidationErrorCode.InvalidCoordinates, "ymin must be less than or equal to ymax"));
        }
    }

    private void ValidateMultiPointCoordinates(double[][] points, List<ValidationIssue> errors, List<ValidationIssue> warnings)
    {
        if (points.Length == 0)
        {
            errors.Add(new ValidationIssue(ValidationErrorCode.MissingCoordinates, "MultiPoint requires at least one point"));
            return;
        }

        var totalVertices = points.Length;
        if (totalVertices > _options.MaxVertices)
        {
            errors.Add(new ValidationIssue(
                ValidationErrorCode.TooManyVertices,
                $"MultiPoint has {totalVertices} points, exceeds maximum allowed ({_options.MaxVertices})"));
            return;
        }

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point.Length < 2)
            {
                errors.Add(new ValidationIssue(ValidationErrorCode.MissingCoordinates, $"Point {i} requires at least 2 coordinates"));
                continue;
            }

            ValidateSingleCoordinate(point[0], $"Point[{i}].X", errors);
            ValidateSingleCoordinate(point[1], $"Point[{i}].Y", errors);
        }
    }

    private void ValidatePathCoordinates(double[][][] paths, List<ValidationIssue> errors, List<ValidationIssue> warnings)
    {
        if (paths.Length == 0)
        {
            errors.Add(new ValidationIssue(ValidationErrorCode.MissingCoordinates, "Paths requires at least one path"));
            return;
        }

        var totalVertices = paths.Sum(p => p.Length);
        if (totalVertices > _options.MaxVertices)
        {
            errors.Add(new ValidationIssue(
                ValidationErrorCode.TooManyVertices,
                $"Paths have {totalVertices} vertices, exceeds maximum allowed ({_options.MaxVertices})"));
            return;
        }

        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            if (path.Length < 2)
            {
                errors.Add(new ValidationIssue(ValidationErrorCode.InsufficientPoints, $"Path {i} requires at least 2 points"));
                continue;
            }

            for (var j = 0; j < path.Length; j++)
            {
                var point = path[j];
                if (point.Length < 2)
                {
                    errors.Add(new ValidationIssue(ValidationErrorCode.MissingCoordinates, $"Path[{i}][{j}] requires at least 2 coordinates"));
                    continue;
                }

                ValidateSingleCoordinate(point[0], $"Path[{i}][{j}].X", errors);
                ValidateSingleCoordinate(point[1], $"Path[{i}][{j}].Y", errors);
            }
        }
    }

    private void ValidateRingCoordinates(double[][][] rings, List<ValidationIssue> errors, List<ValidationIssue> warnings)
    {
        if (rings.Length == 0)
        {
            errors.Add(new ValidationIssue(ValidationErrorCode.MissingCoordinates, "Polygon requires at least one ring"));
            return;
        }

        if (rings.Length > _options.MaxRings)
        {
            errors.Add(new ValidationIssue(
                ValidationErrorCode.TooManyRings,
                $"Polygon has {rings.Length} rings, exceeds maximum allowed ({_options.MaxRings})"));
            return;
        }

        var totalVertices = rings.Sum(r => r.Length);
        if (totalVertices > _options.MaxVertices)
        {
            errors.Add(new ValidationIssue(
                ValidationErrorCode.TooManyVertices,
                $"Polygon has {totalVertices} vertices, exceeds maximum allowed ({_options.MaxVertices})"));
            return;
        }

        for (var i = 0; i < rings.Length; i++)
        {
            var ring = rings[i];
            if (ring.Length < 4)
            {
                errors.Add(new ValidationIssue(ValidationErrorCode.InsufficientPoints, $"Ring {i} requires at least 4 points (including closing point)"));
                continue;
            }

            for (var j = 0; j < ring.Length; j++)
            {
                var point = ring[j];
                if (point.Length < 2)
                {
                    errors.Add(new ValidationIssue(ValidationErrorCode.MissingCoordinates, $"Ring[{i}][{j}] requires at least 2 coordinates"));
                    continue;
                }

                ValidateSingleCoordinate(point[0], $"Ring[{i}][{j}].X", errors);
                ValidateSingleCoordinate(point[1], $"Ring[{i}][{j}].Y", errors);
            }

            // Check ring closure
            var first = ring[0];
            var last = ring[^1];
            if (first.Length >= 2 && last.Length >= 2 &&
                (Math.Abs(first[0] - last[0]) > 1e-10 || Math.Abs(first[1] - last[1]) > 1e-10))
            {
                warnings.Add(new ValidationIssue(ValidationErrorCode.UnclosedRing, $"Ring {i} is not closed, will be auto-closed"));
            }
        }
    }

    private static void ValidateSingleCoordinate(double value, string name, List<ValidationIssue> errors)
    {
        if (double.IsNaN(value))
        {
            errors.Add(new ValidationIssue(ValidationErrorCode.InvalidCoordinates, $"{name} is NaN"));
        }
        else if (double.IsInfinity(value))
        {
            errors.Add(new ValidationIssue(ValidationErrorCode.InvalidCoordinates, $"{name} is Infinity"));
        }
    }

    private static int CountVertices(Geometry geometry)
    {
        return geometry.NumPoints;
    }

    private static int CountRings(Geometry geometry)
    {
        return geometry switch
        {
            Polygon polygon => 1 + polygon.NumInteriorRings,
            MultiPolygon multiPolygon => Enumerable.Range(0, multiPolygon.NumGeometries)
                .Sum(i => CountRings(multiPolygon.GetGeometryN(i))),
            GeometryCollection collection => Enumerable.Range(0, collection.NumGeometries)
                .Sum(i => CountRings(collection.GetGeometryN(i))),
            _ => 0
        };
    }

    private static ValidationErrorCode MapPostGisReasonToErrorCode(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return ValidationErrorCode.InvalidTopology;
        }

        return reason.ToUpperInvariant() switch
        {
            var r when r.Contains("SELF-INTERSECTION") => ValidationErrorCode.SelfIntersection,
            var r when r.Contains("DUPLICATE") => ValidationErrorCode.DuplicatePoints,
            var r when r.Contains("RING") && r.Contains("ORIENT") => ValidationErrorCode.IncorrectOrientation,
            var r when r.Contains("HOLE") && r.Contains("OUTSIDE") => ValidationErrorCode.HoleOutsideShell,
            var r when r.Contains("NESTED") => ValidationErrorCode.NestedHoles,
            var r when r.Contains("DISCONNECT") => ValidationErrorCode.DisconnectedInterior,
            _ => ValidationErrorCode.InvalidTopology
        };
    }

    #endregion
}

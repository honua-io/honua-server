// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Implements three-layer geometry validation and repair.
/// Layer 1: Esri JSON input validation
/// Layer 2: WKB structure validation
/// Layer 3: Topology validation via configured backend
/// </summary>
internal sealed partial class GeometryValidator : IGeometryValidator
{
    private readonly GeometryValidationOptions _options;
    private readonly WkbGeometryValidator _binary;

    public GeometryValidator(
        IOptions<LimitsOptions> limitsOptions,
        IGeometryTopologyValidator topologyValidator,
        ILogger<GeometryValidator> logger)
    {
        var resolvedLimits = limitsOptions?.Value ?? new LimitsOptions();
        _options = resolvedLimits.Validation ?? new GeometryValidationOptions();
        _binary = new WkbGeometryValidator(limitsOptions ?? Options.Create(resolvedLimits), topologyValidator, logger);
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
    public GeometryValidationResult ValidateWkb(byte[]? wkb) => _binary.ValidateWkb(wkb);

    /// <inheritdoc />
    public Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => _binary.ValidateTopologyAsync(wkb, cancellationToken);

    /// <inheritdoc />
    public Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => _binary.RepairAsync(wkb, cancellationToken);

    /// <inheritdoc />
    public Task<GeometryValidationResult> ValidateCompleteAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => _binary.ValidateCompleteAsync(wkb, cancellationToken);

    #region Private Validation Helpers

    private static void ValidatePointCoordinates(GeoServicesGeometry geometry, List<ValidationIssue> errors)
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

    private static void ValidateEnvelopeCoordinates(GeoServicesGeometry geometry, List<ValidationIssue> errors)
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
                (Math.Abs(first[0] - last[0]) > _options.RingClosureTolerance ||
                 Math.Abs(first[1] - last[1]) > _options.RingClosureTolerance))
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

    #endregion
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Validation;

internal sealed partial class WkbGeometryValidator : IGeometryValidator
{
    private readonly GeometryValidationOptions _options;
    private readonly GeometryLimits _geometryLimits;
    private readonly IGeometryTopologyValidator _topologyValidator;
    private readonly ILogger _logger;

    public WkbGeometryValidator(
        IOptions<LimitsOptions> limitsOptions,
        IGeometryTopologyValidator topologyValidator,
        ILogger logger)
    {
        var resolvedLimits = limitsOptions?.Value ?? new LimitsOptions();
        _options = resolvedLimits.Validation ?? new GeometryValidationOptions();
        _geometryLimits = resolvedLimits.Geometry ?? new GeometryLimits();
        _topologyValidator = topologyValidator ?? throw new ArgumentNullException(nameof(topologyValidator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public GeometryValidationResult ValidateEsriJson(object? geometry) => geometry is null
        ? GeometryValidationResult.NullGeometry(_options.AllowNullGeometry)
        : GeometryValidationResult.Failure(ValidationErrorCode.InvalidJsonStructure,
            "Geometry must be a valid GeoServices JSON object");

    public GeometryValidationResult ValidateWkb(byte[]? wkb)
    {
        if (wkb == null || wkb.Length == 0)
        {
            return GeometryValidationResult.NullGeometry(_options.AllowNullGeometry);
        }

        var maxWkbSize = Math.Min(_options.MaxWkbSize, _geometryLimits.MaxGeometrySize);
        if (wkb.Length > maxWkbSize)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.WkbTooLarge,
                $"WKB size ({wkb.Length:N0} bytes) exceeds maximum allowed ({maxWkbSize:N0} bytes)");
        }

        var scanner = new WkbGeometryScanner(wkb);
        var scanOutcome = scanner.ScanRoot();
        if (scanOutcome == WkbScanOutcome.Invalid)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.InvalidWkbStructure,
                "Invalid WKB structure.");
        }

        if (scanOutcome == WkbScanOutcome.Empty)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.EmptyGeometry,
                "Geometry is empty");
        }

        var state = scanner.State;
        if (state.HasNonFiniteCoordinate)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.InvalidCoordinates,
                "Geometry contains non-finite coordinates.");
        }

        // Count vertices
        var maxVertices = Math.Min(_options.MaxVertices, _geometryLimits.MaxVerticesPerGeometry);
        if (state.VertexCount > maxVertices)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.TooManyVertices,
                $"Geometry has {state.VertexCount:N0} vertices, exceeds maximum allowed ({maxVertices:N0})");
        }

        // Count rings for polygons
        if (state.RingCount > _options.MaxRings)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.TooManyRings,
                $"Geometry has {state.RingCount:N0} rings, exceeds maximum allowed ({_options.MaxRings:N0})");
        }

        var stats = new GeometryStats
        {
            GeometryType = state.RootGeometryType!,
            VertexCount = (int)state.VertexCount,
            RingCount = (int)state.RingCount,
            WkbSize = wkb.Length,
            HasZ = state.HasZ,
            HasM = state.HasM,
            Srid = state.RootSrid
        };

        return GeometryValidationResult.Success(stats);
    }

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
            return await _topologyValidator.ValidateTopologyAsync(wkb, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.ValidationTimeout,
                $"Topology validation timed out after {_options.ValidationTimeout.TotalSeconds} seconds");
        }
        // Intentionally generic: the underlying topology validator can throw a wide range of
        // provider/native-library exception types for malformed geometry; any failure here is
        // logged and reported as a validation failure rather than propagating.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogTopologyValidationFailed(_logger, ex);
            return GeometryValidationResult.Failure(
                ValidationErrorCode.InvalidTopology,
                "Topology validation error.");
        }
    }

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
            var repairResult = await _topologyValidator.RepairAsync(wkb, cts.Token).ConfigureAwait(false);
            if (repairResult.Success)
            {
                LogGeometryRepaired(_logger, repairResult.OriginalValidationReason);
            }

            return repairResult;
        }
        catch (OperationCanceledException)
        {
            return GeometryRepairResult.Failed($"Repair timed out after {_options.ValidationTimeout.TotalSeconds} seconds");
        }
        // Intentionally generic: the underlying topology validator can throw a wide range of
        // provider/native-library exception types for malformed geometry; any failure here is
        // logged and reported as a repair failure rather than propagating.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogGeometryRepairFailed(_logger, ex);
            return GeometryRepairResult.Failed("Repair error.");
        }
    }

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

        return GeometryValidationResult.SuccessWithRepair(repairResult.RepairedWkb!, warnings) with
        {
            GeometryTypeChanged = repairResult.GeometryTypeChanged
        };
    }

    #region Logging

    [LoggerMessage(EventId = 2501, Level = LogLevel.Warning, Message = "Topology validation failed")]
    private static partial void LogTopologyValidationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2502, Level = LogLevel.Debug, Message = "Geometry repaired: {OriginalReason}")]
    private static partial void LogGeometryRepaired(ILogger logger, string? originalReason);

    [LoggerMessage(EventId = 2503, Level = LogLevel.Warning, Message = "Geometry repair failed")]
    private static partial void LogGeometryRepairFailed(ILogger logger, Exception exception);

    #endregion
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Validation;

internal sealed partial class WkbGeometryValidator : IGeometryValidator
{
    private const uint EwkbZFlag = 0x80000000u;
    private const uint EwkbMFlag = 0x40000000u;
    private const uint EwkbSridFlag = 0x20000000u;
    private const uint EwkbFlagMask = EwkbZFlag | EwkbMFlag | EwkbSridFlag;
    private const int MaxWkbNestingDepth = 128;

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

        // Check WKB size limit
        var maxWkbSize = Math.Min(_options.MaxWkbSize, _geometryLimits.MaxGeometrySize);
        if (wkb.Length > maxWkbSize)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.WkbTooLarge,
                $"WKB size ({wkb.Length:N0} bytes) exceeds maximum allowed ({maxWkbSize:N0} bytes)");
        }

        var offset = 0;
        var state = default(WkbScanState);
        if (!TryScanGeometry(wkb, ref offset, ref state, expectedType: null, depth: 0, out var isEmpty))
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.InvalidWkbStructure,
                "Invalid WKB structure.");
        }

        if (isEmpty)
        {
            return GeometryValidationResult.Failure(
                ValidationErrorCode.EmptyGeometry,
                "Geometry is empty");
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

    #region Private Validation Helpers

    private struct WkbScanState
    {
        public string? RootGeometryType;
        public int? RootSrid;
        public long VertexCount;
        public long RingCount;
        public bool HasZ;
        public bool HasM;
    }

    private static bool TryScanGeometry(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        ref WkbScanState state,
        uint? expectedType,
        int depth,
        out bool isEmpty)
    {
        isEmpty = true;
        if (depth > MaxWkbNestingDepth || offset >= wkb.Length)
        {
            return false;
        }

        var byteOrder = wkb[offset++];
        if (byteOrder is not (0 or 1))
        {
            return false;
        }

        var littleEndian = byteOrder == 1;
        if (!TryReadUInt32(wkb, ref offset, littleEndian, out var rawType))
        {
            return false;
        }

        var hasSrid = (rawType & EwkbSridFlag) != 0;
        var typeWithoutFlags = rawType & ~EwkbFlagMask;
        var isoDimension = typeWithoutFlags / 1000;
        if (isoDimension > 3)
        {
            return false;
        }

        var baseType = typeWithoutFlags % 1000;
        if (baseType is < 1 or > 7)
        {
            return false;
        }

        if (expectedType.HasValue && baseType != expectedType.Value)
        {
            return false;
        }

        var nodeHasZ = (rawType & EwkbZFlag) != 0 || isoDimension is 1 or 3;
        var nodeHasM = (rawType & EwkbMFlag) != 0 || isoDimension is 2 or 3;
        var ordinateCount = 2 + (nodeHasZ ? 1 : 0) + (nodeHasM ? 1 : 0);

        if (hasSrid)
        {
            if (!TryReadInt32(wkb, ref offset, littleEndian, out var srid))
            {
                return false;
            }

            if (depth == 0 && srid > 0)
            {
                state.RootSrid = srid;
            }
        }

        if (depth == 0)
        {
            state.RootGeometryType = ResolveGeometryTypeName(baseType);
        }

        switch (baseType)
        {
            case 1:
                {
                    var pointBytes = ordinateCount * sizeof(double);
                    if (wkb.Length - offset < pointBytes)
                    {
                        return false;
                    }

                    var x = ReadDouble(wkb.Slice(offset, sizeof(double)), littleEndian);
                    var y = ReadDouble(wkb.Slice(offset + sizeof(double), sizeof(double)), littleEndian);
                    if (double.IsNaN(x) && double.IsNaN(y))
                    {
                        isEmpty = true;
                        offset += pointBytes;
                        return true;
                    }

                    isEmpty = false;
                    state.VertexCount++;
                    if (nodeHasZ && !state.HasZ)
                    {
                        var z = ReadDouble(wkb.Slice(offset + (2 * sizeof(double)), sizeof(double)), littleEndian);
                        if (!double.IsNaN(z))
                        {
                            state.HasZ = true;
                        }
                    }

                    if (nodeHasM && !state.HasM)
                    {
                        var mOffset = (nodeHasZ ? 3 : 2) * sizeof(double);
                        var m = ReadDouble(wkb.Slice(offset + mOffset, sizeof(double)), littleEndian);
                        if (!double.IsNaN(m))
                        {
                            state.HasM = true;
                        }
                    }

                    offset += pointBytes;
                    return true;
                }
            case 2:
                {
                    if (!TryReadUInt32(wkb, ref offset, littleEndian, out var numPoints) || numPoints == 1)
                    {
                        return false;
                    }

                    if (!TryScanCoordinateSequence(
                            wkb,
                            ref offset,
                            numPoints,
                            ordinateCount,
                            littleEndian,
                            nodeHasZ,
                            nodeHasM,
                            ref state))
                    {
                        return false;
                    }

                    isEmpty = numPoints == 0;
                    return true;
                }
            case 3:
                {
                    if (!TryReadUInt32(wkb, ref offset, littleEndian, out var numRings))
                    {
                        return false;
                    }

                    if (numRings > (uint)((wkb.Length - offset) / sizeof(uint)))
                    {
                        return false;
                    }

                    if (numRings == 0)
                    {
                        state.RingCount += 1;
                        isEmpty = true;
                        return true;
                    }

                    state.RingCount += numRings;
                    var polygonEmpty = true;
                    for (uint ring = 0; ring < numRings; ring++)
                    {
                        if (!TryReadUInt32(wkb, ref offset, littleEndian, out var numPoints))
                        {
                            return false;
                        }

                        if (!TryScanCoordinateSequence(
                                wkb,
                                ref offset,
                                numPoints,
                                ordinateCount,
                                littleEndian,
                                nodeHasZ,
                                nodeHasM,
                                ref state))
                        {
                            return false;
                        }

                        if (ring == 0 && numPoints > 0)
                        {
                            polygonEmpty = false;
                        }
                    }

                    isEmpty = polygonEmpty;
                    return true;
                }
            case 4 or 5 or 6 or 7:
                {
                    if (!TryReadUInt32(wkb, ref offset, littleEndian, out var numGeometries))
                    {
                        return false;
                    }

                    if (numGeometries > (uint)((wkb.Length - offset) / 5))
                    {
                        return false;
                    }

                    uint? childExpected = baseType == 7 ? null : baseType - 3;
                    var collectionEmpty = true;
                    for (uint i = 0; i < numGeometries; i++)
                    {
                        if (!TryScanGeometry(wkb, ref offset, ref state, childExpected, depth + 1, out var childEmpty))
                        {
                            return false;
                        }

                        if (!childEmpty)
                        {
                            collectionEmpty = false;
                        }
                    }

                    isEmpty = collectionEmpty;
                    return true;
                }
            default:
                return false;
        }
    }

    private static bool TryScanCoordinateSequence(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        uint numPoints,
        int ordinateCount,
        bool littleEndian,
        bool nodeHasZ,
        bool nodeHasM,
        ref WkbScanState state)
    {
        var byteCount = (ulong)numPoints * (ulong)ordinateCount * sizeof(double);
        if (byteCount > (ulong)(wkb.Length - offset))
        {
            return false;
        }

        state.VertexCount += numPoints;

        if (numPoints > 0 && ((nodeHasZ && !state.HasZ) || (nodeHasM && !state.HasM)))
        {
            var stride = ordinateCount * sizeof(double);
            var zOffset = 2 * sizeof(double);
            var mOffset = (nodeHasZ ? 3 : 2) * sizeof(double);
            var pos = offset;
            for (uint i = 0; i < numPoints && ((nodeHasZ && !state.HasZ) || (nodeHasM && !state.HasM)); i++)
            {
                if (nodeHasZ && !state.HasZ)
                {
                    var z = ReadDouble(wkb.Slice(pos + zOffset, sizeof(double)), littleEndian);
                    if (!double.IsNaN(z))
                    {
                        state.HasZ = true;
                    }
                }

                if (nodeHasM && !state.HasM)
                {
                    var m = ReadDouble(wkb.Slice(pos + mOffset, sizeof(double)), littleEndian);
                    if (!double.IsNaN(m))
                    {
                        state.HasM = true;
                    }
                }

                pos += stride;
            }
        }

        offset += (int)byteCount;
        return true;
    }

    private static string ResolveGeometryTypeName(uint baseType) => baseType switch
    {
        1 => "Point",
        2 => "LineString",
        3 => "Polygon",
        4 => "MultiPoint",
        5 => "MultiLineString",
        6 => "MultiPolygon",
        7 => "GeometryCollection",
        _ => string.Empty
    };

    private static bool TryReadUInt32(ReadOnlySpan<byte> wkb, ref int offset, bool littleEndian, out uint value)
    {
        if (wkb.Length - offset < sizeof(uint))
        {
            value = 0;
            return false;
        }

        var slice = wkb.Slice(offset, sizeof(uint));
        value = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
            : BinaryPrimitives.ReadUInt32BigEndian(slice);
        offset += sizeof(uint);
        return true;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> wkb, ref int offset, bool littleEndian, out int value)
    {
        if (wkb.Length - offset < sizeof(int))
        {
            value = 0;
            return false;
        }

        var slice = wkb.Slice(offset, sizeof(int));
        value = littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(slice)
            : BinaryPrimitives.ReadInt32BigEndian(slice);
        offset += sizeof(int);
        return true;
    }

    private static double ReadDouble(ReadOnlySpan<byte> slice, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadDoubleLittleEndian(slice)
            : BinaryPrimitives.ReadDoubleBigEndian(slice);

    #endregion

    #region Logging

    [LoggerMessage(EventId = 2501, Level = LogLevel.Warning, Message = "Topology validation failed")]
    private static partial void LogTopologyValidationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2502, Level = LogLevel.Debug, Message = "Geometry repaired: {OriginalReason}")]
    private static partial void LogGeometryRepaired(ILogger logger, string? originalReason);

    [LoggerMessage(EventId = 2503, Level = LogLevel.Warning, Message = "Geometry repair failed")]
    private static partial void LogGeometryRepairFailed(ILogger logger, Exception exception);

    #endregion
}

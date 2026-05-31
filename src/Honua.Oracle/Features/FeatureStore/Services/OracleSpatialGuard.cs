// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Oracle.Features.FeatureStore.Services;

/// <summary>
/// Detects whether an Oracle table backs a standard <c>SDO_GEOMETRY</c> spatial source or an
/// ArcSDE-proprietary surface (<c>ST_Geometry</c>/binary, versioned enterprise-geodatabase).
/// </summary>
/// <remarks>
/// <para>The guard is the IP / clean-room enforcement point: ArcSDE formats must never have
/// their bytes decoded by this provider. It probes Oracle's <c>ALL_TAB_COLUMNS</c> catalog
/// once per binding and caches the result for subsequent reads.</para>
/// <para>The cache key is the storage layer identifier; this is safe because schema metadata
/// is stable for the lifetime of a deployment. There is no TTL.</para>
/// </remarks>
internal sealed class OracleSpatialGuard
{
    private const string SdoGeometryDataType = "SDO_GEOMETRY";

    /// <summary>
    /// Known ArcSDE enterprise-geodatabase versioning columns that mark a table as versioned and
    /// therefore out of scope for read-only standard-spatial serving.
    /// </summary>
    public static readonly IReadOnlyList<string> ArcSdeVersioningColumns =
    [
        "GDB_FROM_DATE",
        "GDB_TO_DATE",
        "SDE_STATE_ID"
    ];

    private readonly IOracleSpatialMetadataProbe _probe;
    private readonly ILogger<OracleSpatialGuard> _logger;
    private readonly ConcurrentDictionary<int, Task<GuardResult>> _cache = new();

    public OracleSpatialGuard(IOracleSpatialMetadataProbe probe, ILogger<OracleSpatialGuard> logger)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures the table identified by <paramref name="mapping"/> is a standard <c>SDO_GEOMETRY</c>
    /// surface and not an ArcSDE-versioned source. Throws <see cref="NotSupportedException"/>
    /// before any geometry bytes are read when either check fails.
    /// </summary>
    public async Task EnsureStandardSpatialAsync(
        OracleLayerMapping mapping,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var resultTask = _cache.GetOrAdd(
            mapping.LayerId,
            static (_, state) => state.Self.ProbeAsync(state.Mapping, state.DataConnection, state.Token),
            (Self: this, Mapping: mapping, DataConnection: dataConnection, Token: cancellationToken));

        GuardResult result;
        try
        {
            result = await resultTask.ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(mapping.LayerId, out _);
            throw;
        }

        if (!result.IsSdoGeometry)
        {
            OracleFeatureLog.NonSdoSurfaceRejected(
                _logger,
                mapping.LayerId,
                mapping.GeometryColumn ?? "<none>",
                FormatTable(mapping),
                result.DataType ?? "<unknown>");
            throw new NotSupportedException(
                $"Oracle column '{mapping.GeometryColumn}' on '{FormatTable(mapping)}' is typed '{result.DataType}', not SDO_GEOMETRY. " +
                "ArcSDE ST_Geometry and binary formats are not supported.");
        }

        if (result.VersioningColumns.Count > 0)
        {
            var joined = string.Join(", ", result.VersioningColumns);
            OracleFeatureLog.VersionedTableRejected(_logger, mapping.LayerId, FormatTable(mapping), joined);
            throw new NotSupportedException(
                $"Oracle table '{FormatTable(mapping)}' contains ArcSDE versioning columns ({joined}). " +
                "Versioned enterprise-geodatabase tables are not supported.");
        }
    }

    private async Task<GuardResult> ProbeAsync(
        OracleLayerMapping mapping,
        DataConnection? dataConnection,
        CancellationToken cancellationToken)
    {
        var dataType = await _probe.GetGeometryColumnTypeAsync(mapping, dataConnection, cancellationToken).ConfigureAwait(false);
        var versioning = await _probe.GetArcSdeVersioningColumnsAsync(mapping, dataConnection, cancellationToken).ConfigureAwait(false);

        var isSdo = string.Equals(dataType, SdoGeometryDataType, StringComparison.OrdinalIgnoreCase);
        return new GuardResult(isSdo, dataType, versioning);
    }

    private static string FormatTable(OracleLayerMapping mapping)
        => string.IsNullOrWhiteSpace(mapping.SchemaName)
            ? mapping.TableName
            : mapping.SchemaName + "." + mapping.TableName;

    private sealed record GuardResult(bool IsSdoGeometry, string? DataType, IReadOnlyList<string> VersioningColumns);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Diagnostics;
using System.Text.Json;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Npgsql;
using NpgsqlTypes;
using Honua.Core.Configuration;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

using Honua.Postgres.Features.Infrastructure;
namespace Honua.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    /// <summary>
    /// Insert a batch of features with optional transaction.
    /// </summary>
    private async Task<(int imported, int failed)> InsertBatchAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        List<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        ImportLoadMode loadMode,
        IReadOnlyList<string> keyColumns,
        GeometryRepairTally repairTally,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var failed = 0;

        using var activity = _activitySource.StartActivity("Import.InsertBatch");
        activity?.SetTag("import.table", tableName);
        activity?.SetTag("import.feature_count", features.Count);
        activity?.SetTag("import.load_mode", loadMode.ToString());

        // Continue-on-error can't run inside a single transaction because any statement error aborts it.
        var useTransaction = _limits.UseTransactions && !_limits.ContinueOnError;
        await using var transaction = useTransaction
            ? await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            : null;

        try
        {
            try
            {
                imported = await InsertBatchFastAsync(
                    connection,
                    transaction,
                    schemaName,
                    tableName,
                    features,
                    sourceSrid,
                    targetSrid,
                    wkbWriter,
                    loadMode,
                    keyColumns,
                    repairTally,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (_limits.ContinueOnError)
            {
                (imported, failed) = await InsertBatchIndividuallyAsync(
                    connection,
                    transaction,
                    schemaName,
                    tableName,
                    features,
                    sourceSrid,
                    targetSrid,
                    wkbWriter,
                    loadMode,
                    keyColumns,
                    repairTally,
                    cancellationToken);
            }

            if (transaction != null)
            {
                await transaction.CommitSafelyAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (transaction != null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            throw;
        }

        activity?.SetTag("import.imported_count", imported);
        activity?.SetTag("import.failed_count", failed);

        return (imported, failed);
    }

    private async Task<int> InsertBatchFastAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        string tableName,
        List<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        ImportLoadMode loadMode,
        IReadOnlyList<string> keyColumns,
        GeometryRepairTally repairTally,
        CancellationToken cancellationToken)
    {
        var wkbs = new List<byte[]?>(features.Count);
        var sourceSrids = new List<int>(features.Count);
        var properties = new List<string>(features.Count);

        for (var i = 0; i < features.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = features[i];
            var gate = CreateWkb(feature, wkbWriter, repairTally);

            // A gate skip must exclude the whole row, not degrade it: a null WKB is a legal
            // null-geometry row, so passing the skipped feature through would silently import
            // its properties without geometry instead of skipping the feature.
            if (gate.Skipped)
            {
                continue;
            }

            wkbs.Add(gate.Wkb);

            // Use per-feature SRID when available (e.g. multi-layer FileGDBs
            // where each layer may have its own CRS).
            var featureSrid = feature.Geometry?.SRID;
            sourceSrids.Add(featureSrid is > 0 ? featureSrid.Value : sourceSrid);
            properties.Add(BuildPropertiesJson(feature));
        }

        if (wkbs.Count == 0)
        {
            // Every feature in the batch was skipped by the geometry gate.
            return 0;
        }

        // Honor the auditable Esri-default datum pipeline for the request-level
        // (sourceSrid -> targetSrid) pair (#1501). The pipeline is applied only to rows
        // whose source SRID matches the resolved pair; rows carrying a different per-feature
        // SRID (e.g. mixed-CRS FileGDB layers) keep PROJ's default pipeline via a NULL.
        var datumPipeline = ResolveImportDatumPipeline(sourceSrid, targetSrid);

        // Keyed upsert routes the whole batch through a single unnest-driven
        // INSERT ... ON CONFLICT DO UPDATE (honua.bulk_upsert_import_features) so colliding
        // rows merge in place without dropping the target. It honors the same datum pipeline
        // CASE as the insert path via the optional datum_source_srid/datum_pipeline params.
        if (loadMode == ImportLoadMode.Upsert)
        {
            return await UpsertBatchFastAsync(
                connection,
                transaction,
                schemaName,
                tableName,
                wkbs,
                sourceSrids,
                properties,
                sourceSrid,
                targetSrid,
                datumPipeline,
                keyColumns,
                cancellationToken);
        }

        var sql = datumPipeline is null
            ? """
                SELECT honua.insert_import_feature(
                    @schema_name,
                    @table_name,
                    payload.wkb,
                    payload.source_srid,
                    @target_srid,
                    payload.properties)
                FROM unnest(@wkbs, @source_srids, @properties) AS payload(wkb, source_srid, properties)
                """
            : """
                SELECT honua.insert_import_feature(
                    @schema_name,
                    @table_name,
                    payload.wkb,
                    payload.source_srid,
                    @target_srid,
                    payload.properties,
                    CASE WHEN payload.source_srid = @datum_source_srid THEN @datum_pipeline ELSE NULL END)
                FROM unnest(@wkbs, @source_srids, @properties) AS payload(wkb, source_srid, properties)
                """;

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        command.Parameters.Add("schema_name", NpgsqlDbType.Text).Value = schemaName;
        command.Parameters.Add("table_name", NpgsqlDbType.Text).Value = tableName;
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        command.Parameters.Add("wkbs", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = wkbs;
        command.Parameters.Add("source_srids", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = sourceSrids;
        command.Parameters.Add("properties", NpgsqlDbType.Array | NpgsqlDbType.Jsonb).Value = properties;
        if (datumPipeline is not null)
        {
            command.Parameters.Add("datum_source_srid", NpgsqlDbType.Integer).Value = sourceSrid;
            command.Parameters.Add("datum_pipeline", NpgsqlDbType.Text).Value = datumPipeline;
        }

        var imported = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            imported++;
        }

        return imported;
    }

    /// <summary>
    /// Keyed-upsert fast path: merge a whole batch through a single
    /// <c>honua.bulk_upsert_import_features</c> call (unnest + <c>ON CONFLICT DO UPDATE</c>)
    /// so colliding rows update in place and the rest insert, without dropping the target.
    /// Returns the number of rows processed by the merge.
    /// </summary>
    private static async Task<int> UpsertBatchFastAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        string tableName,
        List<byte[]?> wkbs,
        List<int> sourceSrids,
        List<string> properties,
        int sourceSrid,
        int targetSrid,
        string? datumPipeline,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(BulkUpsertImportFeaturesSql, connection)
        {
            Transaction = transaction
        };
        command.Parameters.Add("schema_name", NpgsqlDbType.Text).Value = schemaName;
        command.Parameters.Add("table_name", NpgsqlDbType.Text).Value = tableName;
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        command.Parameters.Add("wkbs", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = wkbs;
        command.Parameters.Add("source_srids", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = sourceSrids;
        command.Parameters.Add("properties", NpgsqlDbType.Array | NpgsqlDbType.Jsonb).Value = properties;
        command.Parameters.Add("key_columns", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = keyColumns.ToArray();
        command.Parameters.Add("datum_source_srid", NpgsqlDbType.Integer).Value =
            datumPipeline is null ? DBNull.Value : sourceSrid;
        command.Parameters.Add("datum_pipeline", NpgsqlDbType.Text).Value =
            datumPipeline ?? (object)DBNull.Value;

        var processed = await command.ExecuteScalarAsync(cancellationToken);
        return processed is int count ? count : wkbs.Count;
    }

    private async Task<(int imported, int failed)> InsertBatchIndividuallyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        string tableName,
        List<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        ImportLoadMode loadMode,
        IReadOnlyList<string> keyColumns,
        GeometryRepairTally repairTally,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var failed = 0;
        var isUpsert = loadMode == ImportLoadMode.Upsert;

        // Honor the auditable Esri-default datum pipeline for the request-level
        // (sourceSrid -> targetSrid) pair (#1501), applied per feature only when the
        // feature's source SRID matches the resolved pair. The single-row keyed upsert
        // path (this continue-on-error fallback) uses PROJ's default reprojection; the
        // datum-pipelined upsert is covered by the batch fast path.
        var datumPipeline = isUpsert ? null : ResolveImportDatumPipeline(sourceSrid, targetSrid);

        var commandText = isUpsert
            ? UpsertImportFeatureSql
            : (datumPipeline is null ? InsertImportFeatureSql : InsertImportFeatureWithDatumSql);

        await using var command = new NpgsqlCommand(commandText, connection)
        {
            Transaction = transaction
        };
        command.Parameters.Add("schema_name", NpgsqlDbType.Text).Value = schemaName;
        command.Parameters.Add("table_name", NpgsqlDbType.Text).Value = tableName;
        var wkbParameter = command.Parameters.Add("wkb", NpgsqlDbType.Bytea);
        var sourceSridParameter = command.Parameters.Add("source_srid", NpgsqlDbType.Integer);
        sourceSridParameter.Value = sourceSrid;
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        var propertiesParameter = command.Parameters.Add("properties", NpgsqlDbType.Jsonb);
        if (isUpsert)
        {
            command.Parameters.Add("key_columns", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = keyColumns.ToArray();
        }
        var datumPipelineParameter = datumPipeline is null
            ? null
            : command.Parameters.Add("datum_pipeline", NpgsqlDbType.Text);

        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var gate = CreateWkb(feature, wkbWriter, repairTally);

                // A gate skip excludes the whole row (see InsertBatchFastAsync): inserting the
                // feature with a NULL wkb would import its properties as a null-geometry row.
                if (gate.Skipped)
                {
                    continue;
                }

                wkbParameter.Value = gate.Wkb ?? (object)DBNull.Value;
                var featureSrid = feature.Geometry?.SRID;
                var effectiveSourceSrid = featureSrid is > 0 ? featureSrid.Value : sourceSrid;
                sourceSridParameter.Value = effectiveSourceSrid;
                if (datumPipelineParameter is not null)
                {
                    datumPipelineParameter.Value = effectiveSourceSrid == sourceSrid
                        ? datumPipeline!
                        : (object)DBNull.Value;
                }
                propertiesParameter.Value = BuildPropertiesJson(feature);
                await command.ExecuteNonQueryAsync(cancellationToken);
                imported++;
            }
            catch (Exception ex)
            {
                ImportLog.FeatureInsertFailed(_logger, ex, tableName);
                failed++;
                if (!_limits.ContinueOnError)
                {
                    throw;
                }
            }
        }

        return (imported, failed);
    }

    /// <summary>
    /// Build a JSON string of feature properties for import.
    /// </summary>
    private static string BuildPropertiesJson(IFeature feature)
    {
        if (feature.Attributes is null)
        {
            return "{}";
        }

        var names = feature.Attributes.GetNames();
        if (names.Length == 0)
        {
            return "{}";
        }

        var values = feature.Attributes.GetValues();
        var properties = new Dictionary<string, object?>(names.Length, StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i++)
        {
            properties[names[i]] = values[i];
        }

        return JsonSerializer.Serialize(properties, ImportJsonContext.Default.DictionaryStringObject);
    }

    /// <summary>
    /// Create WKB for a feature geometry, enforcing configured validation limits and the
    /// shared import validity gate (<c>ImportLimits.GeometryValidityMode</c>). This is
    /// the single choke point every import reader (GeoJSON, shapefile, GeoPackage, CSV-WKT,
    /// FlatGeobuf, GDAL, …) flows through, so applying the gate here guarantees consistent
    /// repair/strict/accept behavior instead of a per-reader patchwork (#2743). Repairs and
    /// skips are recorded in <paramref name="repairTally"/> for the import report.
    /// A <see cref="WkbGateResult.Skipped"/> result means the feature must be excluded from
    /// the insert entirely; a null <see cref="WkbGateResult.Wkb"/> without the skip flag is a
    /// legitimate null-geometry row whose properties still import.
    /// </summary>
    private WkbGateResult CreateWkb(IFeature feature, WKBWriter wkbWriter, GeometryRepairTally repairTally)
    {
        if (feature.Geometry == null)
        {
            return new WkbGateResult(null, false);
        }

        var geometry = feature.Geometry;

        // Hard memory guard (#1626): reject oversized geometries on vertex/ring count BEFORE
        // materializing coordinate arrays or writing WKB. A single island-scale multipolygon can
        // carry millions of vertices; copying its coordinates and serializing its WKB can allocate
        // hundreds of MB and OOM-crash a memory-constrained serverless host. This guard runs
        // regardless of the optional ValidateGeometry pass so the ceiling always holds, and surfaces
        // a clear, machine-readable error (413-style) rather than crashing.
        var sizeResult = ImportGeometrySizeGuard.Check(geometry, _limits.MaxVertices, _limits.MaxRings);
        if (!sizeResult.IsWithinLimits)
        {
            if (_limits.SkipInvalidGeometry)
            {
                ImportLog.GeometryTooLargeSkipped(_logger, sizeResult.Message ?? "Geometry exceeds import size limit.");
                return WkbGateResult.Skip(repairTally);
            }

            throw new ImportGeometryTooLargeException(sizeResult.Message ?? "Geometry exceeds import size limit.");
        }

        if (_limits.ValidateGeometry)
        {
            var validationError = ValidateGeometry(geometry);
            if (validationError != null)
            {
                if (_limits.SkipInvalidGeometry)
                {
                    return WkbGateResult.Skip(repairTally);
                }

                throw new InvalidOperationException($"Geometry validation failed: {validationError}");
            }
        }

        // Shared topology validity gate (#2743): mirrors the edit paths' GeometryValidationOptions
        // (Accept/Strict/Repair). Repair uses the managed NetTopologySuite GeometryFixer, which is
        // also what the geometry.make-valid GP executor uses. This is NOT identical to the edit
        // path: the edit path repairs via PostGIS ST_MakeValid (GEOS), whereas import repairs via
        // the GEOS-free NTS GeometryFixer — different engines, so a pathological geometry can yield
        // different repaired output on the two paths. Fixing here still prevents a self-intersecting
        // polygon from being stored to later blow up overlay queries with a GEOS TopologyException.
        //
        // Only areal geometry (Polygon/MultiPolygon and nested collections) can be topologically
        // invalid, so points/lines skip both the coordinate walk and the expensive IsValid
        // topology-graph build. Finiteness is required before IsValid: the earlier ValidateGeometry
        // pass already guarantees it when enabled, otherwise walk the coordinates once here. NaN/
        // Infinity ordinates cannot be validated or repaired meaningfully and are left to PostGIS.
        if (_limits.GeometryValidityMode != ValidationMode.Accept
            && geometry.OgcGeometryType is NetTopologySuite.Geometries.OgcGeometryType.Polygon
                or NetTopologySuite.Geometries.OgcGeometryType.MultiPolygon
                or NetTopologySuite.Geometries.OgcGeometryType.GeometryCollection)
        {
            var coordinatesFinite = _limits.ValidateGeometry || ValidateCoordinates(geometry);
            if (coordinatesFinite && !geometry.IsValid)
            {
                if (_limits.GeometryValidityMode == ValidationMode.Strict)
                {
                    if (_limits.SkipInvalidGeometry)
                    {
                        ImportLog.GeometryInvalidSkipped(_logger);
                        return WkbGateResult.Skip(repairTally);
                    }

                    throw new InvalidOperationException(
                        "Geometry validation failed: geometry topology is invalid (self-intersection, ring orientation, or hole placement).");
                }

                // ValidationMode.Repair
                NtsGeometry? repaired;
                try
                {
                    repaired = GeometryFixer.Fix(geometry);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_limits.SkipInvalidGeometry)
                    {
                        ImportLog.GeometryRepairFailedSkipped(_logger, ex);
                        return WkbGateResult.Skip(repairTally);
                    }

                    throw new InvalidOperationException("Geometry repair failed.", ex);
                }

                if (repaired == null || repaired.IsEmpty)
                {
                    if (_limits.SkipInvalidGeometry)
                    {
                        ImportLog.GeometryRepairEmptySkipped(_logger);
                        return WkbGateResult.Skip(repairTally);
                    }

                    throw new InvalidOperationException("Geometry repair produced an empty geometry.");
                }

                // Re-validate the repaired geometry: for pathological input GeometryFixer can return
                // a result that is still invalid. Treat that like the Strict/skip branch rather than
                // counting it as a successful repair and storing a still-broken geometry.
                if (!repaired.IsValid)
                {
                    if (_limits.SkipInvalidGeometry)
                    {
                        ImportLog.GeometryInvalidSkipped(_logger);
                        return WkbGateResult.Skip(repairTally);
                    }

                    throw new InvalidOperationException(
                        "Geometry repair did not produce a valid geometry.");
                }

                repaired.SRID = geometry.SRID;

                // Re-run the hard size guard on the repaired geometry BEFORE counting the repair:
                // make-valid can add vertices/rings (e.g. splitting a self-intersection into
                // multiple rings), so a repaired geometry could exceed MaxVertices/MaxRings even
                // though the pre-repair input passed the same guard.
                var repairedSizeResult = ImportGeometrySizeGuard.Check(
                    repaired, _limits.MaxVertices, _limits.MaxRings);
                if (!repairedSizeResult.IsWithinLimits)
                {
                    if (_limits.SkipInvalidGeometry)
                    {
                        ImportLog.GeometryTooLargeSkipped(
                            _logger,
                            repairedSizeResult.Message ?? "Repaired geometry exceeds import size limit.");
                        return WkbGateResult.Skip(repairTally);
                    }

                    throw new ImportGeometryTooLargeException(
                        repairedSizeResult.Message ?? "Repaired geometry exceeds import size limit.");
                }

                geometry = repaired;
                repairTally.Repaired++;
            }
        }

        var writer = SelectWkbWriter(geometry, wkbWriter);
        var wkb = writer.Write(geometry);

        if (wkb.Length > _limits.MaxWkbSize)
        {
            // The serialized WKB exceeded the per-geometry byte ceiling. This is also enforced
            // independently of ValidateGeometry so a degenerate geometry (few vertices, huge
            // serialized size) cannot blow the memory budget.
            if (_limits.SkipInvalidGeometry)
            {
                ImportLog.GeometryTooLargeSkipped(
                    _logger,
                    $"Geometry WKB size ({wkb.Length:N0} bytes) exceeds maximum allowed ({_limits.MaxWkbSize:N0} bytes).");
                return WkbGateResult.Skip(repairTally);
            }

            throw new ImportGeometryTooLargeException(
                $"Geometry WKB size ({wkb.Length:N0} bytes) exceeds maximum allowed ({_limits.MaxWkbSize:N0} bytes). "
                + "Explode multipart features or simplify the geometry before importing.");
        }

        return new WkbGateResult(wkb, false);
    }

    /// <summary>
    /// Picks the WKB writer dimensionality from the actual source geometry so XYZ,
    /// XYM, and XYZM features round-trip their Z/M ordinates instead of being
    /// silently flattened (#1981). 2-D geometries reuse the shared plain writer:
    /// forcing emitZ/emitM on plain XY coordinates serializes NaN Z/M ordinates
    /// that PostGIS rejects, dropping otherwise-valid rows. Each higher-dimension
    /// writer is allocated per call because <see cref="WKBWriter"/> is not
    /// thread-safe and is shared across concurrent batch workers.
    /// </summary>
    private static WKBWriter SelectWkbWriter(
        NetTopologySuite.Geometries.Geometry geometry,
        WKBWriter plainWriter)
    {
        var (hasZ, hasM) = DetectZm(geometry);
        if (!hasZ && !hasM)
        {
            return plainWriter;
        }

        return new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
    }

    /// <summary>
    /// Mutable per-import counter of geometries repaired by the shared validity gate.
    /// Accumulated across all batches of a single import (streaming is sequential within an
    /// import) and surfaced in the import report / progress so repair is never silent.
    /// </summary>
    private sealed class GeometryRepairTally
    {
        public int Repaired { get; set; }

        /// <summary>
        /// Features excluded from the insert entirely because the geometry gate rejected their
        /// geometry while <c>ImportLimits.SkipInvalidGeometry</c> was enabled (invalid
        /// topology under Strict, failed/empty repair, or an exceeded size limit).
        /// </summary>
        public int SkippedInvalid { get; set; }
    }

    /// <summary>
    /// Outcome of the geometry gate for one feature: the serialized WKB (null for a legitimate
    /// null-geometry row) plus a flag telling the caller to exclude the feature from the insert.
    /// The flag is required because a null WKB alone is indistinguishable from a null-geometry
    /// row, which imports its properties.
    /// </summary>
    private readonly record struct WkbGateResult(byte[]? Wkb, bool Skipped)
    {
        public static WkbGateResult Skip(GeometryRepairTally repairTally)
        {
            repairTally.SkippedInvalid++;
            return new WkbGateResult(null, true);
        }
    }
}

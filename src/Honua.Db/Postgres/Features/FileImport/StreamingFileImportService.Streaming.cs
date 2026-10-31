// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Shared.Models;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using FileGdb = Honua.Core.Features.FileImport.Services.FileGdb;
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
    /// <summary>
    /// Stream features from source and insert into database in batches.
    /// </summary>
    private async Task<(int imported, int failed, int repaired, string[] warnings, IReadOnlyList<ImportValidationIssue> rowIssues, string physicalTableName, bool replacementBlocked, IReadOnlyList<ImportTypedColumn> typedColumns)> ImportStreamingAsync(
        ImportRequest request,
        Stream fileStream,
        SupportedFileFormat format,
        int sourceSrid,
        string[] warnings,
        IProgress<ImportProgress>? progress,
        string jobId,
        CancellationToken cancellationToken,
        ShapefileScratch? shapefileScratch = null,
        FileGdbScratch? fileGdbScratch = null,
        double? coordinateEpoch = null)
    {
        await using var lease = await OpenConnectionAsync(cancellationToken);
        // The whole import holds the replace session lock below. On Lambda it runs on an unpooled
        // connection, so closing it ends the RDS Proxy-pinned session (honua-server#6765).
        await using var session = await _sessionLockConnections
            .OpenAsync(lease, cancellationToken, hasCallerTransaction: lease.Transaction is not null)
            .ConfigureAwait(false);
        var connection = session.Connection;

        // Validate and prepare table
        var targetSchema = ResolveTargetSchema(request.TargetSchema);
        var allowedTableName = await ResolvePhysicalTableNameAsync(
            connection, targetSchema, request.TableName, cancellationToken);
        var loadMode = request.EffectiveLoadMode;

        // Every load mode shares one staging name per logical target. Hold the historical
        // replace advisory lock for append and upsert too, so a concurrent replace cannot drop
        // an in-flight append's staging table or its live rows (#4003, SRV-IMP-008).
        ImportedDatasetSpatialMetadata.SessionLock? replaceLock = null;
        var dataCommitted = false;
        var appendUsesStaging = false;
        try
        {
            replaceLock = await ImportedDatasetSpatialMetadata.AcquireSessionLockAsync(
                connection,
                targetSchema,
                allowedTableName,
                cancellationToken);

            var target = await PrepareImportTargetAsync(
                connection, request, targetSchema, allowedTableName, coordinateEpoch, cancellationToken);
            var loadTableName = target.LoadTableName;
            var hadExistingTarget = target.HadExistingTarget;
            appendUsesStaging = target.AppendUsesStaging;
            var directGuard = loadMode == ImportLoadMode.Replace || appendUsesStaging
                ? null
                : new DirectImportGuard(target.LiveIdentity!.Value, coordinateEpoch);

            // A Replace load streams into a <table>__staging sibling (loadTableName) rather than
            // the live table. Before this try, the only path that ever reclaimed that sibling on
            // failure was the NEXT replace's CreateStagingTableAsync (DROP TABLE IF EXISTS ...
            // CREATE TABLE), so a thrown exception or a cancellation anywhere below left it and
            // its indexes behind indefinitely (#4422). DROP TABLE IF EXISTS makes the cleanup safe
            // to call even when the failure happened after a successful promotion (nothing left to
            // drop, since SwapStagingTableAsync already renamed it away) or before the staging
            // table was created (nothing ever existed).
            try
            {

                // 2-D default writer. CreateWkb upgrades to an emitZ and/or emitM writer per
                // geometry when the source geometry actually carries Z and/or M ordinates
                // (see SelectWkbWriter / DetectZm), so GPX/KML/3-D GeoJSON altitudes and
                // FileGDB/shapefile XYM/XYZM measures are preserved (#1981) without forcing
                // every 2-D coordinate through an emitZ/emitM writer (which serializes NaN
                // Z/M ordinates that PostGIS rejects, dropping otherwise-valid rows).
                var wkbWriter = new WKBWriter();
                var batch = new List<IFeature>(_limits.BatchSize);
                var typedColumnInference = new ImportTypedColumnInference(preserveStringScalars: format != SupportedFileFormat.Csv);
                var totalImported = 0;
                var totalFailed = 0;
                var batchesCommitted = 0;
                var repairTally = new GeometryRepairTally();
                var startTime = DateTimeOffset.UtcNow;

                // Stream features based on format
                if (format == SupportedFileFormat.Shapefile && shapefileScratch == null)
                {
                    throw new InvalidOperationException("Shapefile scratch directory was not prepared.");
                }

                // Collects CSV rows whose mapped geometry column held a value that could not be parsed
                // (e.g. malformed WKT). Surfaced as a completion warning so the loss is never silent.
                var csvGeometryDiagnostics = format == SupportedFileFormat.Csv ? new CsvGeometryDiagnostics() : null;

                // Collects WKT records that could not be parsed (bad WKT/EWKT, stray header lines, a
                // truncated multi-line tail). Surfaced as a completion warning so the loss is never silent.
                var wktGeometryDiagnostics = format == SupportedFileFormat.Wkt ? new WktGeometryDiagnostics() : null;

                var featureStream = format switch
                {
                    SupportedFileFormat.GeoJson => _geoJsonReader.ReadFeaturesAsync(fileStream, cancellationToken),
                    SupportedFileFormat.EsriJson => EsriJsonFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                    SupportedFileFormat.Wkb => WkbFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                    SupportedFileFormat.Wkt => WktFormatReader.ReadStreamingAsync(fileStream, wktGeometryDiagnostics, cancellationToken),
                    SupportedFileFormat.Kml => KmlFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                    SupportedFileFormat.Gpx => GpxFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                    SupportedFileFormat.Gml => GmlFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                    SupportedFileFormat.Csv => CsvFormatReader.ReadStreamingAsync(fileStream, delimiterOverride: null, csvGeometryDiagnostics, request.CsvOptions, cancellationToken),
                    SupportedFileFormat.FlatGeobuf => FlatGeobufFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                    SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(shapefileScratch!.ShpPath, cancellationToken),
                    SupportedFileFormat.GeoPackage => ReadGeoPackageStreamingAsync(fileStream, cancellationToken),
                    SupportedFileFormat.FileGdb => FileGdb.FileGdbReader.ReadStreamingAsync(fileGdbScratch!.GdbPath, cancellationToken),
                    SupportedFileFormat.GeoParquet => GeoParquetReader.ReadStreamingAsync(fileStream, _limits, cancellationToken),
                    _ => throw new NotSupportedException($"Streaming not supported for format: {format}")
                };

                await foreach (var feature in featureStream.WithCancellation(cancellationToken))
                {
                    // FileGDB included: the resolved/explicit source SRID must win over whatever the
                    // reader's geometry factory carried (#6114). CSV is the exception: an EWKT/EWKB
                    // value already carries its own SRID, and stamping the 4326 default here stored
                    // projected coordinates as degrees (#6171). An explicit SourceSrid still wins.
                    if (feature.Geometry != null &&
                        ShouldApplyDetectedSourceSrid(format, request.SourceSrid, feature.Geometry.SRID))
                    {
                        feature.Geometry.SRID = sourceSrid;
                    }

                    batch.Add(feature);
                    ObserveTypedColumns(typedColumnInference, feature);

                    // Process batch when full
                    if (batch.Count >= _limits.BatchSize)
                    {
                        var (imported, failed) = await InsertBatchAsync(
                            connection,
                            targetSchema,
                            loadTableName,
                            batch,
                            sourceSrid,
                            request.TargetSrid,
                            wkbWriter,
                            loadMode,
                            request.KeyColumns,
                            repairTally,
                            cancellationToken,
                            directGuard);

                        totalImported += imported;
                        totalFailed += failed;
                        batchesCommitted++;
                        batch.Clear();

                        // Report progress
                        progress?.Report(new ImportProgress
                        {
                            JobId = jobId,
                            Status = ImportStatus.Processing,
                            FeaturesProcessed = totalImported,
                            FailedFeatures = totalFailed + repairTally.SkippedInvalid +
                                (wktGeometryDiagnostics?.UnparseableRecords ?? 0),
                            RepairedFeatures = repairTally.Repaired,
                            BatchesCommitted = batchesCommitted,
                            TableName = request.TableName,
                            FileName = request.FileName,
                            SourceKind = request.SourceKind,
                            SourceUrl = request.SourceUrl,
                            CloudFileId = request.CloudFileId,
                            UploadId = request.UploadId,
                            Format = format,
                            StartedAt = startTime,
                            BytesRead = fileStream.CanSeek ? fileStream.Position : 0,
                            TotalBytes = fileStream.CanSeek ? fileStream.Length : null,
                            Warnings = warnings,
                            CurrentPhase = "Importing features"
                        });

                        // Yield control to prevent blocking
                        await Task.Yield();
                    }
                }

                // Process remaining features
                if (batch.Count > 0)
                {
                    var (imported, failed) = await InsertBatchAsync(
                        connection,
                        targetSchema,
                        loadTableName,
                        batch,
                        sourceSrid,
                        request.TargetSrid,
                        wkbWriter,
                        loadMode,
                        request.KeyColumns,
                        repairTally,
                        cancellationToken,
                        directGuard);

                    totalImported += imported;
                    totalFailed += failed;
                    batchesCommitted++;
                }

                // Roll geometry-gate skips into the failure tally before the replace path decides
                // whether to promote: a skip excludes the feature entirely rather than reporting a
                // batch insert failure (see InsertBatchFastAsync/InsertBatchIndividuallyAsync), so
                // totalFailed would otherwise read zero here even though input rows were dropped.
                totalFailed += repairTally.SkippedInvalid;

                // Parser skips never enter a batch, so count them before deciding whether
                // the staging dataset is complete enough to replace an existing target.
                totalFailed += wktGeometryDiagnostics?.UnparseableRecords ?? 0;

                // For a replace, the load streamed into the staging sibling. Promote it over the
                // live target unless doing so would destroy a prior COMPLETE dataset: when a target
                // already existed and this load dropped rows (skip/continue), promoting the
                // incomplete staging sibling would silently replace a complete dataset with a
                // partial one (#4006). A first-ever replace into a brand-new target has nothing to
                // protect, so it still promotes imported rows even with dropped rows — that is a
                // normal partial import, not data loss. A load with no imported rows is discarded.
                // Append into an owned target uses the same sibling and merges only after the
                // whole file has loaded, so a failure before that merge leaves the live table as it was.
                var replacementBlocked = loadMode == ImportLoadMode.Replace && hadExistingTarget && totalFailed > 0;
                var stagesIntoSibling = loadMode == ImportLoadMode.Replace || appendUsesStaging;
                IReadOnlyList<ImportTypedColumn> typedColumns = [];
                if (stagesIntoSibling)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (loadMode == ImportLoadMode.Replace)
                {
                    if (replacementBlocked || totalImported == 0)
                    {
                        await DropStagingTableAsync(connection, targetSchema, allowedTableName, CancellationToken.None);
                    }
                    else
                    {
                        // Finish schema promotion before the swap. If it fails, the existing
                        // live dataset remains intact and the staging table is discarded.
                        typedColumns = await PromoteTypedColumnsAsync(
                            connection, targetSchema, loadTableName,
                            typedColumnInference.Resolve(), cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        await SwapStagingTableAsync(
                            connection,
                            targetSchema,
                            allowedTableName,
                            totalImported,
                            request.CommitParticipant,
                            request.TargetSrid,
                            coordinateEpoch,
                            target.LiveIdentity,
                            CancellationToken.None);
                        dataCommitted = true;
                    }
                }
                else if (appendUsesStaging)
                {
                    if (totalImported == 0)
                    {
                        await DropStagingTableAsync(connection, targetSchema, allowedTableName, CancellationToken.None);
                    }
                    else
                    {
                        await AppendStagingIntoTargetAsync(
                            connection,
                            targetSchema,
                            allowedTableName,
                            loadTableName,
                            totalImported,
                            request.CommitParticipant,
                            request.TargetSrid,
                            coordinateEpoch,
                            target.LiveIdentity,
                            CancellationToken.None);
                        dataCommitted = true;
                    }
                }
                else if (totalImported > 0)
                {
                    // Direct append and upsert already wrote the live table. Later cancellation
                    // must not reclassify that write as if it had been rolled back.
                    dataCommitted = true;
                }

                var completionWarningsBuilder = new List<string>(warnings.Length + 2);
                completionWarningsBuilder.AddRange(warnings);

                if (csvGeometryDiagnostics is { UnparseableGeometryRows: > 0 })
                {
                    completionWarningsBuilder.Add(string.Format(
                        null, _csvUnparseableGeometryWarningFormat, csvGeometryDiagnostics.UnparseableGeometryRows));
                }

                if (wktGeometryDiagnostics is { UnparseableRecords: > 0 })
                {
                    completionWarningsBuilder.Add(string.Format(
                        null, _wktUnparseableGeometryWarningFormat, wktGeometryDiagnostics.UnparseableRecords));
                }

                // CSV address rows that could not be geocoded were imported without geometry; surface
                // a summary warning plus per-row issues so callers can report/repair individual rows.
                IReadOnlyList<ImportValidationIssue> rowIssues = [];
                if (csvGeometryDiagnostics is { GeocodeFailureCount: > 0 })
                {
                    completionWarningsBuilder.Add(string.Format(
                        null, _csvGeocodeFailureWarningFormat, csvGeometryDiagnostics.GeocodeFailureCount));
                    rowIssues = [.. csvGeometryDiagnostics.GeocodeFailures.Select(static failure =>
                ImportValidationIssue.Create(
                    ImportValidationErrorCodes.AddressGeocodeFailed,
                    failure.Address.Length == 0
                        ? "The address column was empty; the row was imported without geometry."
                        : $"Address '{failure.Address}' could not be geocoded; the row was imported without geometry.",
                    featureIndex: failure.RowNumber - 1))];
                }

                if (_limits.ContinueOnError && totalFailed > 0)
                {
                    completionWarningsBuilder.Add(string.Format(null, _partialImportWarningFormat, totalFailed));
                }

                // Surface per-row geometry repair accounting so the shared validity gate never silently
                // rewrites input geometry (#2743). Default gate mode is Repair, so previously-invalid
                // geometry is fixed (ST_MakeValid-equivalent) instead of stored as-is.
                if (repairTally.Repaired > 0)
                {
                    completionWarningsBuilder.Add(string.Format(null, _repairedGeometryWarningFormat, repairTally.Repaired));
                }

                // Features the geometry gate excluded entirely (SkipInvalidGeometry): already rolled
                // into totalFailed above (before the replace promotion decision); surface the loss
                // here so a skip is never silent.
                if (repairTally.SkippedInvalid > 0)
                {
                    completionWarningsBuilder.Add(string.Format(null, _skippedInvalidGeometryWarningFormat, repairTally.SkippedInvalid));
                }

                if (dataCommitted)
                {
                    if (loadMode != ImportLoadMode.Replace)
                    {
                        // Append and upsert write to the live table. Refresh their projections
                        // before reporting completion, even if cancellation arrived after commit.
                        typedColumns = await PromoteTypedColumnsAsync(
                            connection,
                            targetSchema,
                            allowedTableName,
                            typedColumnInference.Resolve(),
                            CancellationToken.None);
                    }

                    try
                    {
                        await AnalyzeTableAsync(connection, targetSchema, allowedTableName, CancellationToken.None);
                    }
                    catch (Exception analyzeException) when (analyzeException is not OutOfMemoryException)
                    {
                        // The rows are already committed. An ANALYZE failure must not turn that
                        // into a failed import or skip the completion record.
                        ImportLog.AnalyzeAfterCommitFailed(_logger, analyzeException, targetSchema, allowedTableName);
                        completionWarningsBuilder.Add(
                            "Table statistics could not be refreshed after the import was committed; the imported rows were kept.");
                    }
                }

                if (dataCommitted && cancellationToken.IsCancellationRequested)
                {
                    completionWarningsBuilder.Add(
                        "Import was cancelled after the data was committed. The imported rows were kept.");
                }

                string[] completionWarnings = [.. completionWarningsBuilder];

                // Report completion
                progress?.Report(new ImportProgress
                {
                    JobId = jobId,
                    Status = ImportStatus.Completed,
                    FeaturesProcessed = totalImported,
                    FailedFeatures = totalFailed,
                    RepairedFeatures = repairTally.Repaired,
                    BatchesCommitted = batchesCommitted,
                    TableName = request.TableName,
                    FileName = request.FileName,
                    SourceKind = request.SourceKind,
                    SourceUrl = request.SourceUrl,
                    CloudFileId = request.CloudFileId,
                    UploadId = request.UploadId,
                    Format = format,
                    StartedAt = startTime,
                    CompletedAt = DateTimeOffset.UtcNow,
                    BytesRead = fileStream.CanSeek ? fileStream.Position : 0,
                    TotalBytes = fileStream.CanSeek ? fileStream.Length : null,
                    Warnings = completionWarnings,
                    CurrentPhase = "Import completed"
                });

                return (totalImported, totalFailed, repairTally.Repaired, completionWarnings, rowIssues, allowedTableName, replacementBlocked, typedColumns);
            }
            catch when ((loadMode == ImportLoadMode.Replace || appendUsesStaging) && !dataCommitted)
            {
                try
                {
                    await DropStagingTableAsync(connection, targetSchema, allowedTableName, CancellationToken.None);
                }
                // codeql[cs/catch-of-all-exceptions]: cleanup failure must not mask the original replace error, which is rethrown below
                catch (Exception cleanupEx)
                {
                    ImportLog.StagingTableCleanupFailed(_logger, cleanupEx, targetSchema, allowedTableName);
                }

                throw;
            }
        }
        finally
        {
            if (replaceLock is not null)
            {
                await replaceLock.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Whether the resolved source SRID replaces the geometry's own SRID.
    /// An explicit request SRID always wins. A CSV geometry that already carries an SRID
    /// (EWKT/EWKB) keeps it so projected coordinates are not stored as EPSG:4326 (#6171).
    /// Every other geometry, including CSV values with no embedded SRID and FileGDB
    /// features left at SRID 0, takes the detected source SRID (#6114).
    /// </summary>
    internal static bool ShouldApplyDetectedSourceSrid(
        SupportedFileFormat format,
        int? requestedSourceSrid,
        int featureSrid)
    {
        if (requestedSourceSrid.HasValue || featureSrid <= 0)
        {
            return true;
        }

        return format != SupportedFileFormat.Csv;
    }

    private void RecordImportMetrics(
        string format,
        string mode,
        string status,
        TimeSpan duration,
        long? bytesRead,
        int importedCount,
        int failedCount)
    {
        var tags = new Dictionary<string, string>
        {
            { "format", format },
            { "mode", mode },
            { "status", status }
        };

        _performanceMonitor.RecordHistogram("honua_import_duration_ms", duration.TotalMilliseconds, tags);
        _performanceMonitor.RecordCounter("honua_import_total", 1, tags);
        _performanceMonitor.RecordHistogram("honua_import_features", importedCount, tags);

        if (bytesRead.HasValue)
        {
            _performanceMonitor.RecordHistogram("honua_import_bytes", bytesRead.Value, tags);
        }

        if (failedCount > 0)
        {
            _performanceMonitor.RecordCounter("honua_import_failures_total", failedCount, tags);
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    /// <summary>
    /// Stream features from source and insert into database in batches.
    /// </summary>
    private async Task<(int imported, int failed, string[] warnings)> ImportStreamingAsync(
        ImportRequest request,
        Stream fileStream,
        SupportedFileFormat format,
        int sourceSrid,
        string[] warnings,
        IProgress<ImportProgress>? progress,
        string jobId,
        CancellationToken cancellationToken,
        ShapefileScratch? shapefileScratch = null,
        FileGdbScratch? fileGdbScratch = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        // Validate and prepare table
        var allowedTableName = GetAllowedTableName(request.TableName);
        var targetSchema = ResolveTargetSchema(request.TargetSchema);
        var loadMode = request.EffectiveLoadMode;

        // The load mode determines which physical table the batches stream into and how
        // the target is prepared:
        //
        //   * Replace — load into a fresh, empty <table>__staging sibling, then atomically
        //     rename it over the live table after a fully successful load. This keeps replace
        //     transactional: a failed/cancelled load never leaves a half-dropped target, which
        //     the previous unconditional DROP+CREATE could not guarantee.
        //   * Append  — create the live table only if missing (never drop) and stream into it,
        //     so existing rows are retained.
        //   * Upsert  — create the live table if missing, ensure a unique key index over the
        //     requested property keys, and stream through honua.upsert_import_feature so
        //     colliding rows UPDATE in place and the rest insert, with no full drop.
        //
        // The batch load opens a RepeatableRead transaction whose snapshot is taken on the
        // first statement; preparing the table here (autocommit, on the SAME connection)
        // guarantees it is committed and visible before that snapshot.
        string loadTableName;
        switch (loadMode)
        {
            case ImportLoadMode.Append:
                await EnsureTableAsync(connection, targetSchema, allowedTableName, request.TargetSrid, cancellationToken);
                loadTableName = allowedTableName;
                break;
            case ImportLoadMode.Upsert:
                await EnsureTableAsync(connection, targetSchema, allowedTableName, request.TargetSrid, cancellationToken);
                await EnsureUpsertKeyAsync(connection, targetSchema, allowedTableName, request.KeyColumns, cancellationToken);
                loadTableName = allowedTableName;
                break;
            default:
                // Replace via transactional staging-table swap.
                loadTableName = await CreateStagingTableAsync(
                    connection, targetSchema, allowedTableName, request.TargetSrid, cancellationToken);
                break;
        }

        // 2-D default writer. CreateWkb upgrades to an emitZ and/or emitM writer per
        // geometry when the source geometry actually carries Z and/or M ordinates
        // (see SelectWkbWriter / DetectZm), so GPX/KML/3-D GeoJSON altitudes and
        // FileGDB/shapefile XYM/XYZM measures are preserved (#1981) without forcing
        // every 2-D coordinate through an emitZ/emitM writer (which serializes NaN
        // Z/M ordinates that PostGIS rejects, dropping otherwise-valid rows).
        var wkbWriter = new WKBWriter();
        var batch = new List<IFeature>(_limits.BatchSize);
        var totalImported = 0;
        var totalFailed = 0;
        var nullGeometrySkipped = 0;
        var batchesCommitted = 0;
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
            SupportedFileFormat.Csv => CsvFormatReader.ReadStreamingAsync(fileStream, delimiterOverride: null, csvGeometryDiagnostics, cancellationToken),
            SupportedFileFormat.FlatGeobuf => FlatGeobufFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(shapefileScratch!.ShpPath, cancellationToken),
            SupportedFileFormat.GeoPackage => ReadGeoPackageStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.FileGdb => FileGdb.FileGdbReader.ReadStreamingAsync(fileGdbScratch!.GdbPath, cancellationToken),
            SupportedFileFormat.GeoParquet => GeoParquetReader.ReadStreamingAsync(fileStream, cancellationToken),
            _ => throw new NotSupportedException($"Streaming not supported for format: {format}")
        };

        await foreach (var feature in featureStream.WithCancellation(cancellationToken))
        {
            // GeoParquet: skip rows with null geometry and count them as failures
            // per design decision "Null geometry rows | Skip, count" (ticket #423).
            if (format == SupportedFileFormat.GeoParquet && feature.Geometry == null)
            {
                totalFailed++;
                nullGeometrySkipped++;
                continue;
            }

            if (format != SupportedFileFormat.FileGdb && feature.Geometry != null)
            {
                feature.Geometry.SRID = sourceSrid;
            }

            batch.Add(feature);

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
                    cancellationToken);

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
                    FailedFeatures = totalFailed,
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
                cancellationToken);

            totalImported += imported;
            totalFailed += failed;
            batchesCommitted++;
        }

        // For a replace, the load streamed into the staging sibling; atomically rename it
        // over the live target now that every batch committed successfully. A failure or
        // cancellation before this point left the live table untouched.
        if (loadMode == ImportLoadMode.Replace)
        {
            await SwapStagingTableAsync(connection, targetSchema, allowedTableName, cancellationToken);
        }

        await AnalyzeTableAsync(connection, targetSchema, allowedTableName, cancellationToken);

        // Surface skipped null-geometry rows in the completion progress report
        // so background/queued imports expose the same warning as synchronous results.
        var completionWarningsBuilder = new List<string>(warnings.Length + 2);
        completionWarningsBuilder.AddRange(warnings);
        if (format == SupportedFileFormat.GeoParquet && nullGeometrySkipped > 0)
        {
            completionWarningsBuilder.Add(string.Format(null, _nullGeometryWarningFormat, nullGeometrySkipped));
        }

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

        if (_limits.ContinueOnError && totalFailed > 0)
        {
            completionWarningsBuilder.Add(string.Format(null, _partialImportWarningFormat, totalFailed));
        }

        string[] completionWarnings = [.. completionWarningsBuilder];

        // Report completion
        progress?.Report(new ImportProgress
        {
            JobId = jobId,
            Status = ImportStatus.Completed,
            FeaturesProcessed = totalImported,
            FailedFeatures = totalFailed,
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

        return (totalImported, totalFailed, completionWarnings);
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

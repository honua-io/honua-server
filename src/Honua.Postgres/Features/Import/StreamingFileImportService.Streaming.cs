// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Shared.Models;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using FileGdb = Honua.Core.Features.Import.Services.FileGdb;

namespace Honua.Postgres.Features.Import;

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

        if (request.OverwriteExisting)
        {
            await CreateTableAsync(connection, targetSchema, allowedTableName, request.TargetSrid, cancellationToken);
        }

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

        var featureStream = format switch
        {
            SupportedFileFormat.GeoJson => _geoJsonReader.ReadFeaturesAsync(fileStream, cancellationToken),
            SupportedFileFormat.Wkt => WktFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Kml => KmlFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Gpx => GpxFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Csv => CsvFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
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
                    allowedTableName,
                    batch,
                    sourceSrid,
                    request.TargetSrid,
                    wkbWriter,
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
                allowedTableName,
                batch,
                sourceSrid,
                request.TargetSrid,
                wkbWriter,
                cancellationToken);

            totalImported += imported;
            totalFailed += failed;
            batchesCommitted++;
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

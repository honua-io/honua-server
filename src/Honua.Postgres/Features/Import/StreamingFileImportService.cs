// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Data;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Shapefiles.Readers;
using Npgsql;
using NpgsqlTypes;
using FileGdb = Honua.Core.Features.Import.Services.FileGdb;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Memory-efficient streaming file import service.
/// Processes features incrementally using IAsyncEnumerable and batched database insertion
/// to maintain constant memory usage regardless of file size.
/// </summary>
internal sealed partial class StreamingFileImportService : IFileImportService
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsDetectionService _crsDetectionService;
    private readonly IFileFormatDetectionService _formatDetectionService;
    private readonly ImportLimits _limits;
    private readonly StreamingGeoJsonReader _geoJsonReader;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<StreamingFileImportService> _logger;
    private readonly Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage? _cloudStorage;

    private const string CreateImportTableSql = "SELECT honua.create_import_table(@table_name)";
    private const string InsertImportFeatureSql = "SELECT honua.insert_import_feature(@table_name, @wkb, @source_srid, @target_srid, @properties)";
    private const int CrsDetectionHeaderSize = 8192;
    private const long DefaultMaxArchiveEntryBytes = 500L * 1024 * 1024;
    private const long DefaultMaxArchiveExtractedBytes = 1024L * 1024 * 1024;
    private const double DefaultMaxArchiveCompressionRatio = 200d;

    private static readonly FrozenSet<string> _shapefileComponentExtensions = new[]
        {
            ".shp", ".dbf", ".shx", ".prj", ".cpg"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly string _shapefileScratchRoot = Path.Combine(Path.GetTempPath(), "honua-shapefile");
    private static readonly string _geoPackageScratchRoot = Path.Combine(Path.GetTempPath(), "honua-geopackage");
    private static readonly string _kmzScratchRoot = Path.Combine(Path.GetTempPath(), "honua-kmz");
    private static readonly string _fileGdbScratchRoot = Path.Combine(Path.GetTempPath(), "honua-filegdb");
    private static readonly string _geoParquetScratchRoot = Path.Combine(Path.GetTempPath(), "honua-geoparquet");
    private static readonly CompositeFormat _nullGeometryWarningFormat =
        CompositeFormat.Parse("{0} row(s) were skipped because geometry was null.");
    private static readonly CompositeFormat _partialImportWarningFormat =
        CompositeFormat.Parse("{0} row(s) failed while continue-on-error was enabled; previously imported rows were retained.");
    private static readonly Regex _wktSridRegex = new(
        @"SRID\s*=\s*(\d+)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]{0,128}$")]
    private static partial Regex GeoPackageTableNameRegex();

    public StreamingFileImportService(
        IDatabaseConnectionProvider connectionProvider,
        ICrsDetectionService crsDetectionService,
        IFileFormatDetectionService formatDetectionService,
        IPerformanceMonitor performanceMonitor,
        ILogger<StreamingFileImportService> logger,
        ImportLimits? limits = null,
        Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage? cloudStorage = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsDetectionService = crsDetectionService ?? throw new ArgumentNullException(nameof(crsDetectionService));
        _formatDetectionService = formatDetectionService ?? throw new ArgumentNullException(nameof(formatDetectionService));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limits = limits ?? ImportLimits.Default;
        _geoJsonReader = new StreamingGeoJsonReader(_limits);
        _cloudStorage = cloudStorage;
    }

    /// <inheritdoc/>
    public ImportLimits Limits => _limits;

    /// <inheritdoc/>
    public SupportedFileFormat? DetectFormat(string fileName) => _formatDetectionService.DetectFormat(fileName);

    /// <inheritdoc/>
    public string[] GetSupportedExtensions() => _formatDetectionService.GetSupportedExtensions();

    /// <inheritdoc/>
    public Task<ImportResult> ImportFileAsync(ImportRequest request, CancellationToken cancellationToken = default)
        => ImportFileAsync(request, null, cancellationToken);

    /// <inheritdoc/>
    public async Task<ImportResult> ImportFileAsync(
        ImportRequest request,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        // Validate request has exactly one source: FileStream, CloudFileId, or LocalFilePath
        request.Validate();

        var stopwatch = Stopwatch.StartNew();
        var format = DetectFormat(request.FileName);
        var formatName = format?.ToString() ?? "unknown";
        var mode = progress == null ? "sync" : "background";
        var jobId = Guid.NewGuid().ToString("N")[..8];
        string[] warnings = [];

        // Handle cloud storage file download if needed
        Stream fileStream;
        bool shouldDisposeStream = false;
        long? totalBytes = null;

        if (request.UsesCloudStorage)
        {
            if (_cloudStorage == null)
            {
                throw new InvalidOperationException("Cloud storage is not configured, but CloudFileId was provided.");
            }

            var metadata = await _cloudStorage.GetMetadataAsync(request.CloudFileId!, cancellationToken);
            var downloadStream = await _cloudStorage.DownloadAsync(request.CloudFileId!, cancellationToken);
            if (downloadStream == null)
            {
                var cloudErrorMessage = "Failed to download file from cloud storage.";
                return ImportResult.CreateFailure(
                    request.TableName,
                    format ?? SupportedFileFormat.GeoJson,
                    cloudErrorMessage,
                    stopwatch.Elapsed,
                    warnings);
            }

            fileStream = downloadStream;
            shouldDisposeStream = true;
            totalBytes = metadata?.SizeBytes;
        }
        else if (request.UsesLocalFile)
        {
            var fileInfo = new FileInfo(request.LocalFilePath!);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("Local staged import file was not found.", request.LocalFilePath);
            }

            fileStream = new FileStream(request.LocalFilePath!, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = _limits.StreamBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            shouldDisposeStream = true;
            totalBytes = fileInfo.Length;
        }
        else
        {
            fileStream = request.FileStream!;
            totalBytes = fileStream.CanSeek ? fileStream.Length : null;
        }

        long? bytesRead = null;
        var importedCount = 0;
        var failedCount = 0;
        var status = "failed";
        string? errorMessage = null;
        ImportResult result;
        ShapefileScratch? shapefileScratch = null;
        GeoPackageScratch? geoPackageScratch = null;
        KmzScratch? kmzScratch = null;
        FileGdbScratch? fileGdbScratch = null;
        GeoParquetScratch? geoParquetScratch = null;
        FileStream? fgbTempStream = null;

        ImportLog.ImportStarted(_logger, jobId, request.TableName, formatName, mode, totalBytes);

        try
        {
            if (format == null)
            {
                errorMessage = "Unsupported file format: " + Path.GetExtension(request.FileName);
                result = ImportResult.CreateFailure(
                    request.TableName,
                    SupportedFileFormat.GeoJson,
                    errorMessage,
                    stopwatch.Elapsed,
                    warnings);
                return result;
            }

            int? detectedSrid;
            if (format.Value == SupportedFileFormat.GeoPackage)
            {
                geoPackageScratch = await PrepareGeoPackageScratchAsync(fileStream, cancellationToken);
                var layers = await GetGeoPackageLayersAsync(geoPackageScratch.FilePath, cancellationToken);
                GeoPackageLayerInfo layer;
                try
                {
                    layer = ResolveSingleGeoPackageImportLayer(layers);
                }
                catch (InvalidDataException ex)
                {
                    errorMessage = ex.Message;
                    result = ImportResult.CreateFailure(
                        request.TableName,
                        format.Value,
                        errorMessage,
                        stopwatch.Elapsed,
                        warnings);
                    return result;
                }

                detectedSrid = layer.Srid;

                if (shouldDisposeStream)
                {
                    await fileStream.DisposeAsync();
                    shouldDisposeStream = false;
                }

                fileStream = new FileStream(geoPackageScratch.FilePath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = _limits.StreamBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                shouldDisposeStream = true;
                totalBytes = fileStream.Length;
            }
            else if (format.Value == SupportedFileFormat.Shapefile)
            {
                if (!IsZipFileName(request.FileName))
                {
                    errorMessage = "Shapefile imports require a .zip containing .shp and .dbf files.";
                    result = ImportResult.CreateFailure(
                        request.TableName,
                        format.Value,
                        errorMessage,
                        stopwatch.Elapsed,
                        warnings);
                    return result;
                }

                shapefileScratch = await PrepareShapefileScratchAsync(fileStream, request.FileName, cancellationToken);
                detectedSrid = await _crsDetectionService.DetectFromShapefilePrjAsync(shapefileScratch.ShpPath);
            }
            else if (format.Value == SupportedFileFormat.Kml && IsKmzFileName(request.FileName))
            {
                kmzScratch = await PrepareKmzScratchAsync(fileStream, cancellationToken);

                if (shouldDisposeStream)
                {
                    await fileStream.DisposeAsync();
                    shouldDisposeStream = false;
                }

                fileStream = new FileStream(kmzScratch.KmlPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = _limits.StreamBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                shouldDisposeStream = true;
                totalBytes = fileStream.Length;
                detectedSrid = request.SourceSrid ?? 4326;
            }
            else if (format.Value == SupportedFileFormat.FileGdb)
            {
                fileGdbScratch = await PrepareFileGdbScratchAsync(fileStream, cancellationToken);
                var layers = FileGdb.FileGdbReader.DiscoverLayers(fileGdbScratch.GdbPath);
                if (layers.Length > 1)
                {
                    errorMessage = FileGdb.FileGdbReader.BuildMultiLayerImportMessage(layers);
                    result = ImportResult.CreateFailure(
                        request.TableName,
                        format.Value,
                        errorMessage,
                        stopwatch.Elapsed,
                        warnings);
                    return result;
                }

                detectedSrid = layers.Length == 1 && layers[0].Srid > 0
                    ? layers[0].Srid
                    : FileGdb.FileGdbReader.DetectSrid(fileGdbScratch.GdbPath);
                warnings = FileGdb.FileGdbAdvancedConstructs.DetectWarnings(fileGdbScratch.GdbPath);
            }
            else if (format.Value == SupportedFileFormat.GeoParquet)
            {
                try
                {
                    geoParquetScratch = await PrepareGeoParquetScratchAsync(fileStream, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errorMessage = "Failed to buffer GeoParquet file for reading.";
                    ImportLog.ImportFailedWithException(_logger, ex, jobId, request.TableName);
                    result = ImportResult.CreateFailure(
                        request.TableName, format.Value, errorMessage, stopwatch.Elapsed, warnings);
                    return result;
                }

                var scratchStream = geoParquetScratch.Stream;

                GeoParquetReader.GeoParquetFileMetadata parquetMeta;
                try
                {
                    parquetMeta = await GeoParquetReader.ExtractMetadataAsync(scratchStream, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
                {
                    // Preserve the specific validation message (malformed geo metadata,
                    // missing primary column, etc.) instead of collapsing to "Import failed."
                    errorMessage = ex.Message;
                    ImportLog.ImportFailedWithException(_logger, ex, jobId, request.TableName);
                    result = ImportResult.CreateFailure(
                        request.TableName, format.Value, errorMessage, stopwatch.Elapsed, warnings);
                    return result;
                }

                warnings = parquetMeta.Warnings;

                // Hard-reject files with any oversized row group — Parquet.Net materializes
                // an entire row group's columns in memory, so unbounded groups defeat the
                // streaming memory contract. Log and fail fast instead of silently spiking memory.
                if (parquetMeta.MaxRowGroupRowCount > GeoParquetReader.MaxRowsPerRowGroup)
                {
                    GeoParquetLog.LargeRowGroupDetected(_logger, parquetMeta.RowGroupCount, parquetMeta.MaxRowGroupRowCount);
                    errorMessage = GeoParquetReader.BuildLargeRowGroupMessage(
                        parquetMeta.MaxRowGroupRowCount, parquetMeta.RowGroupCount);
                    result = ImportResult.CreateFailure(
                        request.TableName, format.Value,
                        errorMessage,
                        stopwatch.Elapsed, warnings);
                    return result;
                }

                // Hard-reject non-WKB encoding before any further processing
                if (!parquetMeta.IsWkbEncoding)
                {
                    errorMessage = GeoParquetReader.UnsupportedEncodingMessage;
                    result = ImportResult.CreateFailure(
                        request.TableName, format.Value,
                        errorMessage,
                        stopwatch.Elapsed, warnings);
                    return result;
                }

                detectedSrid = parquetMeta.Srid;

                // If scratch created a new stream (non-seekable input), dispose the original
                if (geoParquetScratch.ScratchDir != null && shouldDisposeStream)
                {
                    await fileStream.DisposeAsync();
                    shouldDisposeStream = false;
                }

                fileStream = scratchStream;
            }
            else if (format.Value == SupportedFileFormat.FlatGeobuf && !fileStream.CanSeek)
            {
                // FlatGeobuf requires seeking for both CRS detection and Deserialize.
                // Always spill non-seekable streams at the service level so progress
                // metrics, BytesRead, and TotalBytes are available from the seekable temp file.
                fgbTempStream = await SpillToSeekableTempAsync(fileStream, cancellationToken);
                if (shouldDisposeStream)
                {
                    await fileStream.DisposeAsync();
                }

                fileStream = fgbTempStream;
                shouldDisposeStream = true;
                totalBytes = fgbTempStream.Length;
                ImportLog.SpilledToTempFile(_logger, totalBytes.Value);
                detectedSrid = request.SourceSrid
                    ?? await DetectCrsStreamingAsync(fileStream, format.Value, cancellationToken);
                // ReadHeader advances position; reset so Deserialize can read from the start.
                fileStream.Position = 0;
            }
            else
            {
                if (request.SourceSrid.HasValue)
                {
                    detectedSrid = request.SourceSrid;
                }
                else if (!fileStream.CanSeek)
                {
                    var header = await ReadHeaderAsync(fileStream, cancellationToken);
                    detectedSrid = header == null
                        ? null
                        : await DetectCrsFromHeaderAsync(header, format.Value, cancellationToken);
                    if (header != null && header.Length > 0)
                    {
                        fileStream = new PrefixedReadStream(header, fileStream);
                    }
                }
                else
                {
                    // Detect CRS using streaming (doesn't load entire file)
                    detectedSrid = await DetectCrsStreamingAsync(fileStream, format.Value, cancellationToken);
                }
            }

            // FlatGeobuf CRS detection reads the binary header, advancing stream position.
            // Reset so Deserialize can read from the start (magic bytes at offset 0).
            if (format.Value == SupportedFileFormat.FlatGeobuf && fileStream.CanSeek)
            {
                fileStream.Position = 0;
            }

            // For FileGDB with no detectable SRID, fall back to TargetSrid (coordinates
            // may not need transformation if the GDB has no spatial features).
            var sourceSrid = request.SourceSrid ?? detectedSrid
                ?? (format.Value == SupportedFileFormat.FileGdb ? request.TargetSrid : null);
            if (!sourceSrid.HasValue)
            {
                errorMessage = $"Source SRID is required for {format.Value} imports when CRS cannot be detected.";
                result = ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    errorMessage,
                    stopwatch.Elapsed,
                    warnings);
                return result;
            }

            // Report initial progress
            progress?.Report(ImportProgress.CreateInitial(
                jobId,
                request.TableName,
                format.Value,
                fileStream.CanSeek ? fileStream.Length : null,
                warnings));

            // Stream features and insert in batches
            (importedCount, failedCount, warnings) = await ImportStreamingAsync(
                request,
                fileStream,
                format.Value,
                sourceSrid.Value,
                warnings,
                progress,
                jobId,
                cancellationToken,
                shapefileScratch,
                fileGdbScratch);

            if (importedCount == 0 && failedCount == 0)
            {
                errorMessage = "No features found in file";
                result = ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    errorMessage,
                    stopwatch.Elapsed,
                    warnings);
                return result;
            }

            status = "success";
            result = ImportResult.CreateSuccess(
                request.TableName,
                format.Value,
                importedCount,
                detectedSrid,
                stopwatch.Elapsed,
                warnings);
            return result;
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            errorMessage = "Import was cancelled";
            result = ImportResult.CreateFailure(
                request.TableName,
                format ?? SupportedFileFormat.GeoJson,
                errorMessage,
                stopwatch.Elapsed,
                warnings);
            return result;
        }
        catch (InvalidDataException ex)
        {
            // Preserve the specific message (e.g. "Row X in row group Y contains
            // invalid WKB geometry data") instead of collapsing to "Import failed."
            ImportLog.ImportFailedWithException(_logger, ex, jobId, request.TableName);
            errorMessage = ex.Message;
            result = ImportResult.CreateFailure(
                request.TableName,
                format ?? SupportedFileFormat.GeoJson,
                errorMessage,
                stopwatch.Elapsed,
                warnings);
            return result;
        }
        catch (Exception ex)
        {
            ImportLog.ImportFailedWithException(_logger, ex, jobId, request.TableName);
            errorMessage = "Import failed.";
            result = ImportResult.CreateFailure(
                request.TableName,
                format ?? SupportedFileFormat.GeoJson,
                errorMessage,
                stopwatch.Elapsed,
                warnings);
            return result;
        }
        finally
        {
            stopwatch.Stop();
            if (fileStream.CanSeek)
            {
                bytesRead = fileStream.Position;
            }

            // Dispose cloud storage stream if needed
            if (shouldDisposeStream)
            {
                await fileStream.DisposeAsync();
            }

            if (fgbTempStream != null && fgbTempStream != fileStream)
            {
                await fgbTempStream.DisposeAsync();
            }

            CleanupShapefileScratch(shapefileScratch);
            CleanupGeoPackageScratch(geoPackageScratch);
            CleanupKmzScratch(kmzScratch);
            CleanupFileGdbScratch(fileGdbScratch);
            CleanupGeoParquetScratch(geoParquetScratch);
            RecordImportMetrics(formatName, mode, status, stopwatch.Elapsed, bytesRead, importedCount, failedCount);

            if (status == "success")
            {
                ImportLog.ImportCompleted(_logger, jobId, request.TableName, formatName, mode, importedCount, failedCount,
                    stopwatch.Elapsed.TotalMilliseconds, bytesRead);
            }
            else if (status == "cancelled")
            {
                ImportLog.ImportCancelled(_logger, jobId, request.TableName, formatName, mode, importedCount, failedCount,
                    stopwatch.Elapsed.TotalMilliseconds, bytesRead);
            }
            else
            {
                ImportLog.ImportFailed(_logger, jobId, request.TableName, formatName, mode, importedCount, failedCount,
                    errorMessage ?? "Unknown error", stopwatch.Elapsed.TotalMilliseconds, bytesRead);
            }
        }
    }

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

        if (request.OverwriteExisting)
        {
            await CreateTableAsync(connection, allowedTableName, cancellationToken);
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

            batch.Add(feature);

            // Process batch when full
            if (batch.Count >= _limits.BatchSize)
            {
                var (imported, failed) = await InsertBatchAsync(
                    connection,
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
                    Format = format,
                    StartedAt = startTime,
                    BytesRead = fileStream.CanSeek ? fileStream.Position : 0,
                    TotalBytes = fileStream.CanSeek ? fileStream.Length : null,
                    Warnings = warnings
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

        await AnalyzeTableAsync(connection, allowedTableName, cancellationToken);

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
            Format = format,
            StartedAt = startTime,
            CompletedAt = DateTimeOffset.UtcNow,
            BytesRead = fileStream.CanSeek ? fileStream.Position : 0,
            TotalBytes = fileStream.CanSeek ? fileStream.Length : null,
            Warnings = completionWarnings
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

    /// <summary>
    /// Insert a batch of features with optional transaction.
    /// </summary>
    private async Task<(int imported, int failed)> InsertBatchAsync(
        NpgsqlConnection connection,
        string tableName,
        IReadOnlyList<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var failed = 0;

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
                    tableName,
                    features,
                    sourceSrid,
                    targetSrid,
                    wkbWriter,
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
                    tableName,
                    features,
                    sourceSrid,
                    targetSrid,
                    wkbWriter,
                    cancellationToken);
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            throw;
        }

        return (imported, failed);
    }

    private async Task<int> InsertBatchFastAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tableName,
        IReadOnlyList<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        var wkbs = new byte[]?[features.Count];
        var sourceSrids = new int[features.Count];
        var properties = new string[features.Count];

        for (var i = 0; i < features.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = features[i];
            wkbs[i] = CreateWkb(feature, wkbWriter);

            // Use per-feature SRID when available (e.g. multi-layer FileGDBs
            // where each layer may have its own CRS).
            var featureSrid = feature.Geometry?.SRID;
            sourceSrids[i] = featureSrid is > 0 ? featureSrid.Value : sourceSrid;
            properties[i] = BuildPropertiesJson(feature);
        }

        const string sql = """
            SELECT honua.insert_import_feature(
                @table_name,
                payload.wkb,
                payload.source_srid,
                @target_srid,
                payload.properties)
            FROM unnest(@wkbs, @source_srids, @properties) AS payload(wkb, source_srid, properties)
            """;

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        command.Parameters.Add("table_name", NpgsqlDbType.Text).Value = tableName;
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        command.Parameters.Add("wkbs", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = wkbs;
        command.Parameters.Add("source_srids", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = sourceSrids;
        command.Parameters.Add("properties", NpgsqlDbType.Array | NpgsqlDbType.Jsonb).Value = properties;

        var imported = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            imported++;
        }

        return imported;
    }

    private async Task<(int imported, int failed)> InsertBatchIndividuallyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tableName,
        IReadOnlyList<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var failed = 0;

        await using var command = new NpgsqlCommand(InsertImportFeatureSql, connection)
        {
            Transaction = transaction
        };
        command.Parameters.Add("table_name", NpgsqlDbType.Text).Value = tableName;
        var wkbParameter = command.Parameters.Add("wkb", NpgsqlDbType.Bytea);
        var sourceSridParameter = command.Parameters.Add("source_srid", NpgsqlDbType.Integer);
        sourceSridParameter.Value = sourceSrid;
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        var propertiesParameter = command.Parameters.Add("properties", NpgsqlDbType.Jsonb);

        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                wkbParameter.Value = CreateWkb(feature, wkbWriter) ?? (object)DBNull.Value;
                var featureSrid = feature.Geometry?.SRID;
                sourceSridParameter.Value = featureSrid is > 0 ? featureSrid.Value : sourceSrid;
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
    /// Create WKB for a feature geometry, enforcing configured validation limits.
    /// </summary>
    private byte[]? CreateWkb(IFeature feature, WKBWriter wkbWriter)
    {
        if (feature.Geometry == null)
        {
            return null;
        }

        if (_limits.ValidateGeometry)
        {
            var validationError = ValidateGeometry(feature.Geometry);
            if (validationError != null)
            {
                if (_limits.SkipInvalidGeometry)
                {
                    return null;
                }

                throw new InvalidOperationException($"Geometry validation failed: {validationError}");
            }
        }

        var writer = HasZ(feature.Geometry)
            ? new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false)
            : wkbWriter;
        var wkb = writer.Write(feature.Geometry);

        if (_limits.ValidateGeometry && wkb.Length > _limits.MaxWkbSize)
        {
            if (_limits.SkipInvalidGeometry)
            {
                return null;
            }

            throw new InvalidOperationException(
                $"Geometry WKB size ({wkb.Length:N0} bytes) exceeds maximum allowed ({_limits.MaxWkbSize:N0} bytes)");
        }

        return wkb;
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

    private static partial class ImportLog
    {
        [LoggerMessage(
            EventId = 7400,
            Level = LogLevel.Information,
            Message = "Import started {JobId} table={TableName} format={Format} mode={Mode} bytes={TotalBytes}")]
        public static partial void ImportStarted(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            string mode,
            long? totalBytes);

        [LoggerMessage(
            EventId = 7401,
            Level = LogLevel.Information,
            Message = "Import completed {JobId} table={TableName} format={Format} mode={Mode} imported={Imported} failed={Failed} durationMs={DurationMs:F2} bytes={BytesRead}")]
        public static partial void ImportCompleted(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            string mode,
            int imported,
            int failed,
            double durationMs,
            long? bytesRead);

        [LoggerMessage(
            EventId = 7402,
            Level = LogLevel.Warning,
            Message = "Import cancelled {JobId} table={TableName} format={Format} mode={Mode} imported={Imported} failed={Failed} durationMs={DurationMs:F2} bytes={BytesRead}")]
        public static partial void ImportCancelled(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            string mode,
            int imported,
            int failed,
            double durationMs,
            long? bytesRead);

        [LoggerMessage(
            EventId = 7403,
            Level = LogLevel.Error,
            Message = "Import failed {JobId} table={TableName} format={Format} mode={Mode} imported={Imported} failed={Failed} error={ErrorMessage} durationMs={DurationMs:F2} bytes={BytesRead}")]
        public static partial void ImportFailed(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            string mode,
            int imported,
            int failed,
            string errorMessage,
            double durationMs,
            long? bytesRead);

        [LoggerMessage(
            EventId = 7407,
            Level = LogLevel.Warning,
            Message = "Import failed with exception {JobId} table={TableName}")]
        public static partial void ImportFailedWithException(
            ILogger logger,
            Exception exception,
            string jobId,
            string tableName);

        [LoggerMessage(
            EventId = 7408,
            Level = LogLevel.Debug,
            Message = "Feature insert failed table={TableName}")]
        public static partial void FeatureInsertFailed(
            ILogger logger,
            Exception exception,
            string tableName);

        [LoggerMessage(
            EventId = 7415,
            Level = LogLevel.Debug,
            Message = "Spilled non-seekable stream to temp file bytes={SpilledBytes}")]
        public static partial void SpilledToTempFile(
            ILogger logger,
            long spilledBytes);
    }

    private static partial class ShapefileLog
    {
        [LoggerMessage(
            EventId = 7404,
            Level = LogLevel.Warning,
            Message = "Failed to delete temporary zip file {ZipPath}")]
        public static partial void DeleteZipFailed(ILogger logger, Exception exception, string zipPath);

        [LoggerMessage(
            EventId = 7405,
            Level = LogLevel.Warning,
            Message = "Failed to clean up shapefile scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);
    }

    private static partial class GeoPackageLog
    {
        [LoggerMessage(
            EventId = 7406,
            Level = LogLevel.Warning,
            Message = "Failed to clean up GeoPackage scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);
    }

    private static partial class KmzLog
    {
        [LoggerMessage(
            EventId = 7409,
            Level = LogLevel.Warning,
            Message = "Failed to delete temporary KMZ file {ZipPath}")]
        public static partial void DeleteZipFailed(ILogger logger, Exception exception, string zipPath);

        [LoggerMessage(
            EventId = 7410,
            Level = LogLevel.Warning,
            Message = "Failed to clean up KMZ scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);
    }

    private static partial class FileGdbLog
    {
        [LoggerMessage(
            EventId = 7411,
            Level = LogLevel.Warning,
            Message = "Failed to clean up FileGDB scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);
    }

    private static partial class GeoParquetLog
    {
        [LoggerMessage(
            EventId = 7440,
            Level = LogLevel.Warning,
            Message = "Failed to clean up GeoParquet scratch directory {ScratchDir}")]
        public static partial void CleanupScratchFailed(ILogger logger, Exception exception, string scratchDir);

        [LoggerMessage(
            EventId = 7441,
            Level = LogLevel.Warning,
            Message = "GeoParquet file has {RowGroupCount} row group(s) with largest group containing {MaxGroupRows} rows. " +
                      "Row groups exceeding the per-group limit will materialize too much data in memory during import")]
        public static partial void LargeRowGroupDetected(ILogger logger, int rowGroupCount, long maxGroupRows);
    }

    /// <inheritdoc/>
    public async Task<FilePreview> PreviewFileAsync(
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var format = DetectFormat(fileName);

        if (!format.HasValue)
        {
            throw new NotSupportedException("Unsupported file format: " + Path.GetExtension(fileName));
        }

        ShapefileScratch? shapefileScratch = null;
        GeoPackageScratch? geoPackageScratch = null;
        KmzScratch? kmzScratch = null;
        FileGdbScratch? fileGdbScratch = null;
        GeoPackageLayerInfo? previewGeoPackageLayer = null;
        FileGdb.FileGdbReader.FileGdbLayerInfo? previewFileGdbLayer = null;
        GeoParquetScratch? geoParquetScratch = null;
        long? geoParquetTotalRows = null;
        FileStream? fgbTempStream = null;
        string[] warnings = [];
        Stream? kmzStream = null;
        try
        {
            int? detectedSrid;
            string[] availableLayers = [];

            if (format.Value == SupportedFileFormat.GeoPackage)
            {
                geoPackageScratch = await PrepareGeoPackageScratchAsync(fileStream, cancellationToken);
                var layers = await GetGeoPackageLayersAsync(geoPackageScratch.FilePath, cancellationToken);
                if (layers.Count == 0)
                {
                    throw new InvalidDataException("GeoPackage does not contain any feature layers.");
                }

                availableLayers = layers.Select(layer => layer.TableName).ToArray();
                previewGeoPackageLayer = layers[0];
                detectedSrid = layers[0].Srid;
            }
            else if (format.Value == SupportedFileFormat.Shapefile)
            {
                if (!IsZipFileName(fileName))
                {
                    throw new NotSupportedException("Shapefile preview requires a .zip containing .shp and .dbf files.");
                }

                shapefileScratch = await PrepareShapefileScratchAsync(fileStream, fileName, cancellationToken);
                detectedSrid = await _crsDetectionService.DetectFromShapefilePrjAsync(shapefileScratch.ShpPath);
            }
            else if (format.Value == SupportedFileFormat.Kml && IsKmzFileName(fileName))
            {
                kmzScratch = await PrepareKmzScratchAsync(fileStream, cancellationToken);
                kmzStream = new FileStream(kmzScratch.KmlPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = _limits.StreamBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                fileStream = kmzStream;
                detectedSrid = SpatialConstants.DefaultSrid;
            }
            else if (format.Value == SupportedFileFormat.FileGdb)
            {
                fileGdbScratch = await PrepareFileGdbScratchAsync(fileStream, cancellationToken);
                var layers = FileGdb.FileGdbReader.DiscoverLayers(fileGdbScratch.GdbPath);
                availableLayers = layers.Select(layer => layer.Name).ToArray();
                previewFileGdbLayer = layers.Length > 0 ? layers[0] : null;
                detectedSrid = layers.FirstOrDefault(layer => layer.Srid > 0).Srid;
                if (detectedSrid <= 0)
                {
                    detectedSrid = FileGdb.FileGdbReader.DetectSrid(fileGdbScratch.GdbPath);
                }

                warnings = FileGdb.FileGdbAdvancedConstructs.DetectWarnings(fileGdbScratch.GdbPath);
                if (layers.Length > 1)
                {
                    warnings = warnings
                        .Append(FileGdb.FileGdbReader.BuildMultiLayerImportMessage(layers))
                        .ToArray();
                }
            }
            else if (format.Value == SupportedFileFormat.GeoParquet)
            {
                geoParquetScratch = await PrepareGeoParquetScratchAsync(fileStream, cancellationToken);
                var scratchStream = geoParquetScratch.Stream;
                var parquetMeta = await GeoParquetReader.ExtractMetadataAsync(scratchStream, cancellationToken);
                detectedSrid = parquetMeta.Srid;
                warnings = parquetMeta.Warnings;
                geoParquetTotalRows = parquetMeta.TotalRowCount;

                // Reject files with any oversized row group — consistent with import path
                if (parquetMeta.MaxRowGroupRowCount > GeoParquetReader.MaxRowsPerRowGroup)
                {
                    throw new InvalidDataException(
                        GeoParquetReader.BuildLargeRowGroupMessage(
                            parquetMeta.MaxRowGroupRowCount, parquetMeta.RowGroupCount));
                }

                // Hard-reject non-WKB encoding — consistent with the import path
                // (ImportFileAsync returns CreateFailure) and the documented contract
                // ("Non-WKB encodings are rejected" in CONTROL_PLANE_API.md).
                if (!parquetMeta.IsWkbEncoding)
                {
                    throw new NotSupportedException(
                        GeoParquetReader.UnsupportedEncodingMessage);
                }

                fileStream = scratchStream;
            }
            else if (format.Value == SupportedFileFormat.FlatGeobuf && !fileStream.CanSeek)
            {
                // FlatGeobuf binary headers can exceed the 8 KB CRS-detection buffer when
                // the schema has many columns. Spill to a seekable temp file so full-header
                // CRS detection and the library's Deserialize (which requires seeking) work.
                fgbTempStream = await SpillToSeekableTempAsync(fileStream, cancellationToken);
                fileStream = fgbTempStream;
                ImportLog.SpilledToTempFile(_logger, fgbTempStream.Length);
                detectedSrid = await DetectCrsStreamingAsync(fileStream, format.Value, cancellationToken);
            }
            else
            {
                if (!fileStream.CanSeek)
                {
                    var header = await ReadHeaderAsync(fileStream, cancellationToken);
                    detectedSrid = header == null
                        ? null
                        : await DetectCrsFromHeaderAsync(header, format.Value, cancellationToken);
                    if (header != null && header.Length > 0)
                    {
                        fileStream = new PrefixedReadStream(header, fileStream);
                    }
                }
                else
                {
                    // Detect CRS using streaming
                    detectedSrid = await DetectCrsStreamingAsync(fileStream, format.Value, cancellationToken);
                }
            }

            // FlatGeobuf embeds the total feature count in its binary header.
            // Read it before enumeration so the preview can report the true total
            // even when the file exceeds the sample cap.
            int? headerFeatureCount = null;
            if (format.Value == SupportedFileFormat.FlatGeobuf && fileStream.CanSeek)
            {
                // CRS detection may have advanced position past the header;
                // reset so ReadHeaderFeatureCount reads from the magic bytes.
                fileStream.Position = 0;
                headerFeatureCount = FlatGeobufFormatReader.ReadHeaderFeatureCount(fileStream);
                fileStream.Position = 0;
            }

            // Stream features but only collect up to the limit
            var features = new List<IFeature>();
            var featureStream = format.Value switch
            {
                SupportedFileFormat.GeoJson => _geoJsonReader.ReadFeaturesAsync(fileStream, cancellationToken),
                SupportedFileFormat.Wkt => WktFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Kml => KmlFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Gpx => GpxFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Csv => CsvFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.FlatGeobuf => FlatGeobufFormatReader.ReadStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(shapefileScratch!.ShpPath, cancellationToken),
                SupportedFileFormat.GeoPackage => ReadGeoPackageLayerAsync(
                    geoPackageScratch!.FilePath,
                    previewGeoPackageLayer ?? throw new InvalidOperationException("GeoPackage preview layer was not prepared."),
                    cancellationToken),
                SupportedFileFormat.FileGdb => previewFileGdbLayer.HasValue
                    ? FileGdb.FileGdbReader.ReadLayerStreamingAsync(fileGdbScratch!.GdbPath, previewFileGdbLayer.Value, cancellationToken)
                    : EmptyFeatureStream(cancellationToken),
                SupportedFileFormat.GeoParquet => GeoParquetReader.ReadStreamingAsync(fileStream, cancellationToken),
                _ => throw new NotSupportedException($"Preview not supported for format: {format}")
            };

            // FlatGeobuf files serialized by some writers (including NTS) set FeaturesCount=0
            // (meaning "unknown"). When the header count is unavailable, continue iterating
            // past the sample cap to count features — but only up to MaxPreviewCountScan to
            // prevent unbounded scans of very large files.
            var needsFullCount = format.Value == SupportedFileFormat.FlatGeobuf
                && !headerFeatureCount.HasValue;
            var totalStreamedCount = 0;

            await foreach (var feature in featureStream.WithCancellation(cancellationToken))
            {
                // GeoParquet: skip null-geometry rows from preview samples, matching
                // the import path's skip behavior (ImportStreamingAsync).
                if (format.Value == SupportedFileFormat.GeoParquet && feature.Geometry == null)
                {
                    continue;
                }

                if (needsFullCount && totalStreamedCount >= _limits.MaxPreviewCountScan)
                {
                    break;
                }

                totalStreamedCount++;
                if (features.Count < _limits.MaxPreviewFeatures)
                {
                    features.Add(feature);
                }
                else if (!needsFullCount)
                {
                    break;
                }
            }

            var sampleProperties = new Dictionary<string, object?>();
            var firstFeature = features.FirstOrDefault();
            if (firstFeature?.Attributes is not null)
            {
                var names = firstFeature.Attributes.GetNames();
                var values = firstFeature.Attributes.GetValues();
                sampleProperties = names.Zip(values).ToDictionary(
                    pair => pair.First,
                    pair => pair.Second is byte[] bytes
                        ? (object?)Convert.ToBase64String(bytes)
                        : pair.Second);
            }

            return new FilePreview
            {
                Format = format.Value,
                TotalFeatureCount = geoParquetTotalRows.HasValue
                    ? (int)Math.Min(geoParquetTotalRows.Value, int.MaxValue)
                    : headerFeatureCount ?? (needsFullCount ? totalStreamedCount : features.Count),
                DetectedSrid = detectedSrid,
                SampleProperties = sampleProperties,
                AvailableLayers = availableLayers,
                Warnings = warnings
            };
        }
        finally
        {
            if (kmzStream != null)
            {
                await kmzStream.DisposeAsync();
            }

            if (fgbTempStream != null)
            {
                await fgbTempStream.DisposeAsync();
            }

            CleanupShapefileScratch(shapefileScratch);
            CleanupGeoPackageScratch(geoPackageScratch);
            CleanupKmzScratch(kmzScratch);
            CleanupFileGdbScratch(fileGdbScratch);
            CleanupGeoParquetScratch(geoParquetScratch);
        }
    }

    /// <summary>
    /// Detect CRS from stream without loading entire file.
    /// </summary>
    private async Task<int?> DetectCrsStreamingAsync(
        Stream stream,
        SupportedFileFormat format,
        CancellationToken cancellationToken)
    {
        try
        {
            return format switch
            {
                SupportedFileFormat.GeoJson => await DetectGeoJsonSridAsync(stream, cancellationToken),
                SupportedFileFormat.Kml => 4326,
                SupportedFileFormat.Gpx => 4326,
                SupportedFileFormat.Csv => 4326,
                SupportedFileFormat.Wkt => await DetectWktSridAsync(stream, cancellationToken),
                SupportedFileFormat.GeoPackage => await DetectGeoPackageSridAsync(stream, cancellationToken),
                SupportedFileFormat.FlatGeobuf => await DetectFlatGeobufCrsAsync(stream),
                SupportedFileFormat.FileGdb => null, // CRS detected during preparation via FileGdbReader.DetectSrid
                _ => null
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = 0;
        }
    }

    /// <summary>
    /// Detects the SRID from a FlatGeobuf stream by reading the binary header.
    /// Tries code/codeString/authority resolution first, then falls back to WKT
    /// parsing via <see cref="ICrsDetectionService.DetectFromWktAsync"/> when the
    /// header embeds CRS as WKT only.
    /// </summary>
    private async Task<int?> DetectFlatGeobufCrsAsync(Stream stream)
    {
        var (srid, crsWkt) = FlatGeobufFormatReader.ReadCrsInfo(stream);
        if (srid.HasValue) return srid;
        if (!string.IsNullOrEmpty(crsWkt))
            return await _crsDetectionService.DetectFromWktAsync(crsWkt);
        return null;
    }

    private static async Task<int?> DetectGeoJsonSridAsync(Stream stream, CancellationToken cancellationToken)
    {
        var detected = await StreamingGeoJsonReader.DetectCrsAsync(stream, cancellationToken);
        return detected ?? 4326;
    }

    private static async Task<byte[]?> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[CrsDetectionHeaderSize];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (bytesRead <= 0)
        {
            return null;
        }

        if (bytesRead == buffer.Length)
        {
            return buffer;
        }

        Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    private async Task<int?> DetectCrsFromHeaderAsync(
        byte[] header,
        SupportedFileFormat format,
        CancellationToken cancellationToken)
    {
        await using var headerStream = new MemoryStream(header, writable: false);

        return format switch
        {
            SupportedFileFormat.GeoJson => await DetectGeoJsonSridAsync(headerStream, cancellationToken),
            SupportedFileFormat.Kml => 4326,
            SupportedFileFormat.Gpx => 4326,
            SupportedFileFormat.Csv => 4326,
            SupportedFileFormat.Wkt => await DetectWktSridAsync(headerStream, cancellationToken),
            // Defensive: currently unreachable — FlatGeobuf non-seekable paths spill to
            // a seekable temp file before reaching here (see the dedicated FlatGeobuf branch above).
            SupportedFileFormat.FlatGeobuf => await DetectFlatGeobufCrsFromHeaderAsync(header),
            _ => null
        };
    }

    /// <summary>
    /// Defensive buffer-based FlatGeobuf CRS detection with WKT fallback.
    /// Currently unreachable — non-seekable FlatGeobuf streams are spilled to
    /// seekable temp files before the header-buffer path.
    /// </summary>
    private async Task<int?> DetectFlatGeobufCrsFromHeaderAsync(byte[] headerBytes)
    {
        var (srid, crsWkt) = FlatGeobufFormatReader.ReadCrsInfoFromHeader(headerBytes);
        if (srid.HasValue) return srid;
        if (!string.IsNullOrEmpty(crsWkt))
            return await _crsDetectionService.DetectFromWktAsync(crsWkt);
        return null;
    }

    private static async Task<int?> DetectWktSridAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[CrsDetectionHeaderSize];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (bytesRead <= 0)
        {
            return null;
        }

        var header = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        var match = _wktSridRegex.Match(header);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var srid))
        {
            return null;
        }

        return srid;
    }

    private async Task<int?> DetectGeoPackageSridAsync(Stream stream, CancellationToken cancellationToken)
    {
        GeoPackageScratch? scratch = null;
        try
        {
            scratch = await PrepareGeoPackageScratchAsync(stream, cancellationToken);
            var layers = await GetGeoPackageLayersAsync(scratch.FilePath, cancellationToken);
            return layers.Count > 0 ? layers[0].Srid : null;
        }
        finally
        {
            CleanupGeoPackageScratch(scratch);
        }
    }

    private sealed record ShapefileScratch(string DirectoryPath, string ShpPath);

    private sealed record GeoPackageScratch(string DirectoryPath, string FilePath);

    private sealed record KmzScratch(string DirectoryPath, string KmlPath);

    private sealed record FileGdbScratch(string DirectoryPath, string GdbPath);

    private sealed record GeoParquetScratch(Stream Stream, string? ScratchDir) : IDisposable
    {
        public void Dispose()
        {
            // Only dispose the stream when the scratch owns it (non-seekable buffered copy).
            // When ScratchDir is null, the scratch wraps the caller's original seekable stream
            // and the caller is responsible for its lifetime.
            if (ScratchDir != null)
            {
                Stream.Dispose();
            }
        }
    }

    private sealed record GeoPackageLayerInfo(string TableName, string GeometryColumn, int? Srid);

    private static async IAsyncEnumerable<IFeature> EmptyFeatureStream(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Copies a non-seekable stream into a seekable temporary file (DeleteOnClose).
    /// Delegates to <see cref="ImportStreamHelper.SpillToSeekableTempAsync"/>.
    /// </summary>
    private static Task<FileStream> SpillToSeekableTempAsync(Stream source, CancellationToken cancellationToken)
        => ImportStreamHelper.SpillToSeekableTempAsync(source, cancellationToken);

    private sealed class PrefixedReadStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _prefix;
        private readonly Stream _inner;
        private int _prefixOffset;
        // Track logical position for diagnostics even on non-seekable wrappers.
        private long _position;

        public PrefixedReadStream(ReadOnlyMemory<byte> prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0)
            {
                return 0;
            }

            var bytesRead = ReadPrefix(buffer.AsSpan(offset, count));
            if (bytesRead < count)
            {
                bytesRead += _inner.Read(buffer, offset + bytesRead, count - bytesRead);
            }

            _position += bytesRead;
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            var bytesRead = ReadPrefix(buffer);
            if (bytesRead < buffer.Length)
            {
                bytesRead += _inner.Read(buffer[bytesRead..]);
            }

            _position += bytesRead;
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = ReadPrefix(buffer.AsSpan(offset, count));
            if (bytesRead < count)
            {
                bytesRead += await _inner.ReadAsync(buffer.AsMemory(offset + bytesRead, count - bytesRead), cancellationToken);
            }

            _position += bytesRead;
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = ReadPrefix(buffer.Span);
            if (bytesRead < buffer.Length)
            {
                bytesRead += await _inner.ReadAsync(buffer[bytesRead..], cancellationToken);
            }

            _position += bytesRead;
            return bytesRead;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private int ReadPrefix(Span<byte> buffer)
        {
            var remaining = _prefix.Length - _prefixOffset;
            if (remaining <= 0)
            {
                return 0;
            }

            var toCopy = Math.Min(buffer.Length, remaining);
            _prefix.Span.Slice(_prefixOffset, toCopy).CopyTo(buffer[..toCopy]);
            _prefixOffset += toCopy;
            return toCopy;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private sealed record ShapefileEntries(
        string BaseName,
        ZipArchiveEntry ShpEntry,
        ZipArchiveEntry DbfEntry,
        ZipArchiveEntry? ShxEntry,
        ZipArchiveEntry? PrjEntry,
        ZipArchiveEntry? CpgEntry);

    private sealed class ShapefileEntryGroup
    {
        public ShapefileEntryGroup(string baseName)
        {
            BaseName = baseName;
        }

        public string BaseName { get; }
        public ZipArchiveEntry? Shp { get; set; }
        public ZipArchiveEntry? Dbf { get; set; }
        public ZipArchiveEntry? Shx { get; set; }
        public ZipArchiveEntry? Prj { get; set; }
        public ZipArchiveEntry? Cpg { get; set; }
    }

    private sealed class ArchiveExtractionBudget
    {
        public ArchiveExtractionBudget(long maxTotalBytes, long maxEntryBytes, double maxCompressionRatio)
        {
            MaxTotalBytes = maxTotalBytes;
            MaxEntryBytes = maxEntryBytes;
            MaxCompressionRatio = maxCompressionRatio;
        }

        public long MaxTotalBytes { get; }
        public long MaxEntryBytes { get; }
        public double MaxCompressionRatio { get; }
        public long TotalExtractedBytes { get; set; }
    }

    private static bool IsZipFileName(string fileName)
        => string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsKmzFileName(string fileName)
        => string.Equals(Path.GetExtension(fileName), ".kmz", StringComparison.OrdinalIgnoreCase);

    private async Task<ShapefileScratch> PrepareShapefileScratchAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!IsZipFileName(fileName))
        {
            throw new NotSupportedException("Shapefile imports require a .zip containing .shp and .dbf files.");
        }

        var scratchDir = Path.Combine(_shapefileScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        string? zipPath = null;
        Stream? zipStream = null;
        var leaveOpen = false;

        try
        {
            if (stream.CanSeek)
            {
                if (stream.Position != 0)
                {
                    stream.Position = 0;
                }

                zipStream = stream;
                leaveOpen = true;
            }
            else
            {
                zipPath = Path.Combine(scratchDir, "upload.zip");
                await using (var zipFileStream = File.Create(zipPath))
                {
                    await stream.CopyToAsync(zipFileStream, cancellationToken);
                }

                zipStream = File.OpenRead(zipPath);
            }

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen);
            var entries = SelectShapefileEntries(archive)
                ?? throw new InvalidDataException("Zip does not contain required .shp and .dbf files.");
            var extractionBudget = CreateArchiveExtractionBudget();

            var shpPath = Path.Combine(scratchDir, entries.BaseName + ".shp");
            var dbfPath = Path.Combine(scratchDir, entries.BaseName + ".dbf");

            await ExtractEntryAsync(entries.ShpEntry, shpPath, extractionBudget, cancellationToken);
            await ExtractEntryAsync(entries.DbfEntry, dbfPath, extractionBudget, cancellationToken);

            if (entries.ShxEntry != null)
            {
                await ExtractEntryAsync(entries.ShxEntry, Path.Combine(scratchDir, entries.BaseName + ".shx"), extractionBudget, cancellationToken);
            }

            if (entries.PrjEntry != null)
            {
                await ExtractEntryAsync(entries.PrjEntry, Path.Combine(scratchDir, entries.BaseName + ".prj"), extractionBudget, cancellationToken);
            }

            if (entries.CpgEntry != null)
            {
                await ExtractEntryAsync(entries.CpgEntry, Path.Combine(scratchDir, entries.BaseName + ".cpg"), extractionBudget, cancellationToken);
            }

            return new ShapefileScratch(scratchDir, shpPath);
        }
        catch
        {
            CleanupShapefileScratchDirectory(scratchDir);
            throw;
        }
        finally
        {
            if (zipStream != null && !leaveOpen)
            {
                await zipStream.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch (Exception ex)
                {
                    ShapefileLog.DeleteZipFailed(_logger, ex, zipPath);
                }
            }
        }
    }

    private async Task<GeoPackageScratch> PrepareGeoPackageScratchAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var scratchDir = Path.Combine(_geoPackageScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        var filePath = Path.Combine(scratchDir, "upload.gpkg");

        try
        {
            if (stream.CanSeek && stream.Position != 0)
            {
                stream.Position = 0;
            }

            await using var outputStream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = _limits.StreamBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            await stream.CopyToAsync(outputStream, cancellationToken);

            return new GeoPackageScratch(scratchDir, filePath);
        }
        catch
        {
            CleanupGeoPackageScratchDirectory(scratchDir);
            throw;
        }
    }

    private async Task<KmzScratch> PrepareKmzScratchAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var scratchDir = Path.Combine(_kmzScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        string? zipPath = null;
        Stream? zipStream = null;
        var leaveOpen = false;

        try
        {
            if (stream.CanSeek)
            {
                if (stream.Position != 0)
                {
                    stream.Position = 0;
                }

                zipStream = stream;
                leaveOpen = true;
            }
            else
            {
                zipPath = Path.Combine(scratchDir, "upload.kmz");
                await using (var zipFileStream = File.Create(zipPath))
                {
                    await stream.CopyToAsync(zipFileStream, cancellationToken);
                }

                zipStream = File.OpenRead(zipPath);
            }

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen);
            var kmlEntry = SelectKmzKmlEntry(archive)
                ?? throw new InvalidDataException("KMZ does not contain a .kml file.");
            var extractionBudget = CreateArchiveExtractionBudget();

            var kmlPath = Path.Combine(scratchDir, "doc.kml");
            await ExtractEntryAsync(kmlEntry, kmlPath, extractionBudget, cancellationToken);

            return new KmzScratch(scratchDir, kmlPath);
        }
        catch
        {
            CleanupKmzScratchDirectory(scratchDir);
            throw;
        }
        finally
        {
            if (zipStream != null && !leaveOpen)
            {
                await zipStream.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch (Exception ex)
                {
                    KmzLog.DeleteZipFailed(_logger, ex, zipPath);
                }
            }
        }
    }

    /// <summary>
    /// Buffer a GeoParquet stream to a seekable stream if needed.
    /// </summary>
    private async Task<GeoParquetScratch> PrepareGeoParquetScratchAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            if (stream.Position != 0)
            {
                stream.Position = 0;
            }

            return new GeoParquetScratch(stream, null);
        }

        var scratchDir = Path.Combine(_geoParquetScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        var filePath = Path.Combine(scratchDir, "upload.parquet");

        try
        {
            await using var outputStream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = _limits.StreamBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            await stream.CopyToAsync(outputStream, cancellationToken);

            var readStream = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = _limits.StreamBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.RandomAccess
            });

            return new GeoParquetScratch(readStream, scratchDir);
        }
        catch
        {
            CleanupGeoParquetScratchDirectory(scratchDir);
            throw;
        }
    }

    /// <summary>
    /// Extract a FileGDB .gdb.zip archive to a scratch directory.
    /// </summary>
    private async Task<FileGdbScratch> PrepareFileGdbScratchAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var scratchDir = Path.Combine(_fileGdbScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);

        string? zipPath = null;
        Stream? zipStream = null;
        var leaveOpen = false;

        try
        {
            if (stream.CanSeek)
            {
                if (stream.Position != 0)
                {
                    stream.Position = 0;
                }

                zipStream = stream;
                leaveOpen = true;
            }
            else
            {
                zipPath = Path.Combine(scratchDir, "upload.gdb.zip");
                await using (var zipFileStream = File.Create(zipPath))
                {
                    await stream.CopyToAsync(zipFileStream, cancellationToken);
                }

                zipStream = File.OpenRead(zipPath);
            }

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen);
            var extractionBudget = CreateArchiveExtractionBudget();

            // Extract all entries, maintaining directory structure
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // directory entry
                }

                // Security: prevent path traversal with multiple layers of protection
                if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.Contains("..") ||
                    entry.FullName.StartsWith('/') || entry.FullName.StartsWith('\\'))
                {
                    throw new InvalidDataException("Archive contains invalid entry name.");
                }

                // The trailing separator ensures a sibling directory that shares a prefix cannot pass the check.
                var normalizedRoot = scratchDir.EndsWith(Path.DirectorySeparatorChar)
                    ? scratchDir
                    : scratchDir + Path.DirectorySeparatorChar;
                var entryPath = Path.GetFullPath(Path.Combine(scratchDir, entry.FullName));
                if (!entryPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Archive contains path traversal.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
                await ExtractEntryAsync(entry, entryPath, extractionBudget, cancellationToken);
            }

            // Find the .gdb directory
            var gdbDir = Directory.GetDirectories(scratchDir, "*.gdb", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (gdbDir == null)
            {
                // Maybe the files are at the root level (no subfolder)
                gdbDir = Directory.GetFiles(scratchDir, "a00000001.gdbtable", SearchOption.AllDirectories)
                    .Select(f => Path.GetDirectoryName(f))
                    .FirstOrDefault();
            }

            if (gdbDir == null)
            {
                throw new InvalidDataException("Archive does not contain a valid File Geodatabase (.gdb directory).");
            }

            return new FileGdbScratch(scratchDir, gdbDir);
        }
        catch (InvalidDataException)
        {
            CleanupFileGdbScratchDirectory(scratchDir);
            throw;
        }
        catch
        {
            CleanupFileGdbScratchDirectory(scratchDir);
            throw;
        }
        finally
        {
            if (zipStream != null && !leaveOpen)
            {
                await zipStream.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
            {
                try
                {
                    File.Delete(zipPath);
                }
                catch
                {
                    // Best-effort cleanup; scratch directory deletion will handle this
                }
            }
        }
    }

    private static ShapefileEntries? SelectShapefileEntries(ZipArchive archive)
    {
        var groups = new Dictionary<string, ShapefileEntryGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var extension = Path.GetExtension(entry.Name);
            if (string.IsNullOrWhiteSpace(extension) || !_shapefileComponentExtensions.Contains(extension))
            {
                continue;
            }

            var baseName = Path.GetFileNameWithoutExtension(entry.Name);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                continue;
            }

            var groupKey = GetShapefileComponentGroupKey(entry, baseName);
            if (!groups.TryGetValue(groupKey, out var group))
            {
                group = new ShapefileEntryGroup(baseName);
                groups.Add(groupKey, group);
            }

            switch (extension.ToLowerInvariant())
            {
                case ".shp":
                    group.Shp = entry;
                    break;
                case ".dbf":
                    group.Dbf = entry;
                    break;
                case ".shx":
                    group.Shx = entry;
                    break;
                case ".prj":
                    group.Prj = entry;
                    break;
                case ".cpg":
                    group.Cpg = entry;
                    break;
            }
        }

        foreach (var group in groups.Values)
        {
            if (group.Shp != null && group.Dbf != null)
            {
                return new ShapefileEntries(group.BaseName, group.Shp, group.Dbf, group.Shx, group.Prj, group.Cpg);
            }
        }

        return null;
    }

    private static string GetShapefileComponentGroupKey(ZipArchiveEntry entry, string baseName)
    {
        var normalizedName = entry.FullName.Replace('\\', '/');
        var slashIndex = normalizedName.LastIndexOf('/');
        var directory = slashIndex >= 0 ? normalizedName[..slashIndex] : string.Empty;
        return string.Concat(directory, "/", baseName);
    }

    private static ZipArchiveEntry? SelectKmzKmlEntry(ZipArchive archive)
    {
        ZipArchiveEntry? firstMatch = null;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            if (!string.Equals(Path.GetExtension(entry.Name), ".kml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(entry.Name, "doc.kml", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            firstMatch ??= entry;
        }

        return firstMatch;
    }

    private ArchiveExtractionBudget CreateArchiveExtractionBudget()
    {
        var maxEntryBytes = _limits.MaxArchiveEntryBytes > 0
            ? _limits.MaxArchiveEntryBytes
            : DefaultMaxArchiveEntryBytes;
        var maxTotalBytes = _limits.MaxArchiveExtractedBytes > 0
            ? _limits.MaxArchiveExtractedBytes
            : DefaultMaxArchiveExtractedBytes;
        var maxCompressionRatio = _limits.MaxArchiveCompressionRatio > 1
            ? _limits.MaxArchiveCompressionRatio
            : DefaultMaxArchiveCompressionRatio;

        if (maxTotalBytes < maxEntryBytes)
        {
            maxTotalBytes = maxEntryBytes;
        }

        return new ArchiveExtractionBudget(maxTotalBytes, maxEntryBytes, maxCompressionRatio);
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        ArchiveExtractionBudget extractionBudget,
        CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.CompressedLength < 0)
        {
            throw new InvalidDataException($"Archive entry '{entry.FullName}' has an invalid size.");
        }

        if (entry.Length > extractionBudget.MaxEntryBytes)
        {
            throw new InvalidDataException(
                $"Archive entry '{entry.FullName}' exceeds maximum uncompressed size ({extractionBudget.MaxEntryBytes:N0} bytes).");
        }

        if (entry.Length > extractionBudget.MaxTotalBytes - extractionBudget.TotalExtractedBytes)
        {
            throw new InvalidDataException(
                $"Archive extraction exceeds maximum total uncompressed size ({extractionBudget.MaxTotalBytes:N0} bytes).");
        }

        if (entry.Length > 0)
        {
            if (entry.CompressedLength <= 0)
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' has invalid compressed size.");
            }

            var compressionRatio = (double)entry.Length / entry.CompressedLength;
            if (compressionRatio > extractionBudget.MaxCompressionRatio)
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' exceeds maximum compression ratio ({extractionBudget.MaxCompressionRatio:N0}).");
            }
        }

        var entryExtractedBytes = 0L;
        var buffer = new byte[64 * 1024];
        await using var entryStream = entry.Open();
        await using var outputStream = File.Create(destinationPath);
        while (true)
        {
            var bytesRead = await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            entryExtractedBytes += bytesRead;
            if (entryExtractedBytes > extractionBudget.MaxEntryBytes)
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' exceeds maximum uncompressed size ({extractionBudget.MaxEntryBytes:N0} bytes).");
            }

            if (bytesRead > extractionBudget.MaxTotalBytes - extractionBudget.TotalExtractedBytes)
            {
                throw new InvalidDataException(
                    $"Archive extraction exceeds maximum total uncompressed size ({extractionBudget.MaxTotalBytes:N0} bytes).");
            }

            await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            extractionBudget.TotalExtractedBytes += bytesRead;
        }
    }

    private void CleanupShapefileScratch(ShapefileScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        CleanupShapefileScratchDirectory(scratch.DirectoryPath);
    }

    private void CleanupShapefileScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            ShapefileLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }

    private void CleanupGeoPackageScratch(GeoPackageScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        CleanupGeoPackageScratchDirectory(scratch.DirectoryPath);
    }

    private void CleanupKmzScratch(KmzScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        CleanupKmzScratchDirectory(scratch.DirectoryPath);
    }

    private void CleanupGeoPackageScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            GeoPackageLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }

    private void CleanupKmzScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            KmzLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }

    private void CleanupFileGdbScratch(FileGdbScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        CleanupFileGdbScratchDirectory(scratch.DirectoryPath);
    }

    private void CleanupFileGdbScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            FileGdbLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }

    private void CleanupGeoParquetScratch(GeoParquetScratch? scratch)
    {
        if (scratch == null)
        {
            return;
        }

        // Only dispose stream and directory if we created a scratch copy.
        // When ScratchDir is null, the scratch wraps the caller's original seekable
        // stream which is disposed by the caller.
        if (scratch.ScratchDir != null)
        {
            scratch.Dispose();
            CleanupGeoParquetScratchDirectory(scratch.ScratchDir);
        }
    }

    private void CleanupGeoParquetScratchDirectory(string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(scratchDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            GeoParquetLog.CleanupScratchFailed(_logger, ex, scratchDir);
        }
    }

    /// <summary>
    /// Stream Shapefile features from extracted components on disk.
    /// </summary>
    private static async IAsyncEnumerable<IFeature> ReadShapefileStreamingAsync(
        string shapefilePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = new ShapefileReaderOptions
        {
            GeometryBuilderMode = GeometryBuilderMode.QuickFixInvalidShapes
        };

        using var reader = Shapefile.OpenRead(shapefilePath, options);
        var recordIndex = 0;

        while (reader.Read(out var deleted, out var feature))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (deleted || feature == null)
            {
                continue;
            }

            yield return feature;

            if (++recordIndex % 256 == 0)
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Stream GeoPackage features from a stream by using a temporary SQLite file.
    /// </summary>
    private async IAsyncEnumerable<IFeature> ReadGeoPackageStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GeoPackageScratch? scratch = null;
        var filePath = (stream as FileStream)?.Name;
        var ownsScratch = false;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            scratch = await PrepareGeoPackageScratchAsync(stream, cancellationToken);
            filePath = scratch.FilePath;
            ownsScratch = true;
        }

        try
        {
            await foreach (var feature in ReadGeoPackageStreamingAsync(filePath, cancellationToken))
            {
                yield return feature;
            }
        }
        finally
        {
            if (ownsScratch)
            {
                CleanupGeoPackageScratch(scratch);
            }
        }
    }

    private static async IAsyncEnumerable<IFeature> ReadGeoPackageStreamingAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var layers = await GetGeoPackageLayersAsync(filePath, cancellationToken);
        var layer = ResolveSingleGeoPackageImportLayer(layers);
        await foreach (var feature in ReadGeoPackageLayerAsync(filePath, layer, cancellationToken))
        {
            yield return feature;
        }
    }

    private static GeoPackageLayerInfo ResolveSingleGeoPackageImportLayer(IReadOnlyList<GeoPackageLayerInfo> layers)
    {
        if (layers.Count == 0)
        {
            throw new InvalidDataException("GeoPackage does not contain any feature layers.");
        }

        if (layers.Count > 1)
        {
            throw new InvalidDataException(BuildMultiLayerGeoPackageImportMessage(layers));
        }

        return layers[0];
    }

    private static string BuildMultiLayerGeoPackageImportMessage(IReadOnlyList<GeoPackageLayerInfo> layers)
    {
        var layerNames = string.Join(", ", layers.Select(layer => layer.TableName));
        return $"GeoPackage contains multiple feature layers ({layerNames}). Import requires a single-layer GeoPackage; preview AvailableLayers lists the source layers to export or split before import.";
    }

    private static async IAsyncEnumerable<IFeature> ReadGeoPackageLayerAsync(
        string filePath,
        GeoPackageLayerInfo layer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!GeoPackageTableNameRegex().IsMatch(layer.TableName))
        {
            throw new InvalidOperationException("GeoPackage contains table name with unsupported characters.");
        }

        await using var connection = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly;");
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(layer.TableName)}";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var geometryOrdinal = reader.GetOrdinal(layer.GeometryColumn);
        var geoReader = new GeoPackageGeoReader
        {
            HandleSRID = true,
            RepairRings = true
        };

        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            NtsGeometry? geometry = null;
            if (!reader.IsDBNull(geometryOrdinal))
            {
                var blob = reader.GetFieldValue<byte[]>(geometryOrdinal);
                geometry = geoReader.Read(blob);
                if (geometry != null && layer.Srid.HasValue && geometry.SRID <= 0)
                {
                    geometry.SRID = layer.Srid.Value;
                }
            }

            var attributes = new AttributesTable();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i == geometryOrdinal)
                {
                    continue;
                }

                var name = reader.GetName(i);
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                attributes.Add(name, value);
            }

            yield return new Feature(geometry, attributes);
        }
    }

    private static async Task<IReadOnlyList<GeoPackageLayerInfo>> GetGeoPackageLayersAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly;");
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT c.table_name, g.column_name, g.srs_id
            FROM gpkg_contents c
            JOIN gpkg_geometry_columns g ON c.table_name = g.table_name
            WHERE c.data_type = 'features'
            ORDER BY c.table_name
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var layers = new List<GeoPackageLayerInfo>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var tableName = reader.GetString(0);
            var geometryColumn = reader.GetString(1);
            var srid = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
            layers.Add(new GeoPackageLayerInfo(tableName, geometryColumn, NormalizeGeoPackageSrid(srid)));
        }

        return layers;
    }

    private static int? NormalizeGeoPackageSrid(int? srid)
    {
        if (!srid.HasValue)
        {
            return null;
        }

        return srid.Value <= 0 ? null : srid.Value;
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    #region Table Management

    private static string GetAllowedTableName(string tableName)
    {
        ValidateTableName(tableName);
        var sanitized = System.Text.RegularExpressions.Regex.Replace(tableName, @"[^a-zA-Z0-9_]", "_");
        return "imported_" + sanitized.ToLowerInvariant();
    }

    private static async Task CreateTableAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CreateImportTableSql, connection);
        command.Parameters.AddWithValue("table_name", tableName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AnalyzeTableAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var sql = $"ANALYZE {QuoteIdentifier(tableName)}";
        // codeql[cs/sql-injection] tableName is validated and quoted as an identifier.
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private Task<NpgsqlConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken)
        => _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken);

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

    #endregion
}

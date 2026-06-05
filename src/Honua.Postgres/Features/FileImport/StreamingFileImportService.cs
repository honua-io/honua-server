// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
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
    private readonly PostgresSchemaConfiguration _schemaConfiguration;

    private const string CreateImportTableSql = "SELECT honua.create_import_table(@schema_name, @table_name, @target_srid)";
    private const string InsertImportFeatureSql = "SELECT honua.insert_import_feature(@schema_name, @table_name, @wkb, @source_srid, @target_srid, @properties)";
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
        Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage? cloudStorage = null,
        PostgresSchemaConfiguration? schemaConfiguration = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsDetectionService = crsDetectionService ?? throw new ArgumentNullException(nameof(crsDetectionService));
        _formatDetectionService = formatDetectionService ?? throw new ArgumentNullException(nameof(formatDetectionService));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limits = limits ?? ImportLimits.Default;
        _geoJsonReader = new StreamingGeoJsonReader(_limits);
        _cloudStorage = cloudStorage;
        _schemaConfiguration = schemaConfiguration ?? new PostgresSchemaConfiguration(
            PostgresSchemaConfiguration.DefaultMetadataSchema,
            PostgresSchemaConfiguration.DefaultDataSchema,
            [PostgresSchemaConfiguration.DefaultDataSchema, "public"]);
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
                var prep = await PrepareGeoParquetImportAsync(
                    fileStream, jobId, request.TableName, warnings, cancellationToken);
                geoParquetScratch = prep.Scratch;
                warnings = prep.Warnings;
                if (prep.ErrorMessage != null)
                {
                    errorMessage = prep.ErrorMessage;
                    result = ImportResult.CreateFailure(
                        request.TableName, format.Value, errorMessage, stopwatch.Elapsed, warnings);
                    return result;
                }

                detectedSrid = prep.DetectedSrid;

                // If scratch created a new stream (non-seekable input), dispose the original
                if (geoParquetScratch!.ScratchDir != null && shouldDisposeStream)
                {
                    await fileStream.DisposeAsync();
                    shouldDisposeStream = false;
                }

                fileStream = prep.Stream!;
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
                var validationErrors = new[]
                {
                    ImportValidationIssue.Create(
                        ImportValidationErrorCodes.SourceSridRequired,
                        errorMessage,
                        field: nameof(request.SourceSrid))
                };
                result = ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    errorMessage,
                    stopwatch.Elapsed,
                    warnings,
                    ImportValidationErrorCodes.SourceSridRequired,
                    validationErrors);
                return result;
            }

            var sridValidationErrors = await ValidateImportSridsAsync(
                sourceSrid.Value,
                request.TargetSrid,
                cancellationToken);
            if (sridValidationErrors.Count > 0)
            {
                errorMessage = sridValidationErrors[0].Message;
                result = ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    errorMessage,
                    stopwatch.Elapsed,
                    warnings,
                    sridValidationErrors[0].Code,
                    sridValidationErrors);
                return result;
            }

            if (format.Value == SupportedFileFormat.GeoJson)
            {
                if (!fileStream.CanSeek)
                {
                    var seekableStream = await SpillToSeekableTempAsync(fileStream, cancellationToken);
                    if (shouldDisposeStream)
                    {
                        await fileStream.DisposeAsync();
                    }

                    fileStream = seekableStream;
                    shouldDisposeStream = true;
                    totalBytes = seekableStream.Length;
                    ImportLog.SpilledToTempFile(_logger, totalBytes.Value);
                }

                var geoJsonValidation = await _geoJsonReader.ValidateAsync(fileStream, cancellationToken);
                if (!geoJsonValidation.IsValid)
                {
                    var issue = geoJsonValidation.Issues[0];
                    errorMessage = issue.Message;
                    result = ImportResult.CreateFailure(
                        request.TableName,
                        format.Value,
                        errorMessage,
                        stopwatch.Elapsed,
                        warnings,
                        issue.Code,
                        geoJsonValidation.Issues);
                    return result;
                }
            }

            // Report initial progress
            progress?.Report(ImportProgress.CreateInitial(
                jobId,
                request.TableName,
                format.Value,
                fileStream.CanSeek ? fileStream.Length : null,
                request.FileName,
                request.SourceKind,
                request.SourceUrl,
                request.CloudFileId,
                request.UploadId,
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
        catch (Npgsql.PostgresException ex)
        {
            // Surface the underlying PostgreSQL server message (e.g. a missing staging
            // table or a constraint violation) instead of collapsing to the generic
            // "Import failed." MessageText is the server-supplied message and never
            // includes the connection string, SQL text, or a managed stack trace, so it
            // is safe to relay to the operator while remaining actionable.
            ImportLog.ImportFailedWithException(_logger, ex, jobId, request.TableName);
            errorMessage = $"Import failed: {ex.MessageText}";
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
}

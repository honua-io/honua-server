// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
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
    // PA-161: the batch-insert hot path (InsertBatchAsync, StreamingFileImportService.Batch.cs)
    // had no OTel span. Local ActivitySource named "Honua" for TracerProvider correlation,
    // matching the convention already used by PostgresAnomalyAnalyzer in this project.
    private static readonly ActivitySource _activitySource = new("Honua", "1.0.0");

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsDetectionService _crsDetectionService;
    private readonly IFileFormatDetectionService _formatDetectionService;
    private readonly ImportLimits _limits;
    private readonly StreamingGeoJsonReader _geoJsonReader;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<StreamingFileImportService> _logger;
    private readonly Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage? _cloudStorage;
    private readonly PostgresSchemaConfiguration _schemaConfiguration;
    private readonly IDatumTransformationCatalog? _datumTransformationCatalog;

    private const string CreateImportTableSql = "SELECT honua.create_import_table(@schema_name, @table_name, @target_srid)";
    private const string EnsureImportTableSql = "SELECT honua.ensure_import_table(@schema_name, @table_name, @target_srid)";
    private const string CreateImportStagingTableSql = "SELECT honua.create_import_staging_table(@schema_name, @table_name, @target_srid)";
    private const string SwapImportTableSql = "SELECT honua.swap_import_table(@schema_name, @table_name)";
    private const string EnsureImportUpsertKeySql = "SELECT honua.ensure_import_upsert_key(@schema_name, @table_name, @key_columns)";
    private const string InsertImportFeatureSql = "SELECT honua.insert_import_feature(@schema_name, @table_name, @wkb, @source_srid, @target_srid, @properties)";
    private const string InsertImportFeatureWithDatumSql = "SELECT honua.insert_import_feature(@schema_name, @table_name, @wkb, @source_srid, @target_srid, @properties, @datum_pipeline)";
    private const string UpsertImportFeatureSql = "SELECT honua.upsert_import_feature(@schema_name, @table_name, @wkb, @source_srid, @target_srid, @properties, @key_columns)";
    private const string BulkUpsertImportFeaturesSql = "SELECT processed_count FROM honua.bulk_upsert_import_features(@schema_name, @table_name, @wkbs, @source_srids, @target_srid, @properties, @key_columns, @datum_source_srid, @datum_pipeline)";
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
    private static readonly CompositeFormat _csvUnparseableGeometryWarningFormat =
        CompositeFormat.Parse("{0} row(s) had a geometry value that could not be parsed as WKT, EWKT, or WKB; each raw value was preserved as an attribute and the row was imported without geometry.");
    private static readonly CompositeFormat _wktUnparseableGeometryWarningFormat =
        CompositeFormat.Parse("{0} record(s) could not be parsed as WKT, EWKT, or WKB and were skipped.");
    private static readonly CompositeFormat _csvGeocodeFailureWarningFormat =
        CompositeFormat.Parse("{0} row(s) had an address that could not be geocoded; each row was imported without geometry.");
    private static readonly CompositeFormat _repairedGeometryWarningFormat =
        CompositeFormat.Parse("{0} feature(s) had invalid geometry that was automatically repaired (ST_MakeValid-equivalent) before import.");
    private static readonly CompositeFormat _skippedInvalidGeometryWarningFormat =
        CompositeFormat.Parse("{0} feature(s) were skipped because their geometry was invalid or exceeded geometry size limits (SkipInvalidGeometry is enabled).");
    private static readonly Regex _wktSridRegex = new(
        @"SRID\s*=\s*(\d+)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]{0,128}$")]
    private static partial Regex GeoPackageTableNameRegex();

    public StreamingFileImportService(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ICrsDetectionService crsDetectionService,
        IFileFormatDetectionService formatDetectionService,
        IPerformanceMonitor performanceMonitor,
        ILogger<StreamingFileImportService> logger,
        ImportLimits? limits = null,
        Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage? cloudStorage = null,
        PostgresSchemaConfiguration? schemaConfiguration = null,
        IDatumTransformationCatalog? datumTransformationCatalog = null)
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
        _datumTransformationCatalog = datumTransformationCatalog;
    }

    /// <summary>
    /// Resolves the Esri-default datum-transformation PROJ pipeline for the
    /// <paramref name="sourceSrid"/> -&gt; <paramref name="targetSrid"/> import reprojection,
    /// so the import path honors the same auditable geotransformation the query path uses
    /// (#1501). Returns <see langword="null"/> when no transform is needed (equal SRIDs),
    /// no catalog is wired, or the catalog has no curated default for the pair — in every
    /// such case the import keeps PROJ's default (2-argument <c>ST_Transform</c>) behavior.
    /// </summary>
    /// <remarks>
    /// Only forward selections are applied. The catalog synthesizes reverse directions with
    /// <see cref="DatumTransformationSelection.TransformForward"/> set to <see langword="false"/> but
    /// keeps the forward <see cref="DatumTransformationSelection.ProjPipeline"/>; applying that forward
    /// pipeline to reverse-direction input (e.g. a NAD27→NAD83 NADCON shift on NAD83 coordinates) would
    /// corrupt the result. Until inverse pipelines are emitted, reverse-direction imports fall back to
    /// PROJ's default path rather than the (wrong-way) explicit pipeline.
    /// </remarks>
    private string? ResolveImportDatumPipeline(int sourceSrid, int targetSrid)
    {
        if (sourceSrid == targetSrid || _datumTransformationCatalog is null)
        {
            return null;
        }

        if (_datumTransformationCatalog.TryGetDefault(sourceSrid, targetSrid, out var selection)
            && selection.TransformForward
            && selection.ProjPipeline is { Length: > 0 } pipeline)
        {
            return pipeline;
        }

        return null;
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
                catch (InvalidDataException)
                {
                    errorMessage = "Invalid GeoPackage import request.";
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
                detectedSrid = await _crsDetectionService.DetectFromShapefilePrjAsync(shapefileScratch.ShpPath, cancellationToken);
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

            // Non-fatal preflight issues (e.g. GeoJSON geometries that will be repaired by the
            // shared validity gate on import, #2743). Relayed on the successful result alongside the
            // per-row issues so callers still see them without the whole file being rejected.
            ImportValidationIssue[] preflightWarnings = [];
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
                    var issue = geoJsonValidation.Issues.First(
                        i => i.Severity == ImportValidationSeverity.Error);
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

                preflightWarnings = geoJsonValidation.Issues
                    .Where(i => i.Severity == ImportValidationSeverity.Warning)
                    .ToArray();
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
            IReadOnlyList<ImportValidationIssue> rowIssues;
            int repairedCount;
            (importedCount, failedCount, repairedCount, warnings, rowIssues) = await ImportStreamingAsync(
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
                warnings,
                physicalTableName: GetAllowedTableName(request.TableName),
                schema: ResolveTargetSchema(request.TargetSchema),
                repairedGeometryCount: repairedCount) with
            {
                // Per-row issues (e.g. CSV address rows that failed to geocode, or GeoJSON
                // geometries repaired by the validity gate) that did not block the import but should
                // be relayed to the caller alongside the warnings.
                ValidationErrors = preflightWarnings.Length == 0
                    ? rowIssues
                    : [.. preflightWarnings, .. rowIssues]
            };
            return result;
        }
        catch (CsvImportOptionsException ex)
        {
            // The caller-supplied CSV options could not be applied (missing/conflicting
            // columns or a geocoded-row cap overrun). The message is composed from the
            // caller's own option values and CSV header names, so it is client-safe.
            ImportLog.ImportFailedWithException(_logger, ex, jobId, request.TableName);
            errorMessage = ex.Message;
            result = ImportResult.CreateFailure(
                request.TableName,
                format ?? SupportedFileFormat.Csv,
                errorMessage,
                stopwatch.Elapsed,
                warnings,
                ImportValidationErrorCodes.CsvOptionsInvalid,
                [ImportValidationIssue.Create(ImportValidationErrorCodes.CsvOptionsInvalid, errorMessage)]);
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
        catch (ImportGeometryTooLargeException ex)
        {
            // A single feature exceeded the geometry size guard and the import is configured to
            // fail rather than skip (#1626). Surface a clear, machine-readable 413-style error with
            // remediation guidance instead of letting the oversized geometry crash the host.
            ImportLog.ImportFailedWithException(_logger, ex, jobId, request.TableName);
            errorMessage = SanitizeImportValidationMessage(ex);
            var geometryTooLargeIssues = new[]
            {
                ImportValidationIssue.Create(ImportValidationErrorCodes.GeometryTooLarge, errorMessage)
            };
            result = ImportResult.CreateFailure(
                request.TableName,
                format ?? SupportedFileFormat.GeoJson,
                errorMessage,
                stopwatch.Elapsed,
                warnings,
                ImportValidationErrorCodes.GeometryTooLarge,
                geometryTooLargeIssues);
            return result;
        }
        catch (InvalidDataException ex)
        {
            // Preserve the specific message (e.g. "Row X in row group Y contains
            // invalid WKB geometry data") after stripping unsafe environment details.
            ImportLog.ImportFailedWithException(_logger, ex, jobId, request.TableName);
            errorMessage = SanitizeInvalidDataMessage(ex);
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
            // Keep the full server message (which can include relation/function/schema/
            // constraint names) in the server log, but never relay it to the client. The
            // client-facing message maps the standardized SQLSTATE to a short, friendly
            // explanation and includes the SQL state code so operators can correlate
            // without leaking provider internals.
            ImportLog.ImportFailedWithException(_logger, ex, jobId, request.TableName);
            errorMessage = $"Import failed: {DescribePostgresError(ex.SqlState)} (SQL state {ex.SqlState}).";
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

    private static string DescribePostgresError(string? sqlState) => sqlState switch
    {
        "42P01" => "a required staging table was not available",
        "42883" => "a required database function was not available",
        "23505" => "a record with a conflicting key already exists",
        "23502" => "a required value was missing",
        "23503" => "a referenced record was missing",
        "22P02" or "22P04" or "22023" => "the data could not be parsed for the target columns",
        _ => "an unexpected database error occurred",
    };

    private static string SanitizeInvalidDataMessage(InvalidDataException exception)
        => SanitizeImportValidationMessage(exception);

    private static string SanitizeImportValidationMessage(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "Import file is invalid."
            : exception.Message;

        message = ImportErrorUrlRegex().Replace(message, "[redacted-url]");
        message = ImportErrorAbsolutePathRegex().Replace(message, "[redacted-path]");
        return message.Length <= 512 ? message : string.Concat(message.AsSpan(0, 512), "...");
    }

    [GeneratedRegex(@"https?://[^\s""'<>)]*", RegexOptions.IgnoreCase)]
    private static partial Regex ImportErrorUrlRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:[A-Za-z]:\\|/)[^\s""'<>)]*", RegexOptions.IgnoreCase)]
    private static partial Regex ImportErrorAbsolutePathRegex();
}

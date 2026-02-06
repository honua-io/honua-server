// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.IO.Esri;
using NetTopologySuite.IO.Esri.Shapefiles.Readers;
using Npgsql;
using NpgsqlTypes;
using Coordinate = NetTopologySuite.Geometries.Coordinate;
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
    private readonly ImportLimits _limits;
    private readonly StreamingGeoJsonReader _geoJsonReader;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<StreamingFileImportService> _logger;
    private readonly Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage? _cloudStorage;

    private const string CreateImportTableSql = "SELECT honua.create_import_table(@table_name)";
    private const string InsertImportFeatureSql = "SELECT honua.insert_import_feature(@table_name, @wkb, @source_srid, @target_srid, @properties)";
    private const int CrsDetectionHeaderSize = 8192;

    /// <summary>
    /// Supported file extensions mapped to formats
    /// </summary>
    private static readonly FrozenDictionary<string, SupportedFileFormat> _fileExtensions =
        new Dictionary<string, SupportedFileFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".geojson"] = SupportedFileFormat.GeoJson,
            [".json"] = SupportedFileFormat.GeoJson,
            [".kml"] = SupportedFileFormat.Kml,
            [".wkt"] = SupportedFileFormat.Wkt,
            [".zip"] = SupportedFileFormat.Shapefile,
            [".gpkg"] = SupportedFileFormat.GeoPackage,
            [".gpx"] = SupportedFileFormat.Gpx
        }
        .ToFrozenDictionary();
    private static readonly FrozenSet<string> _shapefileComponentExtensions = new[]
        {
            ".shp", ".dbf", ".shx", ".prj", ".cpg"
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly string _shapefileScratchRoot = Path.Combine(Path.GetTempPath(), "honua-shapefile");
    private static readonly string _geoPackageScratchRoot = Path.Combine(Path.GetTempPath(), "honua-geopackage");
    private static readonly Regex _wktSridRegex = new(
        @"SRID\s*=\s*(\d+)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public StreamingFileImportService(
        IDatabaseConnectionProvider connectionProvider,
        ICrsDetectionService crsDetectionService,
        IPerformanceMonitor performanceMonitor,
        ILogger<StreamingFileImportService> logger,
        ImportLimits? limits = null,
        Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage? cloudStorage = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsDetectionService = crsDetectionService ?? throw new ArgumentNullException(nameof(crsDetectionService));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limits = limits ?? ImportLimits.Default;
        _geoJsonReader = new StreamingGeoJsonReader(_limits);
        _cloudStorage = cloudStorage;
    }

    /// <inheritdoc/>
    public ImportLimits Limits => _limits;

    /// <inheritdoc/>
    public SupportedFileFormat? DetectFormat(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(extension) ? null :
               _fileExtensions.TryGetValue(extension, out var format) ? format : null;
    }

    /// <inheritdoc/>
    public string[] GetSupportedExtensions() => _fileExtensions.Keys.ToArray();

    /// <inheritdoc/>
    public Task<ImportResult> ImportFileAsync(ImportRequest request, CancellationToken cancellationToken = default)
        => ImportFileAsync(request, null, cancellationToken);

    /// <inheritdoc/>
    public async Task<ImportResult> ImportFileAsync(
        ImportRequest request,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        // Validate request has either FileStream or CloudFileId
        request.Validate();

        var stopwatch = Stopwatch.StartNew();
        var format = DetectFormat(request.FileName);
        var formatName = format?.ToString() ?? "unknown";
        var mode = progress == null ? "sync" : "background";
        var jobId = Guid.NewGuid().ToString("N")[..8];

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
                    stopwatch.Elapsed);
            }

            fileStream = downloadStream;
            shouldDisposeStream = true;
            totalBytes = metadata?.SizeBytes;
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
                    stopwatch.Elapsed);
                return result;
            }

            int? detectedSrid;
            if (format.Value == SupportedFileFormat.GeoPackage)
            {
                geoPackageScratch = await PrepareGeoPackageScratchAsync(fileStream, cancellationToken);
                var layers = await GetGeoPackageLayersAsync(geoPackageScratch.FilePath, cancellationToken);
                if (layers.Count == 0)
                {
                    errorMessage = "GeoPackage does not contain any feature layers.";
                    result = ImportResult.CreateFailure(
                        request.TableName,
                        format.Value,
                        errorMessage,
                        stopwatch.Elapsed);
                    return result;
                }

                var layer = layers[0];
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
                        stopwatch.Elapsed);
                    return result;
                }

                shapefileScratch = await PrepareShapefileScratchAsync(fileStream, request.FileName, cancellationToken);
                detectedSrid = await _crsDetectionService.DetectFromShapefilePrjAsync(shapefileScratch.ShpPath);
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

            var sourceSrid = request.SourceSrid ?? detectedSrid;
            if (!sourceSrid.HasValue)
            {
                errorMessage = $"Source SRID is required for {format.Value} imports when CRS cannot be detected.";
                result = ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    errorMessage,
                    stopwatch.Elapsed);
                return result;
            }

            // Report initial progress
            progress?.Report(ImportProgress.CreateInitial(
                jobId,
                request.TableName,
                format.Value,
                fileStream.CanSeek ? fileStream.Length : null));

            // Stream features and insert in batches
            (importedCount, failedCount) = await ImportStreamingAsync(
                request,
                fileStream,
                format.Value,
                sourceSrid.Value,
                progress,
                jobId,
                cancellationToken,
                shapefileScratch);

            if (importedCount == 0 && failedCount == 0)
            {
                errorMessage = "No features found in file";
                result = ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    errorMessage,
                    stopwatch.Elapsed);
                return result;
            }

            status = "success";
            result = ImportResult.CreateSuccess(
                request.TableName,
                format.Value,
                importedCount,
                detectedSrid,
                stopwatch.Elapsed);
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
                stopwatch.Elapsed);
            return result;
        }
        catch (Exception)
        {
            errorMessage = "Import failed.";
            result = ImportResult.CreateFailure(
                request.TableName,
                format ?? SupportedFileFormat.GeoJson,
                errorMessage,
                stopwatch.Elapsed);
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

            CleanupShapefileScratch(shapefileScratch);
            CleanupGeoPackageScratch(geoPackageScratch);
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
    private async Task<(int imported, int failed)> ImportStreamingAsync(
        ImportRequest request,
        Stream fileStream,
        SupportedFileFormat format,
        int sourceSrid,
        IProgress<ImportProgress>? progress,
        string jobId,
        CancellationToken cancellationToken,
        ShapefileScratch? shapefileScratch = null)
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
            SupportedFileFormat.Wkt => ReadWktStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Kml => ReadKmlStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Gpx => ReadGpxStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(shapefileScratch!.ShpPath, cancellationToken),
            SupportedFileFormat.GeoPackage => ReadGeoPackageStreamingAsync(fileStream, cancellationToken),
            _ => throw new NotSupportedException($"Streaming not supported for format: {format}")
        };

        await foreach (var feature in featureStream.WithCancellation(cancellationToken))
        {
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
                    TotalBytes = fileStream.CanSeek ? fileStream.Length : null
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
            TotalBytes = fileStream.CanSeek ? fileStream.Length : null
        });

        return (totalImported, totalFailed);
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

        await using var command = new NpgsqlCommand(InsertImportFeatureSql, connection)
        {
            Transaction = transaction
        };
        command.Parameters.Add("table_name", NpgsqlDbType.Text).Value = tableName;
        var wkbParameter = command.Parameters.Add("wkb", NpgsqlDbType.Bytea);
        command.Parameters.Add("source_srid", NpgsqlDbType.Integer).Value = sourceSrid;
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        var propertiesParameter = command.Parameters.Add("properties", NpgsqlDbType.Jsonb);

        try
        {
            foreach (var feature in features)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    wkbParameter.Value = CreateWkb(feature, wkbWriter) ?? (object)DBNull.Value;
                    propertiesParameter.Value = BuildPropertiesJson(feature);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    imported++;
                }
                catch (Exception)
                {
                    failed++;
                    if (!_limits.ContinueOnError)
                    {
                        throw;
                    }
                }
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
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
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

        var wkb = wkbWriter.Write(feature.Geometry);

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

            // Stream features but only collect up to the limit
            var features = new List<IFeature>();
            var featureStream = format.Value switch
            {
                SupportedFileFormat.GeoJson => _geoJsonReader.ReadFeaturesAsync(fileStream, cancellationToken),
                SupportedFileFormat.Wkt => ReadWktStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Kml => ReadKmlStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Gpx => ReadGpxStreamingAsync(fileStream, cancellationToken),
                SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(shapefileScratch!.ShpPath, cancellationToken),
                SupportedFileFormat.GeoPackage => ReadGeoPackageStreamingAsync(geoPackageScratch!.FilePath, cancellationToken),
                _ => throw new NotSupportedException($"Preview not supported for format: {format}")
            };

            await foreach (var feature in featureStream.WithCancellation(cancellationToken))
            {
                features.Add(feature);
                if (features.Count >= _limits.MaxPreviewFeatures)
                    break;
            }

            var sampleProperties = new Dictionary<string, object?>();
            var firstFeature = features.FirstOrDefault();
            if (firstFeature?.Attributes is not null)
            {
                var names = firstFeature.Attributes.GetNames();
                var values = firstFeature.Attributes.GetValues();
                sampleProperties = names.Zip(values).ToDictionary(pair => pair.First, pair => (object?)pair.Second);
            }

            return new FilePreview
            {
                Format = format.Value,
                TotalFeatureCount = features.Count,
                DetectedSrid = detectedSrid,
                SampleProperties = sampleProperties,
                AvailableLayers = availableLayers
            };
        }
        finally
        {
            CleanupShapefileScratch(shapefileScratch);
            CleanupGeoPackageScratch(geoPackageScratch);
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
                SupportedFileFormat.Wkt => await DetectWktSridAsync(stream, cancellationToken),
                SupportedFileFormat.GeoPackage => await DetectGeoPackageSridAsync(stream, cancellationToken),
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

    private async Task<int?> DetectGeoJsonSridAsync(Stream stream, CancellationToken cancellationToken)
    {
        var detected = await _geoJsonReader.DetectCrsAsync(stream, cancellationToken);
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
            SupportedFileFormat.Wkt => await DetectWktSridAsync(headerStream, cancellationToken),
            _ => null
        };
    }

    private async Task<int?> DetectWktSridAsync(Stream stream, CancellationToken cancellationToken)
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

    private sealed record GeoPackageLayerInfo(string TableName, string GeometryColumn, int? Srid);

    private sealed class PrefixedReadStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _prefix;
        private readonly Stream _inner;
        private int _prefixOffset;

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
            get => throw new NotSupportedException();
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

            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = ReadPrefix(buffer.AsSpan(offset, count));
            if (bytesRead < count)
            {
                bytesRead += await _inner.ReadAsync(buffer.AsMemory(offset + bytesRead, count - bytesRead), cancellationToken);
            }

            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = ReadPrefix(buffer.Span);
            if (bytesRead < buffer.Length)
            {
                bytesRead += await _inner.ReadAsync(buffer[bytesRead..], cancellationToken);
            }

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

    private static bool IsZipFileName(string fileName)
        => string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);

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

            var shpPath = Path.Combine(scratchDir, entries.BaseName + ".shp");
            var dbfPath = Path.Combine(scratchDir, entries.BaseName + ".dbf");

            await ExtractEntryAsync(entries.ShpEntry, shpPath, cancellationToken);
            await ExtractEntryAsync(entries.DbfEntry, dbfPath, cancellationToken);

            if (entries.ShxEntry != null)
            {
                await ExtractEntryAsync(entries.ShxEntry, Path.Combine(scratchDir, entries.BaseName + ".shx"), cancellationToken);
            }

            if (entries.PrjEntry != null)
            {
                await ExtractEntryAsync(entries.PrjEntry, Path.Combine(scratchDir, entries.BaseName + ".prj"), cancellationToken);
            }

            if (entries.CpgEntry != null)
            {
                await ExtractEntryAsync(entries.CpgEntry, Path.Combine(scratchDir, entries.BaseName + ".cpg"), cancellationToken);
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

            if (!groups.TryGetValue(baseName, out var group))
            {
                group = new ShapefileEntryGroup(baseName);
                groups.Add(baseName, group);
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

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var entryStream = entry.Open();
        await using var outputStream = File.Create(destinationPath);
        await entryStream.CopyToAsync(outputStream, cancellationToken);
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

    #region Streaming Readers for Other Formats

    /// <summary>
    /// Stream WKT features line by line.
    /// </summary>
    private async IAsyncEnumerable<IFeature> ReadWktStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var wktReader = new WKTReader();
        using var reader = new StreamReader(stream, leaveOpen: true);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            IFeature? feature = null;
            try
            {
                var geometry = wktReader.Read(line.Trim());
                if (geometry != null)
                {
                    feature = new Feature(geometry, new AttributesTable());
                }
            }
            catch
            {
                // Skip invalid WKT lines
            }

            if (feature != null)
                yield return feature;
        }
    }

    /// <summary>
    /// Stream KML features using XmlReader for memory efficiency.
    /// </summary>
    private async IAsyncEnumerable<IFeature> ReadKmlStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true
        };

        using var reader = XmlReader.Create(stream, settings);
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Placemark")
            {
                var feature = await ParseKmlPlacemarkAsync(reader, geometryFactory, cancellationToken);
                if (feature != null)
                    yield return feature;
            }
        }
    }

    private static async Task<IFeature?> ParseKmlPlacemarkAsync(
        XmlReader reader,
        GeometryFactory geometryFactory,
        CancellationToken cancellationToken)
    {
        var attributes = new AttributesTable();
        NtsGeometry? geometry = null;
        var depth = reader.Depth;

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Placemark")
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "name":
                        var name = await reader.ReadElementContentAsStringAsync();
                        attributes.Add("name", name);
                        break;
                    case "description":
                        var desc = await reader.ReadElementContentAsStringAsync();
                        attributes.Add("description", desc);
                        break;
                    case "Point":
                        geometry = await ParseKmlPointAsync(reader, geometryFactory, cancellationToken);
                        break;
                    case "LineString":
                        geometry = await ParseKmlLineStringAsync(reader, geometryFactory, cancellationToken);
                        break;
                    case "Polygon":
                        geometry = await ParseKmlPolygonAsync(reader, geometryFactory, cancellationToken);
                        break;
                }
            }
        }

        return new Feature(geometry, attributes);
    }

    private static async Task<NtsGeometry?> ParseKmlPointAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Point")
                break;

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "coordinates")
            {
                var coords = await reader.ReadElementContentAsStringAsync();
                var parts = coords.Trim().Split(',');
                if (parts.Length >= 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
                {
                    return factory.CreatePoint(new Coordinate(lon, lat));
                }
            }
        }
        return null;
    }

    private static async Task<NtsGeometry?> ParseKmlLineStringAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "LineString")
                break;

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "coordinates")
            {
                var coords = await reader.ReadElementContentAsStringAsync();
                var coordinates = ParseKmlCoordinates(coords);
                if (coordinates.Length >= 2)
                    return factory.CreateLineString(coordinates);
            }
        }
        return null;
    }

    private static async Task<NtsGeometry?> ParseKmlPolygonAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        LinearRing? outerRing = null;
        var innerRings = new List<LinearRing>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Polygon")
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "outerBoundaryIs")
                {
                    outerRing = await ParseKmlBoundaryAsync(reader, factory, "outerBoundaryIs", cancellationToken);
                }
                else if (reader.LocalName == "innerBoundaryIs")
                {
                    var ring = await ParseKmlBoundaryAsync(reader, factory, "innerBoundaryIs", cancellationToken);
                    if (ring != null)
                        innerRings.Add(ring);
                }
            }
        }

        if (outerRing != null)
            return factory.CreatePolygon(outerRing, innerRings.ToArray());

        return null;
    }

    private static async Task<LinearRing?> ParseKmlBoundaryAsync(
        XmlReader reader,
        GeometryFactory factory,
        string boundaryName,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == boundaryName)
                break;

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "coordinates")
            {
                var coords = await reader.ReadElementContentAsStringAsync();
                var coordinates = ParseKmlCoordinates(coords);
                if (coordinates.Length >= 4)
                    return factory.CreateLinearRing(coordinates);
            }
        }
        return null;
    }

    private static readonly char[] _kmlCoordinateSeparators = { ' ', '\n', '\r', '\t' };

    private static Coordinate[] ParseKmlCoordinates(string coordsString)
    {
        var coords = new List<Coordinate>();
        var parts = coordsString.Trim().Split(_kmlCoordinateSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var components = part.Split(',');
            if (components.Length >= 2 &&
                double.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                double.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            {
                coords.Add(new Coordinate(lon, lat));
            }
        }

        return coords.ToArray();
    }

    /// <summary>
    /// Stream GPX features using XmlReader for memory efficiency.
    /// </summary>
    private async IAsyncEnumerable<IFeature> ReadGpxStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true
        };

        using var reader = XmlReader.Create(stream, settings);
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element)
            {
                IFeature? feature = null;
                switch (reader.LocalName)
                {
                    case "wpt":
                        feature = await ParseGpxWaypointAsync(reader, geometryFactory, cancellationToken);
                        break;
                    case "trk":
                        feature = await ParseGpxTrackAsync(reader, geometryFactory, cancellationToken);
                        break;
                    case "rte":
                        feature = await ParseGpxRouteAsync(reader, geometryFactory, cancellationToken);
                        break;
                }

                if (feature != null)
                    yield return feature;
            }
        }
    }

    private static async Task<IFeature?> ParseGpxWaypointAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        var lat = reader.GetAttribute("lat");
        var lon = reader.GetAttribute("lon");

        if (lat == null || lon == null ||
            !double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
            !double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            return null;

        var attributes = new AttributesTable();
        var geometry = factory.CreatePoint(new Coordinate(longitude, latitude));

        using (var subtree = reader.ReadSubtree())
        {
            await subtree.ReadAsync();
            while (await subtree.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (subtree.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var name = subtree.LocalName;
                if (subtree.IsEmptyElement)
                {
                    attributes.Add(name, string.Empty);
                    continue;
                }

                var value = await subtree.ReadElementContentAsStringAsync();
                attributes.Add(name, value);
            }
        }

        return new Feature(geometry, attributes);
    }

    private static async Task<IFeature?> ParseGpxTrackAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        var attributes = new AttributesTable();
        var allCoordinates = new List<Coordinate>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "trk")
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "name")
                {
                    attributes.Add("name", await reader.ReadElementContentAsStringAsync());
                }
                else if (reader.LocalName == "trkpt")
                {
                    var lat = reader.GetAttribute("lat");
                    var lon = reader.GetAttribute("lon");
                    if (lat != null && lon != null &&
                        double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) &&
                        double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
                    {
                        allCoordinates.Add(new Coordinate(longitude, latitude));
                    }
                }
            }
        }

        if (allCoordinates.Count >= 2)
        {
            var geometry = factory.CreateLineString(allCoordinates.ToArray());
            return new Feature(geometry, attributes);
        }

        return null;
    }

    private static async Task<IFeature?> ParseGpxRouteAsync(
        XmlReader reader,
        GeometryFactory factory,
        CancellationToken cancellationToken)
    {
        var attributes = new AttributesTable();
        var coordinates = new List<Coordinate>();

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "rte")
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "name")
                {
                    attributes.Add("name", await reader.ReadElementContentAsStringAsync());
                }
                else if (reader.LocalName == "rtept")
                {
                    var lat = reader.GetAttribute("lat");
                    var lon = reader.GetAttribute("lon");
                    if (lat != null && lon != null &&
                        double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) &&
                        double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
                    {
                        coordinates.Add(new Coordinate(longitude, latitude));
                    }
                }
            }
        }

        if (coordinates.Count >= 2)
        {
            var geometry = factory.CreateLineString(coordinates.ToArray());
            return new Feature(geometry, attributes);
        }

        return null;
    }

    /// <summary>
    /// Stream Shapefile features from extracted components on disk.
    /// </summary>
    private async IAsyncEnumerable<IFeature> ReadShapefileStreamingAsync(
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

    private async IAsyncEnumerable<IFeature> ReadGeoPackageStreamingAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var layers = await GetGeoPackageLayersAsync(filePath, cancellationToken);
        if (layers.Count == 0)
        {
            throw new InvalidDataException("GeoPackage does not contain any feature layers.");
        }

        var layer = layers[0];
        await foreach (var feature in ReadGeoPackageLayerAsync(filePath, layer, cancellationToken))
        {
            yield return feature;
        }
    }

    private async IAsyncEnumerable<IFeature> ReadGeoPackageLayerAsync(
        string filePath,
        GeoPackageLayerInfo layer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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

    #endregion

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

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is NpgsqlConnection npgsqlConnection)
        {
            return npgsqlConnection;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException("Expected NpgsqlConnection for streaming import.");
    }

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

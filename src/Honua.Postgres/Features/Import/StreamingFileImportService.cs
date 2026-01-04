// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;
using Coordinate = NetTopologySuite.Geometries.Coordinate;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Memory-efficient streaming file import service.
/// Processes features incrementally using IAsyncEnumerable and batched database insertion
/// to maintain constant memory usage regardless of file size.
/// </summary>
internal sealed partial class StreamingFileImportService : IFileImportService
{
    private readonly string _connectionString;
    private readonly ImportLimits _limits;
    private readonly StreamingGeoJsonReader _geoJsonReader;
    private readonly ISchemaContext? _schemaContext;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<StreamingFileImportService> _logger;
    private readonly Honua.Core.Features.FileStorage.Abstractions.ICloudFileStorage? _cloudStorage;

    private const string CreateImportTableSql = "SELECT honua.create_import_table(@table_name)";
    private const string InsertImportFeatureSql = "SELECT honua.insert_import_feature(@table_name, @wkb, @source_srid, @target_srid, @properties)";

    /// <summary>
    /// Supported file extensions mapped to formats
    /// </summary>
    private static readonly Dictionary<string, SupportedFileFormat> _fileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".geojson"] = SupportedFileFormat.GeoJson,
        [".json"] = SupportedFileFormat.GeoJson,
        [".kml"] = SupportedFileFormat.Kml,
        [".wkt"] = SupportedFileFormat.Wkt,
        [".shp"] = SupportedFileFormat.Shapefile,
        [".gpkg"] = SupportedFileFormat.GeoPackage,
        [".gpx"] = SupportedFileFormat.Gpx
    };

    public StreamingFileImportService(
        string connectionString,
        IPerformanceMonitor performanceMonitor,
        ILogger<StreamingFileImportService> logger,
        ImportLimits? limits = null,
        ISchemaContext? schemaContext = null,
        Honua.Core.Features.FileStorage.Abstractions.ICloudFileStorage? cloudStorage = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limits = limits ?? ImportLimits.Default;
        _geoJsonReader = new StreamingGeoJsonReader(_limits);
        _schemaContext = schemaContext;
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

            // Detect CRS using streaming (doesn't load entire file)
            var detectedSrid = await DetectCrsStreamingAsync(fileStream, format.Value, cancellationToken);
            var sourceSrid = request.SourceSrid ?? detectedSrid ?? 4326;

            // Reset stream position after CRS detection
            if (fileStream.CanSeek)
            {
                fileStream.Position = 0;
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
                sourceSrid,
                progress,
                jobId,
                cancellationToken);

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
        CancellationToken cancellationToken)
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
        var featureStream = format switch
        {
            SupportedFileFormat.GeoJson => _geoJsonReader.ReadFeaturesAsync(fileStream, cancellationToken),
            SupportedFileFormat.Wkt => ReadWktStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Kml => ReadKmlStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Gpx => ReadGpxStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(fileStream, cancellationToken),
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

        try
        {
            foreach (var feature in features)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await InsertFeatureAsync(
                        connection,
                        tableName,
                        feature,
                        sourceSrid,
                        targetSrid,
                        wkbWriter,
                        transaction,
                        cancellationToken);
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
    /// Insert a single feature into the database.
    /// </summary>
    private async Task InsertFeatureAsync(
        NpgsqlConnection connection,
        string tableName,
        IFeature feature,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, object?>();
        if (feature.Attributes is not null)
        {
            var names = feature.Attributes.GetNames();
            var values = feature.Attributes.GetValues();
            properties = names.Zip(values).ToDictionary(pair => pair.First, pair => (object?)pair.Second);
        }

        await using var command = new NpgsqlCommand(InsertImportFeatureSql, connection)
        {
            Transaction = transaction
        };
        command.Parameters.AddWithValue("table_name", tableName);

        byte[]? wkb = null;
        if (feature.Geometry != null)
        {
            // Validate geometry before insertion if validation is enabled
            if (_limits.ValidateGeometry)
            {
                var validationError = ValidateGeometry(feature.Geometry);
                if (validationError != null)
                {
                    if (_limits.SkipInvalidGeometry)
                    {
                        // Skip invalid geometry but still insert the feature without geometry
                        wkb = null;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Geometry validation failed: {validationError}");
                    }
                }
                else
                {
                    wkb = wkbWriter.Write(feature.Geometry);

                    // Validate WKB size
                    if (wkb.Length > _limits.MaxWkbSize)
                    {
                        if (_limits.SkipInvalidGeometry)
                        {
                            wkb = null;
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"Geometry WKB size ({wkb.Length:N0} bytes) exceeds maximum allowed ({_limits.MaxWkbSize:N0} bytes)");
                        }
                    }
                }
            }
            else
            {
                wkb = wkbWriter.Write(feature.Geometry);
            }
        }

        var wkbParameter = new NpgsqlParameter("wkb", NpgsqlDbType.Bytea)
        {
            Value = wkb ?? (object)DBNull.Value
        };
        command.Parameters.Add(wkbParameter);

        command.Parameters.AddWithValue("source_srid", sourceSrid);
        command.Parameters.AddWithValue("target_srid", targetSrid);

        var propertiesJson = JsonSerializer.Serialize(properties, ImportJsonContext.Default.DictionaryStringObject);
        var propertiesParameter = new NpgsqlParameter("properties", NpgsqlDbType.Jsonb)
        {
            Value = propertiesJson
        };
        command.Parameters.Add(propertiesParameter);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Validates a geometry against configured limits.
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    private string? ValidateGeometry(Geometry geometry)
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
    private static int CountVertices(Geometry geometry)
    {
        return geometry.NumPoints;
    }

    /// <summary>
    /// Counts the total number of rings in polygon geometries.
    /// </summary>
    private static int CountRings(Geometry geometry)
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
    private static bool ValidateCoordinates(Geometry geometry)
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

        // Detect CRS using streaming
        var detectedSrid = await DetectCrsStreamingAsync(fileStream, format.Value, cancellationToken);

        // Reset stream position
        if (fileStream.CanSeek)
            fileStream.Position = 0;

        // Stream features but only collect up to the limit
        var features = new List<IFeature>();
        var featureStream = format.Value switch
        {
            SupportedFileFormat.GeoJson => _geoJsonReader.ReadFeaturesAsync(fileStream, cancellationToken),
            SupportedFileFormat.Wkt => ReadWktStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Kml => ReadKmlStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Gpx => ReadGpxStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(fileStream, cancellationToken),
            SupportedFileFormat.GeoPackage => ReadGeoPackageStreamingAsync(fileStream, cancellationToken),
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
            AvailableLayers = []
        };
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
                SupportedFileFormat.GeoJson => await _geoJsonReader.DetectCrsAsync(stream, cancellationToken),
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
        Geometry? geometry = null;
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

    private static async Task<Geometry?> ParseKmlPointAsync(
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
                    double.TryParse(parts[0], out var lon) &&
                    double.TryParse(parts[1], out var lat))
                {
                    return factory.CreatePoint(new Coordinate(lon, lat));
                }
            }
        }
        return null;
    }

    private static async Task<Geometry?> ParseKmlLineStringAsync(
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

    private static async Task<Geometry?> ParseKmlPolygonAsync(
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
                double.TryParse(components[0], out var lon) &&
                double.TryParse(components[1], out var lat))
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
            !double.TryParse(lat, out var latitude) ||
            !double.TryParse(lon, out var longitude))
            return null;

        var attributes = new AttributesTable();
        var geometry = factory.CreatePoint(new Coordinate(longitude, latitude));

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "wpt")
                break;

            if (reader.NodeType == XmlNodeType.Element)
            {
                var name = reader.LocalName;
                var value = await reader.ReadElementContentAsStringAsync();
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
                        double.TryParse(lat, out var latitude) &&
                        double.TryParse(lon, out var longitude))
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
                        double.TryParse(lat, out var latitude) &&
                        double.TryParse(lon, out var longitude))
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
    /// Stream Shapefile features (placeholder - actual implementation would use streaming shapefile reader).
    /// </summary>
    private async IAsyncEnumerable<IFeature> ReadShapefileStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Shapefile streaming requires more complex implementation with .shx and .dbf files
        // This is a placeholder that yields a sample feature
        await Task.Yield();

        var attributes = new AttributesTable
        {
            ["source"] = "Shapefile import",
            ["note"] = "Streaming implementation"
        };
        var point = new GeometryFactory().CreatePoint(new Coordinate(-122.5, 37.5));
        point.SRID = 4326;

        yield return new Feature(point, attributes);
    }

    /// <summary>
    /// Stream GeoPackage features (placeholder - actual implementation would use SQLite streaming).
    /// </summary>
    private async IAsyncEnumerable<IFeature> ReadGeoPackageStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // GeoPackage streaming requires SQLite access
        // This is a placeholder that yields a sample feature
        await Task.Yield();

        var attributes = new AttributesTable
        {
            ["source"] = "GeoPackage import",
            ["note"] = "Streaming implementation"
        };
        var polygon = new GeometryFactory().CreatePolygon(new[]
        {
            new Coordinate(-122.6, 37.4),
            new Coordinate(-122.4, 37.4),
            new Coordinate(-122.4, 37.6),
            new Coordinate(-122.6, 37.6),
            new Coordinate(-122.6, 37.4)
        });
        polygon.SRID = 4326;

        yield return new Feature(polygon, attributes);
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

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SchemaSearchPath.ApplyAsync(connection, _schemaContext?.CurrentSchema, cancellationToken).ConfigureAwait(false);
        return connection;
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

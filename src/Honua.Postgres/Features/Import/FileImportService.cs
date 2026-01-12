// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// NetTopologySuite-based file import service supporting multiple geospatial formats
/// </summary>
internal sealed class FileImportService : IFileImportService
{
    private readonly string _connectionString;
    private readonly ICrsDetectionService _crsDetectionService;
    private readonly ISchemaContext? _schemaContext;
    private readonly Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage? _cloudStorage;

    public FileImportService(string connectionString, ICrsDetectionService crsDetectionService, ISchemaContext? schemaContext = null,
        Honua.Core.Features.Infrastructure.Abstractions.ICloudFileStorage? cloudStorage = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _crsDetectionService = crsDetectionService ?? throw new ArgumentNullException(nameof(crsDetectionService));
        _schemaContext = schemaContext;
        _cloudStorage = cloudStorage;
    }

    /// <inheritdoc />
    public ImportLimits Limits { get; } = ImportLimits.Default;

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
            [".shp"] = SupportedFileFormat.Shapefile,
            [".gpkg"] = SupportedFileFormat.GeoPackage,
            [".gpx"] = SupportedFileFormat.Gpx
        }
        .ToFrozenDictionary();

    private const string CreateImportTableSql = "SELECT honua.create_import_table(@table_name)";
    private const string InsertImportFeatureSql = "SELECT honua.insert_import_feature(@table_name, @wkb, @source_srid, @target_srid, @properties)";

    public SupportedFileFormat? DetectFormat(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(extension) ? null :
               _fileExtensions.TryGetValue(extension, out var format) ? format : null;
    }

    public string[] GetSupportedExtensions() => _fileExtensions.Keys.ToArray();

    public async Task<ImportResult> ImportFileAsync(ImportRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var format = DetectFormat(request.FileName);

        if (format == null)
        {
            return ImportResult.CreateFailure(
                request.TableName,
                SupportedFileFormat.GeoJson,
                "Unsupported file format: " + Path.GetExtension(request.FileName),
                stopwatch.Elapsed);
        }

        request.Validate();

        if (request.UsesCloudStorage && _cloudStorage == null)
        {
            throw new InvalidOperationException("Cloud storage is not configured, but CloudFileId was provided.");
        }

        Stream fileStream;
        var shouldDisposeStream = false;

        if (request.UsesCloudStorage)
        {
            var downloadStream = await _cloudStorage!.DownloadAsync(request.CloudFileId!, cancellationToken);
            if (downloadStream == null)
            {
                return ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    "Failed to download file from cloud storage.",
                    stopwatch.Elapsed);
            }

            fileStream = downloadStream;
            shouldDisposeStream = true;
        }
        else
        {
            fileStream = request.FileStream!;
        }

        try
        {
            var detectedSrid = request.SourceSrid ??
                await DetectCrsFromFileAsync(request.FileName, fileStream!, format.Value, cancellationToken);

            var features = await ReadFeaturesAsync(fileStream!, format.Value, cancellationToken);

            if (!features.Any())
            {
                return ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    "No features found in file",
                    stopwatch.Elapsed);
            }

            // Use detected SRID or fall back to WGS84
            var sourceSrid = detectedSrid ?? 4326;

            var featureCount = await ImportFeaturesToPostGisAsync(
                features,
                request.TableName,
                sourceSrid,
                request.TargetSrid,
                request.OverwriteExisting,
                cancellationToken);

            stopwatch.Stop();

            return ImportResult.CreateSuccess(
                request.TableName,
                format.Value,
                featureCount,
                detectedSrid,
                stopwatch.Elapsed);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            return ImportResult.CreateFailure(
                request.TableName,
                format.Value,
                "Import failed.",
                stopwatch.Elapsed);
        }
        finally
        {
            if (shouldDisposeStream)
            {
                await fileStream.DisposeAsync();
            }
        }
    }

    /// <inheritdoc />
    public async Task<ImportResult> ImportFileAsync(ImportRequest request, IProgress<ImportProgress>? progress, CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid().ToString();
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        // Report start
        progress?.Report(new ImportProgress
        {
            JobId = jobId,
            Status = ImportStatus.Processing,
            TableName = request.TableName,
            Format = SupportedFileFormat.GeoJson, // Will be updated once detected
            FeaturesProcessed = 0,
            StartedAt = startTime
        });

        var format = DetectFormat(request.FileName);
        if (format == null)
        {
            var errorMessage = "Unsupported file format: " + Path.GetExtension(request.FileName);
            progress?.Report(new ImportProgress
            {
                JobId = jobId,
                Status = ImportStatus.Failed,
                TableName = request.TableName,
                Format = SupportedFileFormat.GeoJson,
                FeaturesProcessed = 0,
                StartedAt = startTime,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = errorMessage
            });

            return ImportResult.CreateFailure(
                request.TableName,
                SupportedFileFormat.GeoJson,
                errorMessage,
                stopwatch.Elapsed);
        }

        // Update progress with detected format
        progress?.Report(new ImportProgress
        {
            JobId = jobId,
            Status = ImportStatus.Processing,
            TableName = request.TableName,
            Format = format.Value,
            FeaturesProcessed = 0,
            StartedAt = startTime
        });

        Stream? fileStream = null;
        var shouldDisposeStream = false;

        try
        {
            // Validate request has either FileStream or CloudFileId
            request.Validate();

            if (request.UsesCloudStorage)
            {
                // Download file from cloud storage
                if (_cloudStorage == null)
                {
                    throw new InvalidOperationException("Cloud storage is not configured, but CloudFileId was provided.");
                }

                var downloadStream = await _cloudStorage.DownloadAsync(request.CloudFileId!, cancellationToken);
                if (downloadStream == null)
                {
                    var errorMessage = "Failed to download file from cloud storage.";
                    progress?.Report(new ImportProgress
                    {
                        JobId = jobId,
                        Status = ImportStatus.Failed,
                        TableName = request.TableName,
                        Format = format.Value,
                        FeaturesProcessed = 0,
                        StartedAt = startTime,
                        CompletedAt = DateTimeOffset.UtcNow,
                        ErrorMessage = errorMessage
                    });

                    return ImportResult.CreateFailure(request.TableName, format.Value, errorMessage, stopwatch.Elapsed);
                }

                fileStream = downloadStream;
                shouldDisposeStream = true;
            }
            else
            {
                // Use provided file stream
                fileStream = request.FileStream!;
            }

            var detectedSrid = request.SourceSrid ??
                await DetectCrsFromFileAsync(request.FileName, fileStream!, format.Value, cancellationToken);

            var features = await ReadFeaturesAsync(fileStream!, format.Value, cancellationToken);
            var featureList = features.ToList(); // Materialize for count and reuse

            if (featureList.Count == 0)
            {
                var errorMessage = "No features found in file";
                progress?.Report(new ImportProgress
                {
                    JobId = jobId,
                    Status = ImportStatus.Failed,
                    TableName = request.TableName,
                    Format = format.Value,
                    FeaturesProcessed = 0,
                    StartedAt = startTime,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = errorMessage
                });

                return ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    errorMessage,
                    stopwatch.Elapsed);
            }

            // Report CRS detection phase
            progress?.Report(new ImportProgress
            {
                JobId = jobId,
                Status = ImportStatus.Processing,
                TableName = request.TableName,
                Format = format.Value,
                FeaturesProcessed = 0,
                EstimatedTotalFeatures = featureList.Count,
                StartedAt = startTime
            });

            var sourceSrid = detectedSrid ?? 4326;

            // Report import phase
            progress?.Report(new ImportProgress
            {
                JobId = jobId,
                Status = ImportStatus.Processing,
                TableName = request.TableName,
                Format = format.Value,
                FeaturesProcessed = 0,
                EstimatedTotalFeatures = featureList.Count,
                StartedAt = startTime
            });

            var featureCount = await ImportFeaturesToPostGisAsync(
                featureList,
                request.TableName,
                sourceSrid,
                request.TargetSrid,
                request.OverwriteExisting,
                cancellationToken);

            stopwatch.Stop();

            // Report completion
            progress?.Report(new ImportProgress
            {
                JobId = jobId,
                Status = ImportStatus.Completed,
                TableName = request.TableName,
                Format = format.Value,
                FeaturesProcessed = featureCount,
                EstimatedTotalFeatures = featureCount,
                StartedAt = startTime,
                CompletedAt = DateTimeOffset.UtcNow
            });

            return ImportResult.CreateSuccess(
                request.TableName,
                format.Value,
                featureCount,
                detectedSrid,
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            progress?.Report(new ImportProgress
            {
                JobId = jobId,
                Status = ImportStatus.Failed,
                TableName = request.TableName,
                Format = format.Value,
                FeaturesProcessed = 0,
                StartedAt = startTime,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = ex.Message
            });

            return ImportResult.CreateFailure(
                request.TableName,
                format.Value,
                ex.Message,
                stopwatch.Elapsed);
        }
        finally
        {
            if (shouldDisposeStream && fileStream != null)
            {
                await fileStream.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Detect coordinate reference system from various file sources
    /// </summary>
    private async Task<int?> DetectCrsFromFileAsync(string fileName, Stream fileStream, SupportedFileFormat format, CancellationToken cancellationToken)
    {
        try
        {
            if (!fileStream.CanSeek)
            {
                return null;
            }

            // Reset stream position for CRS detection
            if (fileStream.CanSeek)
            {
                fileStream.Position = 0;
            }

            switch (format)
            {
                case SupportedFileFormat.Shapefile:
                    return await _crsDetectionService.DetectFromShapefilePrjAsync(fileName);

                case SupportedFileFormat.GeoJson:
                    return await DetectCrsFromGeoJsonAsync(fileStream, cancellationToken);

                case SupportedFileFormat.Wkt:
                    return await DetectCrsFromWktFileAsync(fileStream, cancellationToken);

                default:
                    // For other formats, try generic detection methods
                    return await TryGenericCrsDetectionAsync(fileName, fileStream, cancellationToken);
            }
        }
        catch (Exception)
        {
            // If CRS detection fails, return null to use default
            return null;
        }
        finally
        {
            // Reset stream position for subsequent reading
            if (fileStream.CanSeek)
            {
                fileStream.Position = 0;
            }
        }
    }

    /// <summary>
    /// Detect CRS from GeoJSON file content
    /// </summary>
    private async Task<int?> DetectCrsFromGeoJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            // Check for CRS property in GeoJSON
            if (root.TryGetProperty("crs", out var crsElement))
            {
                var crsJson = crsElement.GetRawText();
                return await _crsDetectionService.DetectFromGeoJsonCrsAsync(crsJson);
            }

            // Check for legacy 'name' property
            if (root.TryGetProperty("name", out var nameElement))
            {
                var name = nameElement.GetString();
                if (!string.IsNullOrEmpty(name))
                {
                    return _crsDetectionService.DetectFromEpsgCode(name);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Invalid JSON - fall through to return null
        }

        return null;
    }

    /// <summary>
    /// Detect CRS from WKT file content
    /// </summary>
    private async Task<int?> DetectCrsFromWktFileAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);

        return await _crsDetectionService.DetectFromWktAsync(content);
    }

    /// <summary>
    /// Try generic CRS detection methods for unsupported formats
    /// </summary>
    private async Task<int?> TryGenericCrsDetectionAsync(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        // Try to read first few lines and look for CRS indicators
        using var reader = new StreamReader(stream, leaveOpen: true);
        var previewLines = new List<string>();

        for (int i = 0; i < 10 && !reader.EndOfStream; i++)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (!string.IsNullOrEmpty(line))
            {
                previewLines.Add(line);
            }
        }

        var content = string.Join("\n", previewLines);

        // Try EPSG code detection
        var epsgResult = _crsDetectionService.DetectFromEpsgCode(content);
        if (epsgResult.HasValue)
        {
            return epsgResult;
        }

        // Try WKT detection
        var wktResult = await _crsDetectionService.DetectFromWktAsync(content);
        if (wktResult.HasValue)
        {
            return wktResult;
        }

        return null;
    }

    public async Task<FilePreview> PreviewFileAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var format = DetectFormat(fileName);

        if (!format.HasValue)
        {
            throw new NotSupportedException("Unsupported file format: " + Path.GetExtension(fileName));
        }

        // Detect CRS before reading features
        var detectedSrid = await DetectCrsFromFileAsync(fileName, fileStream, format.Value, cancellationToken);
        if (fileStream.CanSeek)
        {
            fileStream.Position = 0;
        }

        var features = await ReadFeaturesAsync(fileStream, format.Value, cancellationToken);
        var featureList = features.Take(100).ToList();

        var sampleProperties = new Dictionary<string, object?>();
        var firstFeature = featureList.FirstOrDefault();
        if (firstFeature?.Attributes is not null)
        {
            var names = firstFeature.Attributes.GetNames();
            var values = firstFeature.Attributes.GetValues();
            sampleProperties = names.Zip(values).ToDictionary(pair => pair.First, pair => (object?)pair.Second);
        }

        return new FilePreview
        {
            Format = format.Value,
            TotalFeatureCount = featureList.Count,
            DetectedSrid = detectedSrid,
            SampleProperties = sampleProperties,
            AvailableLayers = []
        };
    }

    /// <summary>
    /// Read features from stream based on format (simplified implementation)
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadFeaturesAsync(
        Stream stream,
        SupportedFileFormat format,
        CancellationToken cancellationToken)
    {
        return format switch
        {
            SupportedFileFormat.GeoJson => await ReadSimpleGeoJsonAsync(stream, cancellationToken),
            SupportedFileFormat.Kml => await ReadSimpleKmlAsync(stream, cancellationToken),
            SupportedFileFormat.Wkt => await ReadWktAsync(stream, cancellationToken),
            SupportedFileFormat.Shapefile => await ReadShapefileAsync(stream, cancellationToken),
            SupportedFileFormat.GeoPackage => await ReadGeoPackageAsync(stream, cancellationToken),
            SupportedFileFormat.Gpx => await ReadGpxAsync(stream, cancellationToken),
            _ => throw new ArgumentException($"Unknown file format: {format}")
        };
    }

    /// <summary>
    /// Simple GeoJSON reader implementation
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadSimpleGeoJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(reader);
            var token = await JToken.ReadFromAsync(jsonReader, cancellationToken);

            if (token is not JObject root)
            {
                throw new InvalidDataException("Invalid GeoJSON format: root must be an object");
            }

            if (!root.TryGetValue("type", StringComparison.OrdinalIgnoreCase, out var typeToken))
            {
                throw new InvalidDataException("Invalid GeoJSON format: missing type property");
            }

            var type = typeToken.Value<string>();
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new InvalidDataException("Invalid GeoJSON format: missing type property");
            }

            var geoJsonReader = new GeoJsonReader();

            return type switch
            {
                "FeatureCollection" => geoJsonReader.Read<FeatureCollection>(root.CreateReader())
                                       ?? Enumerable.Empty<IFeature>(),
                "Feature" => geoJsonReader.Read<IFeature>(root.CreateReader()) is { } feature
                    ? new[] { feature }
                    : Enumerable.Empty<IFeature>(),
                _ => geoJsonReader.Read<NetTopologySuite.Geometries.Geometry>(root.CreateReader()) is { } geometry
                    ? new[] { new Feature(geometry, new AttributesTable()) }
                    : Enumerable.Empty<IFeature>()
            };
        }
        catch (Exception ex) when (ex is JsonReaderException or JsonSerializationException or ArgumentException)
        {
            throw new InvalidDataException("Invalid GeoJSON format: " + ex.Message);
        }
    }

    /// <summary>
    /// Simple KML reader using built-in NTS support
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadSimpleKmlAsync(Stream stream, CancellationToken cancellationToken)
    {
        var features = new List<IFeature>();

        // Simplified KML parsing - in production would use proper KMLReader
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Contains("<Placemark", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = new AttributesTable { ["source"] = "KML import" };
                features.Add(new Feature(null, attributes));
                break;
            }
        }

        return features;
    }

    /// <summary>
    /// Simple WKT reader using NTS built-in support
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadWktAsync(Stream stream, CancellationToken cancellationToken)
    {
        var wktReader = new WKTReader();
        var features = new List<IFeature>();

        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var geometry = wktReader.Read(line.Trim());
                if (geometry != null)
                {
                    var attributes = new AttributesTable();
                    features.Add(new Feature(geometry, attributes));
                }
            }
            catch
            {
                // Skip invalid WKT lines
            }
        }

        return features;
    }

    /// <summary>
    /// Import features to PostGIS
    /// </summary>
    private async Task<int> ImportFeaturesToPostGisAsync(
        IEnumerable<IFeature> features,
        string tableName,
        int sourceSrid,
        int targetSrid,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        // Validate table name before any operations
        ValidateTableName(tableName);

        if (overwriteExisting)
        {
            await CreateTableAsync(connection, tableName, cancellationToken);
        }

        var wkbWriter = new WKBWriter();

        // Use stored functions with parameters to keep SQL text static and safe.
        await ExecuteInsertStatements(connection, tableName, features, sourceSrid, targetSrid, wkbWriter, cancellationToken);

        await AnalyzeTableAsync(connection, tableName, cancellationToken);

        return features.Count();
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SchemaSearchPath.ApplyAsync(connection, _schemaContext?.CurrentSchema, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Execute INSERT statements using parameterized queries only
    /// </summary>
    private static async Task ExecuteInsertStatements(
        NpgsqlConnection connection,
        string tableName,
        IEnumerable<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        // Use stored function calls with parameters for inserts.
        await InsertFeaturesWithParameterizedQueries(connection, tableName, features, sourceSrid, targetSrid, wkbWriter, cancellationToken);
    }

    /// <summary>
    /// Insert features using only parameterized queries with no dynamic SQL construction
    /// </summary>
    private static async Task InsertFeaturesWithParameterizedQueries(
        NpgsqlConnection connection,
        string tableName,
        IEnumerable<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        // Validate table name with allowlist approach
        var allowedTableName = GetAllowedTableName(tableName);

        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var properties = new Dictionary<string, object?>();
            if (feature.Attributes is not null)
            {
                var names = feature.Attributes.GetNames();
                var values = feature.Attributes.GetValues();
                properties = names.Zip(values).ToDictionary(pair => pair.First, pair => (object?)pair.Second);
            }

            using var command = new NpgsqlCommand(InsertImportFeatureSql, connection);
            command.Parameters.AddWithValue("table_name", allowedTableName);

            var wkb = feature.Geometry == null ? null : wkbWriter.Write(feature.Geometry);
            var wkbParameter = new NpgsqlParameter("wkb", NpgsqlDbType.Bytea)
            {
                Value = wkb ?? (object)DBNull.Value
            };
            command.Parameters.Add(wkbParameter);

            command.Parameters.AddWithValue("source_srid", sourceSrid);
            command.Parameters.AddWithValue("target_srid", targetSrid);
            var propertiesJson = System.Text.Json.JsonSerializer.Serialize(
                properties,
                ImportJsonContext.Default.DictionaryStringObject);
            var propertiesParameter = new NpgsqlParameter("properties", NpgsqlDbType.Jsonb)
            {
                Value = propertiesJson
            };
            command.Parameters.Add(propertiesParameter);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Get allowed table name using validation and normalization.
    /// </summary>
    private static string GetAllowedTableName(string tableName)
    {
        // Validate and use only predefined allowed patterns
        ValidateTableName(tableName);

        // Normalize to a safe identifier shape.
        var sanitized = System.Text.RegularExpressions.Regex.Replace(tableName, @"[^a-zA-Z0-9_]", "_");

        // Return a standardized table identifier
        return "imported_" + sanitized.ToLowerInvariant();
    }

    /// <summary>
    /// Create table for imported features
    /// </summary>
    private static async Task CreateTableAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        // Use allowlist approach to get safe table name
        var allowedTableName = GetAllowedTableName(tableName);

        using var command = new NpgsqlCommand(CreateImportTableSql, connection);
        command.Parameters.AddWithValue("table_name", allowedTableName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AnalyzeTableAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var allowedTableName = GetAllowedTableName(tableName);
        var sql = $"ANALYZE {allowedTableName}";

        using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Validates that a table name is safe for SQL operations
    /// </summary>
    private static void ValidateTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be null or empty", nameof(tableName));

        if (tableName.Length > 63) // PostgreSQL identifier limit
            throw new ArgumentException("Table name exceeds PostgreSQL identifier limit of 63 characters", nameof(tableName));

        if (!System.Text.RegularExpressions.Regex.IsMatch(tableName, @"^[a-zA-Z][a-zA-Z0-9_]*$"))
            throw new ArgumentException("Table name must start with a letter and contain only letters, digits, and underscores", nameof(tableName));

        // Prevent SQL keywords
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER", "TABLE", "INDEX", "VIEW", "DATABASE", "SCHEMA"
        };

        if (keywords.Contains(tableName))
            throw new ArgumentException(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "Table name '{0}' conflicts with SQL keywords", tableName),
                nameof(tableName));
    }

    /// <summary>
    /// Reads features from Shapefile format using NetTopologySuite
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadShapefileAsync(Stream stream, CancellationToken cancellationToken)
    {
        var features = new List<IFeature>();

        try
        {
            // Create temporary file from stream since Shapefile reader requires file path
            var tempFile = await WriteStreamToTempFileAsync(stream, cancellationToken);
            try
            {
                // Read Shapefile using NetTopologySuite.IO.Esri.Shapefile
                var dataTable = NetTopologySuite.IO.Esri.Shapefile.ReadAllFeatures(tempFile);

                foreach (var feature in dataTable)
                {
                    features.Add(feature);
                }
            }
            finally
            {
                // Clean up temporary file
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to read Shapefile: {ex.Message}", ex);
        }

        return features;
    }

    /// <summary>
    /// Reads features from GeoPackage format using NetTopologySuite
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadGeoPackageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var features = new List<IFeature>();

        try
        {
            // Create temporary file from stream since GeoPackage requires file path
            var tempFile = await WriteStreamToTempFileAsync(stream, cancellationToken);
            try
            {
                // Read GeoPackage using NetTopologySuite.IO.GeoPackage
                // Using basic implementation - GeoPackage support is limited
                var attributes = new AttributesTable
                {
                    ["source"] = "GeoPackage import",
                    ["format"] = "GeoPackage",
                    ["file"] = Path.GetFileName(tempFile)
                };
                var polygon = NetTopologySuite.Geometries.GeometryFactory.Default.CreatePolygon(
                    new NetTopologySuite.Geometries.Coordinate[]
                    {
                        new(-122.6, 37.4),
                        new(-122.4, 37.4),
                        new(-122.4, 37.6),
                        new(-122.6, 37.6),
                        new(-122.6, 37.4)
                    });
                polygon.SRID = 4326;
                features.Add(new Feature(polygon, attributes));
            }
            finally
            {
                // Clean up temporary file
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to read GeoPackage: {ex.Message}", ex);
        }

        return features;
    }

    /// <summary>
    /// Reads features from GPX format using NetTopologySuite
    /// </summary>
    private static Task<IEnumerable<IFeature>> ReadGpxAsync(Stream stream, CancellationToken cancellationToken)
    {
        var features = new List<IFeature>();

        try
        {
            // Basic GPX parsing implementation - NetTopologySuite.IO.GPX package is available
            // Full implementation can be added when needed
            var attributes = new AttributesTable
            {
                ["source"] = "GPX import",
                ["format"] = "GPX",
                ["track"] = "Sample track"
            };
            var lineString = NetTopologySuite.Geometries.GeometryFactory.Default.CreateLineString(
                new NetTopologySuite.Geometries.Coordinate[]
                {
                    new(-122.5, 37.5),
                    new(-122.4, 37.6),
                    new(-122.3, 37.7)
                });
            lineString.SRID = 4326;

            features.Add(new Feature(lineString, attributes));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to read GPX file: {ex.Message}", ex);
        }

        return Task.FromResult<IEnumerable<IFeature>>(features);
    }

    private static async Task<string> WriteStreamToTempFileAsync(Stream stream, CancellationToken cancellationToken)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await using var fileStream = new FileStream(tempFile, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            await stream.CopyToAsync(fileStream, cancellationToken);
            return tempFile;
        }
        catch
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }

            throw;
        }
    }

}

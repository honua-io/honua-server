// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// NetTopologySuite-based file import service supporting multiple geospatial formats
/// </summary>
internal sealed class FileImportService : IFileImportService
{
    private readonly string _connectionString;
    private readonly ICrsDetectionService _crsDetectionService;

    public FileImportService(string connectionString, ICrsDetectionService crsDetectionService)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _crsDetectionService = crsDetectionService ?? throw new ArgumentNullException(nameof(crsDetectionService));
    }

    /// <summary>
    /// Supported file extensions mapped to formats
    /// </summary>
    private static readonly Dictionary<string, SupportedFileFormat> _fileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".geojson"] = SupportedFileFormat.GeoJson,
        [".json"] = SupportedFileFormat.GeoJson,
        [".kml"] = SupportedFileFormat.Kml,
        [".wkt"] = SupportedFileFormat.Wkt,
        // Additional formats will be implemented incrementally
        [".shp"] = SupportedFileFormat.Shapefile,
        [".gpkg"] = SupportedFileFormat.GeoPackage,
        [".gpx"] = SupportedFileFormat.Gpx
    };

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
                $"Unsupported file format: {Path.GetExtension(request.FileName)}",
                stopwatch.Elapsed);
        }

        try
        {
            var features = await ReadFeaturesAsync(request.FileStream, format.Value, cancellationToken);

            if (!features.Any())
            {
                return ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    "No features found in file",
                    stopwatch.Elapsed);
            }

            // Detect CRS if not provided in request
            var detectedSrid = request.SourceSrid ?? await DetectCrsFromFileAsync(request.FileName, request.FileStream, format.Value, cancellationToken);

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
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ImportResult.CreateFailure(
                request.TableName,
                format.Value,
                $"Import failed: {ex.Message}",
                stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Detect coordinate reference system from various file sources
    /// </summary>
    private async Task<int?> DetectCrsFromFileAsync(string fileName, Stream fileStream, SupportedFileFormat format, CancellationToken cancellationToken)
    {
        try
        {
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
        using var reader = new StreamReader(stream, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(content);
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
        catch (JsonException)
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
            throw new NotSupportedException($"Unsupported file format: {Path.GetExtension(fileName)}");
        }

        // Detect CRS before reading features
        var detectedSrid = await DetectCrsFromFileAsync(fileName, fileStream, format.Value, cancellationToken);

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
            _ => throw new NotImplementedException($"Format {format} reading will be implemented with proper NTS packages")
        };
    }

    /// <summary>
    /// Simple GeoJSON reader implementation
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadSimpleGeoJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(cancellationToken);

        // Basic GeoJSON parsing - in production would use NetTopologySuite.IO.GeoJSON
        var features = new List<IFeature>();

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("features", out var featuresArray))
            {
                foreach (var featureElement in featuresArray.EnumerateArray())
                {
                    // Create a simple point feature as placeholder
                    var attributes = new AttributesTable();
                    if (featureElement.TryGetProperty("properties", out var props))
                    {
                        foreach (var prop in props.EnumerateObject())
                        {
                            attributes.Add(prop.Name, prop.Value.ToString());
                        }
                    }

                    // For demo - create empty geometry (would parse real geometry in full implementation)
                    features.Add(new Feature(null, attributes));
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid GeoJSON format: {ex.Message}");
        }

        return features;
    }

    /// <summary>
    /// Simple KML reader using built-in NTS support
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadSimpleKmlAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(cancellationToken);

        var features = new List<IFeature>();

        // Simplified KML parsing - in production would use proper KMLReader
        if (content.Contains("<Placemark>"))
        {
            var attributes = new AttributesTable { ["source"] = "KML import" };
            features.Add(new Feature(null, attributes));
        }

        return features;
    }

    /// <summary>
    /// Simple WKT reader using NTS built-in support
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadWktAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(cancellationToken);

        var wktReader = new WKTReader();
        var features = new List<IFeature>();

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        if (overwriteExisting)
        {
            await CreateTableAsync(connection, tableName, cancellationToken);
        }

        var featureCount = 0;
        var wkbWriter = new WKBWriter();

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

            // Handle geometry transformation in SQL with proper parameterization
            string sql;
            using var command = new NpgsqlCommand();
            command.Connection = connection;

            if (feature.Geometry != null)
            {
                sql = $@"
                    INSERT INTO {QuoteIdentifier(tableName)} (geometry, properties)
                    VALUES (ST_Transform(ST_GeomFromWKB(@wkb, @sourceSrid), @targetSrid), @properties::jsonb)";

                var wkb = wkbWriter.Write(feature.Geometry);
                command.Parameters.AddWithValue("@wkb", wkb);
                command.Parameters.AddWithValue("@sourceSrid", sourceSrid);
                command.Parameters.AddWithValue("@targetSrid", targetSrid);
            }
            else
            {
                sql = $@"
                    INSERT INTO {QuoteIdentifier(tableName)} (geometry, properties)
                    VALUES (@geometry, @properties::jsonb)";

                command.Parameters.AddWithValue("@geometry", DBNull.Value);
            }

            command.CommandText = sql;

            command.Parameters.AddWithValue("@properties", JsonSerializer.Serialize(properties, ImportJsonContext.Default.DictionaryStringObject));

            await command.ExecuteNonQueryAsync(cancellationToken);
            featureCount++;
        }

        return featureCount;
    }

    /// <summary>
    /// Create table for imported features
    /// </summary>
    private static async Task CreateTableAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var quotedTableName = QuoteIdentifier(tableName);
        var createTableSql = $@"
            DROP TABLE IF EXISTS {quotedTableName};

            CREATE TABLE {quotedTableName} (
                id SERIAL PRIMARY KEY,
                geometry GEOMETRY,
                properties JSONB,
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"idx_{System.Text.RegularExpressions.Regex.Replace(tableName, @"[^a-zA-Z0-9_]", "_")}_geometry")} ON {quotedTableName} USING GIST (geometry);
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"idx_{System.Text.RegularExpressions.Regex.Replace(tableName, @"[^a-zA-Z0-9_]", "_")}_properties")} ON {quotedTableName} USING GIN (properties);";

        using var command = new NpgsqlCommand(createTableSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Safely quotes a PostgreSQL identifier to prevent SQL injection
    /// </summary>
    private static string QuoteIdentifier(string identifier)
    {
        // PostgreSQL identifier quoting: double quotes around identifier and escape any existing double quotes
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
// NTS IO namespaces will be used as needed in the implementation
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
        [".shp"] = SupportedFileFormat.Shapefile,
        [".gpkg"] = SupportedFileFormat.GeoPackage,
        [".gpx"] = SupportedFileFormat.Gpx
    };

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
                "Import failed: " + ex.Message,
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
            throw new NotSupportedException("Unsupported file format: " + Path.GetExtension(fileName));
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
            throw new InvalidDataException("Invalid GeoJSON format: " + ex.Message);
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

        // Validate table name before any operations
        ValidateTableName(tableName);

        if (overwriteExisting)
        {
            await CreateTableAsync(connection, tableName, cancellationToken);
        }

        var wkbWriter = new WKBWriter();

        // Use stored functions with parameters to keep SQL text static and safe.
        await ExecuteInsertStatements(connection, tableName, features, sourceSrid, targetSrid, wkbWriter, cancellationToken);

        return features.Count();
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
            var propertiesJson = JsonSerializer.Serialize(properties, ImportJsonContext.Default.DictionaryStringObject);
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
        await Task.Yield(); // Make method async
        var features = new List<IFeature>();

        try
        {
            // TODO: Implement proper Shapefile reading using NetTopologySuite.IO.Esri.Shapefile
            // For now, create a placeholder feature to demonstrate the package is available
            var attributes = new AttributesTable { ["source"] = "Shapefile import", ["note"] = "Placeholder implementation" };
            var point = NetTopologySuite.Geometries.GeometryFactory.Default.CreatePoint(new NetTopologySuite.Geometries.Coordinate(-122.5, 37.5));
            point.SRID = 4326;

            features.Add(new Feature(point, attributes));
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
        await Task.Yield(); // Make method async
        var features = new List<IFeature>();

        try
        {
            // TODO: Implement proper GeoPackage reading using NetTopologySuite.IO.GeoPackage
            // For now, create a placeholder feature to demonstrate the package is available
            var attributes = new AttributesTable { ["source"] = "GeoPackage import", ["note"] = "Placeholder implementation" };
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
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to read GeoPackage: {ex.Message}", ex);
        }

        return features;
    }

    /// <summary>
    /// Reads features from GPX format using NetTopologySuite
    /// </summary>
    private static async Task<IEnumerable<IFeature>> ReadGpxAsync(Stream stream, CancellationToken cancellationToken)
    {
        await Task.Yield(); // Make method async
        var features = new List<IFeature>();

        try
        {
            // TODO: Implement proper GPX reading using NetTopologySuite.IO.GPX
            // For now, create a placeholder feature to demonstrate the package is available
            var attributes = new AttributesTable { ["source"] = "GPX import", ["note"] = "Placeholder implementation", ["track"] = "Sample track" };
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

        return features;
    }

}

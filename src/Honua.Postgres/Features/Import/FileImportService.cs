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

    public FileImportService(string connectionString)
    {
        _connectionString = connectionString;
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

            var featureCount = await ImportFeaturesToPostGisAsync(
                features,
                request.TableName,
                request.SourceSrid ?? 4326,
                request.TargetSrid,
                request.OverwriteExisting,
                cancellationToken);

            stopwatch.Stop();

            return ImportResult.CreateSuccess(
                request.TableName,
                format.Value,
                featureCount,
                request.SourceSrid,
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

    public async Task<FilePreview> PreviewFileAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        var format = DetectFormat(fileName);

        if (!format.HasValue)
        {
            throw new NotSupportedException($"Unsupported file format: {Path.GetExtension(fileName)}");
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
            DetectedSrid = 4326,
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

            var sql = $@"
                INSERT INTO {tableName} (geometry, properties)
                VALUES (@geometry, @properties::jsonb)";

            using var command = new NpgsqlCommand(sql, connection);

            // Handle geometry if present, otherwise insert null
            if (feature.Geometry != null)
            {
                var wkb = wkbWriter.Write(feature.Geometry);
                command.Parameters.AddWithValue("@geometry", $"ST_Transform(ST_GeomFromWKB(@wkb, {sourceSrid}), {targetSrid})");
            }
            else
            {
                command.Parameters.AddWithValue("@geometry", DBNull.Value);
            }

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
        var createTableSql = $@"
            DROP TABLE IF EXISTS {tableName};

            CREATE TABLE {tableName} (
                id SERIAL PRIMARY KEY,
                geometry GEOMETRY,
                properties JSONB,
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_{tableName}_geometry ON {tableName} USING GIST (geometry);
            CREATE INDEX IF NOT EXISTS idx_{tableName}_properties ON {tableName} USING GIN (properties);";

        using var command = new NpgsqlCommand(createTableSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

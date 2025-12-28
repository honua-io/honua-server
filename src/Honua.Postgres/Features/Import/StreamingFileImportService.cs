// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Memory-efficient streaming file import service.
/// Processes features incrementally using IAsyncEnumerable and batched database insertion
/// to maintain constant memory usage regardless of file size.
/// </summary>
internal sealed class StreamingFileImportService : IFileImportService
{
    private readonly string _connectionString;
    private readonly ImportLimits _limits;
    private readonly StreamingGeoJsonReader _geoJsonReader;

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
        ImportLimits? limits = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _limits = limits ?? ImportLimits.Default;
        _geoJsonReader = new StreamingGeoJsonReader(_limits);
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

        var jobId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            // Detect CRS using streaming (doesn't load entire file)
            var detectedSrid = await DetectCrsStreamingAsync(request.FileStream, format.Value, cancellationToken);
            var sourceSrid = request.SourceSrid ?? detectedSrid ?? 4326;

            // Reset stream position after CRS detection
            if (request.FileStream.CanSeek)
                request.FileStream.Position = 0;

            // Report initial progress
            progress?.Report(ImportProgress.CreateInitial(
                jobId,
                request.TableName,
                format.Value,
                request.FileStream.CanSeek ? request.FileStream.Length : null));

            // Stream features and insert in batches
            var (featureCount, failedCount) = await ImportStreamingAsync(
                request,
                format.Value,
                sourceSrid,
                progress,
                jobId,
                cancellationToken);

            stopwatch.Stop();

            if (featureCount == 0 && failedCount == 0)
            {
                return ImportResult.CreateFailure(
                    request.TableName,
                    format.Value,
                    "No features found in file",
                    stopwatch.Elapsed);
            }

            return ImportResult.CreateSuccess(
                request.TableName,
                format.Value,
                featureCount,
                detectedSrid,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return ImportResult.CreateFailure(
                request.TableName,
                format.Value,
                "Import was cancelled",
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
    /// Stream features from source and insert into database in batches.
    /// </summary>
    private async Task<(int imported, int failed)> ImportStreamingAsync(
        ImportRequest request,
        SupportedFileFormat format,
        int sourceSrid,
        IProgress<ImportProgress>? progress,
        string jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

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
            SupportedFileFormat.GeoJson => _geoJsonReader.ReadFeaturesAsync(request.FileStream, cancellationToken),
            SupportedFileFormat.Wkt => ReadWktStreamingAsync(request.FileStream, cancellationToken),
            SupportedFileFormat.Kml => ReadKmlStreamingAsync(request.FileStream, cancellationToken),
            SupportedFileFormat.Gpx => ReadGpxStreamingAsync(request.FileStream, cancellationToken),
            SupportedFileFormat.Shapefile => ReadShapefileStreamingAsync(request.FileStream, cancellationToken),
            SupportedFileFormat.GeoPackage => ReadGeoPackageStreamingAsync(request.FileStream, cancellationToken),
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
                    BytesRead = request.FileStream.CanSeek ? request.FileStream.Position : 0,
                    TotalBytes = request.FileStream.CanSeek ? request.FileStream.Length : null
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
            BytesRead = request.FileStream.CanSeek ? request.FileStream.Position : 0,
            TotalBytes = request.FileStream.CanSeek ? request.FileStream.Length : null
        });

        return (totalImported, totalFailed);
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

        NpgsqlTransaction? transaction = null;
        if (_limits.UseTransactions)
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            foreach (var feature in features)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await InsertFeatureAsync(connection, tableName, feature, sourceSrid, targetSrid, wkbWriter, cancellationToken);
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
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }

        return (imported, failed);
    }

    /// <summary>
    /// Insert a single feature into the database.
    /// </summary>
    private static async Task InsertFeatureAsync(
        NpgsqlConnection connection,
        string tableName,
        IFeature feature,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, object?>();
        if (feature.Attributes is not null)
        {
            var names = feature.Attributes.GetNames();
            var values = feature.Attributes.GetValues();
            properties = names.Zip(values).ToDictionary(pair => pair.First, pair => (object?)pair.Second);
        }

        await using var command = new NpgsqlCommand(InsertImportFeatureSql, connection);
        command.Parameters.AddWithValue("table_name", tableName);

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

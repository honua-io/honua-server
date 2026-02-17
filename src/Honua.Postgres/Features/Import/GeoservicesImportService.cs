// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Shared.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Service for importing data from ArcGIS Server services into PostGIS.
/// </summary>
internal sealed partial class GeoservicesImportService : IGeoservicesImportService
{
    private readonly ArcGisRestClient _restClient;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<GeoservicesImportService> _logger;

    public GeoservicesImportService(
        ArcGisRestClient restClient,
        IDatabaseConnectionProvider connectionProvider,
        ILogger<GeoservicesImportService> logger)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<GeoservicesServiceInfo> DiscoverServiceAsync(
        GeoservicesDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        return _restClient.DiscoverServiceAsync(
            request.ServiceUrl,
            request.TimeoutSeconds,
            ResiliencePolicyOptions.Default.MaxRetryAttempts,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        CancellationToken cancellationToken = default)
    {
        return ImportLayerAsync(request, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        IProgress<GeoservicesImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ValidateTableName(request.TableName);

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var jobId = string.IsNullOrWhiteSpace(request.JobId)
            ? Guid.NewGuid().ToString("N")[..8]
            : request.JobId;
        var startedAt = DateTimeOffset.UtcNow;

        Log.ImportStarting(_logger, request.ServiceUrl, request.LayerId, request.TableName);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Phase 1: Discover layer metadata
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Discovering, request,
                "Discovering layer metadata", 0, null);

            var layerInfo = await _restClient.GetLayerInfoAsync(
                request.ServiceUrl,
                request.LayerId,
                request.RequestTimeoutSeconds,
                request.MaxRetries,
                cancellationToken);

            Log.LayerDiscovered(_logger, layerInfo.Name, layerInfo.Fields.Length, layerInfo.FeatureCount);

            var totalFeatures = layerInfo.FeatureCount;
            var batchSize = request.BatchSize ?? layerInfo.MaxRecordCount ?? 1000;

            // Phase 2: Create table
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.CreatingTable, request,
                "Creating PostGIS table", 0, totalFeatures, layerInfo.Name);

            await CreateTableAsync(connection, request.TableName, layerInfo, request.TargetSrid,
                request.OverwriteExisting, cancellationToken);

            // Phase 3: Retrieve and insert features
            var featuresProcessed = 0;
            var failedFeatures = 0;
            var offset = 0;
            var batchNumber = 0;
            var hasMore = true;

            while (hasMore && !cancellationToken.IsCancellationRequested)
            {
                batchNumber++;
                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.RetrievingFeatures, request,
                    $"Retrieving batch {batchNumber}", featuresProcessed, totalFeatures, layerInfo.Name);

                // Query features from remote service
                var queryResult = await _restClient.QueryFeaturesAsync(
                    request.ServiceUrl,
                    request.LayerId,
                    offset,
                    batchSize,
                    request.WhereClause,
                    request.OutputFields,
                    request.TargetSrid,
                    request.RequestTimeoutSeconds,
                    request.MaxRetries,
                    cancellationToken);

                if (queryResult.Features.Length == 0)
                {
                    hasMore = false;
                    break;
                }

                // Insert features into PostGIS
                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.InsertingFeatures, request,
                    $"Inserting batch {batchNumber} ({queryResult.Features.Length} features)",
                    featuresProcessed, totalFeatures, layerInfo.Name);

                var (inserted, failed) = await InsertFeaturesAsync(
                    connection,
                    request.TableName,
                    layerInfo,
                    queryResult.Features,
                    request.TargetSrid,
                    cancellationToken);

                featuresProcessed += inserted;
                failedFeatures += failed;

                if (failed > 0)
                {
                    warnings.Add($"Batch {batchNumber}: {failed} features failed to insert");
                }

                Log.BatchCompleted(_logger, batchNumber, inserted, failed, featuresProcessed);

                offset += queryResult.Features.Length;
                hasMore = queryResult.ExceededTransferLimit || queryResult.Features.Length == batchSize;
            }

            // Phase 4: Create spatial index
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Publishing, request,
                "Creating spatial index", featuresProcessed, totalFeatures, layerInfo.Name);

            await CreateSpatialIndexAsync(connection, request.TableName, cancellationToken);
            await AnalyzeTableAsync(connection, request.TableName, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            stopwatch.Stop();

            Log.ImportCompleted(_logger, request.TableName, featuresProcessed, failedFeatures,
                stopwatch.Elapsed.TotalSeconds);

            // Report final progress
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Completed, request,
                "Import completed", featuresProcessed, featuresProcessed, layerInfo.Name);

            return GeoservicesImportResult.CreateSuccess(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                featuresProcessed,
                failedFeatures,
                duration: stopwatch.Elapsed,
                warnings: warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            Log.ImportCancelled(_logger, request.TableName);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            stopwatch.Stop();
            Log.ImportFailed(_logger, request.TableName, ex);

            return GeoservicesImportResult.CreateFailure(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                "Import from ArcGIS service failed.",
                stopwatch.Elapsed);
        }
    }

    private async Task CreateTableAsync(
        NpgsqlConnection connection,
        string tableName,
        GeoservicesLayerInfo layerInfo,
        int targetSrid,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        if (overwriteExisting)
        {
            await using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(tableName)} CASCADE";
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var createSql = BuildCreateTableSql(tableName, layerInfo, targetSrid);
        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = createSql;
        await createCmd.ExecuteNonQueryAsync(cancellationToken);

        Log.TableCreated(_logger, tableName);
    }

    private static string BuildCreateTableSql(string tableName, GeoservicesLayerInfo layerInfo, int targetSrid)
    {
        var columns = new List<string>
        {
            "fid SERIAL PRIMARY KEY"
        };

        // Add attribute fields
        foreach (var field in layerInfo.Fields)
        {
            if (field.IsObjectId || IsGeometryField(field))
                continue; // We use fid instead

            var pgType = MapEsriTypeToPgType(field.Type, field.Length);
            var nullable = field.Nullable ? "" : " NOT NULL";
            columns.Add($"\"{field.Name.SanitizeFieldName()}\" {pgType}{nullable}");
        }

        // Add geometry column if the layer has geometry
        if (!string.IsNullOrEmpty(layerInfo.GeometryType))
        {
            var pgGeomType = MapEsriGeometryType(layerInfo.GeometryType);
            columns.Add($"geom geometry({pgGeomType}, {targetSrid})");
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE TABLE {QuoteIdentifier(tableName)} (");

        for (var i = 0; i < columns.Count; i++)
        {
            var suffix = i == columns.Count - 1 ? string.Empty : ",";
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {columns[i]}{suffix}");
        }

        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string MapEsriTypeToPgType(string esriType, int? length)
    {
        return esriType.ToPostgresType(length);
    }

    private static string MapEsriGeometryType(string esriGeometryType)
    {
        return esriGeometryType.ToUpperInvariant() switch
        {
            "ESRIGEOMETRYPOINT" => "POINT",
            "ESRIGEOMETRYMULTIPOINT" => "MULTIPOINT",
            "ESRIGEOMETRYPOLYLINE" => "MULTILINESTRING",
            "ESRIGEOMETRYPOLYGON" => "MULTIPOLYGON",
            "ESRIGEOMETRYENVELOPE" => "POLYGON",
            _ => "GEOMETRY"
        };
    }

    private static bool IsGeometryField(GeoservicesFieldInfo field)
        => field.Type.Equals("esriFieldTypeGeometry", StringComparison.OrdinalIgnoreCase);


    private async Task<(int inserted, int failed)> InsertFeaturesAsync(
        NpgsqlConnection connection,
        string tableName,
        GeoservicesLayerInfo layerInfo,
        ArcGisFeature[] features,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        var inserted = 0;
        var failed = 0;
        string? firstError = null;
        var higherDimensionCount = 0;

        // Build insert statement
        var fields = layerInfo.Fields.Where(f => !f.IsObjectId && !IsGeometryField(f)).ToArray();
        var hasGeometry = !string.IsNullOrEmpty(layerInfo.GeometryType);

        var columnNames = string.Join(", ", fields.Select(f => $"\"{f.Name.SanitizeFieldName()}\""));
        if (hasGeometry)
        {
            columnNames += ", geom";
        }

        var parameterPlaceholders = string.Join(", ", fields.Select((_, i) => $"@p{i}"));
        if (hasGeometry)
        {
            parameterPlaceholders += $", ST_SetSRID(ST_GeomFromGeoJSON(@geom), {targetSrid})";
        }

        var insertSql = $"INSERT INTO {QuoteIdentifier(tableName)} ({columnNames}) VALUES ({parameterPlaceholders})";

        // Create the command once, add parameters with placeholder values, and prepare
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = insertSql;

        for (var i = 0; i < fields.Length; i++)
        {
            cmd.Parameters.AddWithValue($"p{i}", DBNull.Value);
        }

        if (hasGeometry)
        {
            cmd.Parameters.Add("geom", NpgsqlDbType.Text).Value = DBNull.Value;
        }

        await cmd.PrepareAsync(cancellationToken);

        foreach (var feature in features)
        {
            try
            {
                // Update attribute parameter values
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    object? value = null;

                    if (feature.Attributes?.TryGetValue(field.Name, out var jsonValue) == true)
                    {
                        value = ConvertJsonValue(jsonValue, field.Type);
                    }

                    cmd.Parameters[$"p{i}"].Value = value ?? DBNull.Value;
                }

                // Update geometry parameter value
                if (hasGeometry && feature.Geometry.HasValue)
                {
                    if (HasHigherDimensionCoordinates(feature.Geometry.Value))
                    {
                        higherDimensionCount++;
                    }

                    var geoJson = ConvertEsriGeometryToGeoJson(feature.Geometry.Value);
                    if (geoJson is null)
                    {
                        Log.GeometryConversionFailed(_logger, tableName);
                    }

                    cmd.Parameters["geom"].Value = geoJson ?? (object)DBNull.Value;
                }
                else if (hasGeometry)
                {
                    cmd.Parameters["geom"].Value = DBNull.Value;
                }

                await cmd.ExecuteNonQueryAsync(cancellationToken);
                inserted++;
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                Log.FeatureInsertFailed(_logger, ex.Message);
                failed++;
            }
        }

        if (firstError is not null)
        {
            Log.FeatureInsertFailures(_logger, failed, firstError);
        }

        if (higherDimensionCount > 0)
        {
            Log.HigherDimensionGeometryDetected(_logger, higherDimensionCount, tableName);
        }

        return (inserted, failed);
    }

    private static object? ConvertJsonValue(JsonElement element, string esriType)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;

        return esriType.ToUpperInvariant() switch
        {
            "ESRIFIELDTYPEOID" or "ESRIFIELDTYPEINTEGER" or "ESRIFIELDTYPESMALLINTEGER" =>
                element.ValueKind == JsonValueKind.Number ? element.GetInt32() : null,

            "ESRIFIELDTYPEDOUBLE" or "ESRIFIELDTYPESINGLE" =>
                element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null,

            "ESRIFIELDTYPESTRING" =>
                element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),

            "ESRIFIELDTYPEDATE" =>
                element.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(element.GetInt64())
                    : null,

            "ESRIFIELDTYPEGUID" or "ESRIFIELDTYPEGLOBALID" =>
                element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var guid)
                    ? guid
                    : null,

            _ => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
        };
    }

    private static string? ConvertEsriGeometryToGeoJson(JsonElement geometry)
    {
        // Esri JSON geometry format is similar to GeoJSON but not identical
        // This converts common geometry types to GeoJSON
        try
        {
            if (geometry.TryGetProperty("x", out var x) && geometry.TryGetProperty("y", out var y))
            {
                // Point
                return BuildPointGeoJson(x.GetDouble(), y.GetDouble());
            }

            if (geometry.TryGetProperty("rings", out var rings))
            {
                // Polygon
                var coordinates = rings.EnumerateArray()
                    .Select(ring => ring.EnumerateArray()
                        .Where(coord => coord.GetArrayLength() >= 2)
                        .Select(coord => new[] { coord[0].GetDouble(), coord[1].GetDouble() })
                        .ToArray())
                    .Where(ring => ring.Length >= 4)
                    .ToArray();

                if (coordinates.Length == 0)
                    return null;

                return BuildMultiPolygonGeoJson(coordinates);
            }

            if (geometry.TryGetProperty("paths", out var paths))
            {
                // Polyline
                var coordinates = paths.EnumerateArray()
                    .Select(path => path.EnumerateArray()
                        .Where(coord => coord.GetArrayLength() >= 2)
                        .Select(coord => new[] { coord[0].GetDouble(), coord[1].GetDouble() })
                        .ToArray())
                    .Where(path => path.Length >= 2)
                    .ToArray();

                if (coordinates.Length == 0)
                    return null;

                return BuildMultiLineStringGeoJson(coordinates);
            }

            if (geometry.TryGetProperty("points", out var points))
            {
                // Multipoint
                var coordinates = points.EnumerateArray()
                    .Where(p => p.GetArrayLength() >= 2)
                    .Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() })
                    .ToArray();

                if (coordinates.Length == 0)
                    return null;

                return BuildMultiPointGeoJson(coordinates);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasHigherDimensionCoordinates(JsonElement geometry)
    {
        try
        {
            // Point: check for z property
            if (geometry.TryGetProperty("z", out _))
                return true;

            // Rings (polygon), paths (polyline), points (multipoint): check coordinate array lengths
            foreach (var propName in new[] { "rings", "paths" })
            {
                if (geometry.TryGetProperty(propName, out var arrays))
                {
                    foreach (var array in arrays.EnumerateArray())
                    {
                        foreach (var coord in array.EnumerateArray())
                        {
                            if (coord.GetArrayLength() > 2)
                                return true;
                        }
                    }
                }
            }

            if (geometry.TryGetProperty("points", out var pts))
            {
                foreach (var coord in pts.EnumerateArray())
                {
                    if (coord.GetArrayLength() > 2)
                        return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string BuildPointGeoJson(double x, double y)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Point");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            writer.WriteNumberValue(x);
            writer.WriteNumberValue(y);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string BuildMultiPolygonGeoJson(double[][][] rings)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MultiPolygon");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            writer.WriteStartArray();
            foreach (var ring in rings)
            {
                writer.WriteStartArray();
                foreach (var coord in ring)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(coord[0]);
                    writer.WriteNumberValue(coord[1]);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string BuildMultiLineStringGeoJson(double[][][] lines)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MultiLineString");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            foreach (var line in lines)
            {
                writer.WriteStartArray();
                foreach (var coord in line)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(coord[0]);
                    writer.WriteNumberValue(coord[1]);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string BuildMultiPointGeoJson(double[][] points)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MultiPoint");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            foreach (var point in points)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(point[0]);
                writer.WriteNumberValue(point[1]);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string BuildGeoJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        write(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private async Task CreateSpatialIndexAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier(tableName + "_geom_idx")} ON {QuoteIdentifier(tableName)} USING GIST (geom)";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        Log.SpatialIndexCreated(_logger, tableName);
    }

    private async Task AnalyzeTableAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ANALYZE {QuoteIdentifier(tableName)}";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is NpgsqlConnection npgsqlConnection)
        {
            return npgsqlConnection;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException("Expected NpgsqlConnection for Geoservices import.");
    }

    private static void ReportProgress(
        IProgress<GeoservicesImportProgress>? progress,
        string jobId,
        DateTimeOffset startedAt,
        GeoservicesImportStatus status,
        GeoservicesImportRequest request,
        string phase,
        int featuresProcessed,
        int? totalFeatures,
        string? layerName = null)
    {
        progress?.Report(new GeoservicesImportProgress
        {
            JobId = jobId,
            Status = status,
            FeaturesProcessed = featuresProcessed,
            EstimatedTotalFeatures = totalFeatures,
            SourceServiceUrl = request.ServiceUrl,
            SourceLayerId = request.LayerId,
            SourceLayerName = layerName,
            TableName = request.TableName,
            StartedAt = startedAt,
            CurrentPhase = phase
        });
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_]*$")]
    private static partial Regex TableNameRegex();

    private static void ValidateTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be null or empty", nameof(tableName));

        if (tableName.Length > 63)
            throw new ArgumentException("Table name exceeds PostgreSQL identifier limit of 63 characters", nameof(tableName));

        if (!TableNameRegex().IsMatch(tableName))
            throw new ArgumentException("Table name must start with a letter and contain only letters, digits, and underscores", nameof(tableName));
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private static partial class Log
    {
        [LoggerMessage(7820, LogLevel.Information,
            "Starting Geoservices import from {ServiceUrl} layer {LayerId} to table {TableName}")]
        public static partial void ImportStarting(ILogger logger, string serviceUrl, int layerId, string tableName);

        [LoggerMessage(7821, LogLevel.Information,
            "Layer discovered: {LayerName}, {FieldCount} fields, ~{FeatureCount} features")]
        public static partial void LayerDiscovered(ILogger logger, string layerName, int fieldCount, int? featureCount);

        [LoggerMessage(7822, LogLevel.Debug, "Table {TableName} created")]
        public static partial void TableCreated(ILogger logger, string tableName);

        [LoggerMessage(7823, LogLevel.Debug,
            "Batch {BatchNumber} completed: {Inserted} inserted, {Failed} failed, {Total} total")]
        public static partial void BatchCompleted(ILogger logger, int batchNumber, int inserted, int failed, int total);

        [LoggerMessage(7824, LogLevel.Debug, "Spatial index created on {TableName}")]
        public static partial void SpatialIndexCreated(ILogger logger, string tableName);

        [LoggerMessage(7825, LogLevel.Information,
            "Import completed: {TableName}, {FeatureCount} features, {FailedCount} failed, {DurationSeconds:F1}s")]
        public static partial void ImportCompleted(
            ILogger logger, string tableName, int featureCount, int failedCount, double durationSeconds);

        [LoggerMessage(7826, LogLevel.Warning, "Import cancelled: {TableName}")]
        public static partial void ImportCancelled(ILogger logger, string tableName);

        [LoggerMessage(7827, LogLevel.Error, "Import failed: {TableName}")]
        public static partial void ImportFailed(ILogger logger, string tableName, Exception exception);

        [LoggerMessage(7828, LogLevel.Debug, "Feature insert failed: {ErrorMessage}")]
        public static partial void FeatureInsertFailed(ILogger logger, string errorMessage);

        [LoggerMessage(7829, LogLevel.Warning,
            "Geoservices import encountered {FailedCount} insert failures. First error: {ErrorMessage}")]
        public static partial void FeatureInsertFailures(ILogger logger, int failedCount, string errorMessage);

        [LoggerMessage(7830, LogLevel.Warning,
            "Geometry conversion failed for feature with non-null geometry in table {TableName}")]
        public static partial void GeometryConversionFailed(ILogger logger, string tableName);

        [LoggerMessage(7831, LogLevel.Warning,
            "Batch contains {Count} features with higher-dimension (Z/M) coordinates that will be dropped during 2D import in table {TableName}")]
        public static partial void HigherDimensionGeometryDetected(ILogger logger, int count, string tableName);
    }
}

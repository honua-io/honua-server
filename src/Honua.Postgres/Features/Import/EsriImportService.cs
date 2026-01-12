// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
internal sealed partial class EsriImportService : IEsriImportService
{
    private readonly ArcGisRestClient _restClient;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<EsriImportService> _logger;

    public EsriImportService(
        ArcGisRestClient restClient,
        IDatabaseConnectionProvider connectionProvider,
        ILogger<EsriImportService> logger)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<EsriServiceInfo> DiscoverServiceAsync(
        EsriDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        return _restClient.DiscoverServiceAsync(
            request.ServiceUrl,
            request.TimeoutSeconds,
            ResiliencePolicyOptions.Default.MaxRetryAttempts,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<EsriImportResult> ImportLayerAsync(
        EsriImportRequest request,
        CancellationToken cancellationToken = default)
    {
        return ImportLayerAsync(request, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EsriImportResult> ImportLayerAsync(
        EsriImportRequest request,
        IProgress<EsriImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var jobId = Guid.NewGuid().ToString("N")[..8];

        Log.ImportStarting(_logger, request.ServiceUrl, request.LayerId, request.TableName);

        try
        {
            // Phase 1: Discover layer metadata
            ReportProgress(progress, jobId, EsriImportStatus.Discovering, request,
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
            ReportProgress(progress, jobId, EsriImportStatus.CreatingTable, request,
                "Creating PostGIS table", 0, totalFeatures, layerInfo.Name);

            await CreateTableAsync(request.TableName, layerInfo, request.TargetSrid,
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
                ReportProgress(progress, jobId, EsriImportStatus.RetrievingFeatures, request,
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
                ReportProgress(progress, jobId, EsriImportStatus.InsertingFeatures, request,
                    $"Inserting batch {batchNumber} ({queryResult.Features.Length} features)",
                    featuresProcessed, totalFeatures, layerInfo.Name);

                var (inserted, failed) = await InsertFeaturesAsync(
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
            ReportProgress(progress, jobId, EsriImportStatus.Publishing, request,
                "Creating spatial index", featuresProcessed, totalFeatures, layerInfo.Name);

            await CreateSpatialIndexAsync(request.TableName, cancellationToken);
            await AnalyzeTableAsync(request.TableName, cancellationToken);

            stopwatch.Stop();

            Log.ImportCompleted(_logger, request.TableName, featuresProcessed, failedFeatures,
                stopwatch.Elapsed.TotalSeconds);

            // Report final progress
            ReportProgress(progress, jobId, EsriImportStatus.Completed, request,
                "Import completed", featuresProcessed, featuresProcessed, layerInfo.Name);

            return EsriImportResult.CreateSuccess(
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
            Log.ImportCancelled(_logger, request.TableName);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.ImportFailed(_logger, request.TableName, ex);

            return EsriImportResult.CreateFailure(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                "Import from ArcGIS service failed.",
                stopwatch.Elapsed);
        }
    }

    private async Task CreateTableAsync(
        string tableName,
        EsriLayerInfo layerInfo,
        int targetSrid,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        if (overwriteExisting)
        {
            await using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = $"DROP TABLE IF EXISTS \"{tableName}\" CASCADE";
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var createSql = BuildCreateTableSql(tableName, layerInfo, targetSrid);
        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = createSql;
        await createCmd.ExecuteNonQueryAsync(cancellationToken);

        Log.TableCreated(_logger, tableName);
    }

    private static string BuildCreateTableSql(string tableName, EsriLayerInfo layerInfo, int targetSrid)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE TABLE \"{tableName}\" (");
        sb.AppendLine("    fid SERIAL PRIMARY KEY,");

        // Add attribute fields
        foreach (var field in layerInfo.Fields)
        {
            if (field.IsObjectId)
                continue; // We use fid instead

            var pgType = MapEsriTypeToPgType(field.Type, field.Length);
            var nullable = field.Nullable ? "" : " NOT NULL";
            sb.AppendLine(CultureInfo.InvariantCulture, $"    \"{field.Name.SanitizeFieldName()}\" {pgType}{nullable},");
        }

        // Add geometry column if the layer has geometry
        if (!string.IsNullOrEmpty(layerInfo.GeometryType))
        {
            var pgGeomType = MapEsriGeometryType(layerInfo.GeometryType);
            sb.AppendLine(CultureInfo.InvariantCulture, $"    geom geometry({pgGeomType}, {targetSrid})");
        }
        else
        {
            // Remove trailing comma from last field
            sb.Length -= 3;
            sb.AppendLine();
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


    private async Task<(int inserted, int failed)> InsertFeaturesAsync(
        string tableName,
        EsriLayerInfo layerInfo,
        ArcGisFeature[] features,
        int targetSrid,
        CancellationToken cancellationToken)
    {
        var inserted = 0;
        var failed = 0;

        await using var connection = await OpenConnectionAsync(cancellationToken);

        // Build insert statement
        var fields = layerInfo.Fields.Where(f => !f.IsObjectId).ToArray();
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

        var insertSql = $"INSERT INTO \"{tableName}\" ({columnNames}) VALUES ({parameterPlaceholders})";

        foreach (var feature in features)
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = insertSql;

                // Add attribute parameters
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    object? value = null;

                    if (feature.Attributes?.TryGetValue(field.Name, out var jsonValue) == true)
                    {
                        value = ConvertJsonValue(jsonValue, field.Type);
                    }

                    cmd.Parameters.AddWithValue($"p{i}", value ?? DBNull.Value);
                }

                // Add geometry parameter
                if (hasGeometry && feature.Geometry.HasValue)
                {
                    var geoJson = ConvertEsriGeometryToGeoJson(feature.Geometry.Value);
                    cmd.Parameters.AddWithValue("geom", NpgsqlDbType.Jsonb, geoJson ?? (object)DBNull.Value);
                }
                else if (hasGeometry)
                {
                    cmd.Parameters.AddWithValue("geom", DBNull.Value);
                }

                await cmd.ExecuteNonQueryAsync(cancellationToken);
                inserted++;
            }
            catch (Exception ex)
            {
                Log.FeatureInsertFailed(_logger, ex.Message);
                failed++;
            }
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
                        .Select(coord => new[] { coord[0].GetDouble(), coord[1].GetDouble() })
                        .ToArray())
                    .ToArray();

                return BuildMultiPolygonGeoJson(coordinates);
            }

            if (geometry.TryGetProperty("paths", out var paths))
            {
                // Polyline
                var coordinates = paths.EnumerateArray()
                    .Select(path => path.EnumerateArray()
                        .Select(coord => new[] { coord[0].GetDouble(), coord[1].GetDouble() })
                        .ToArray())
                    .ToArray();

                return BuildMultiLineStringGeoJson(coordinates);
            }

            if (geometry.TryGetProperty("points", out var points))
            {
                // Multipoint
                var coordinates = points.EnumerateArray()
                    .Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() })
                    .ToArray();

                return BuildMultiPointGeoJson(coordinates);
            }

            return null;
        }
        catch
        {
            return null;
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

    private async Task CreateSpatialIndexAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS \"{tableName}_geom_idx\" ON \"{tableName}\" USING GIST (geom)";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        Log.SpatialIndexCreated(_logger, tableName);
    }

    private async Task AnalyzeTableAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ANALYZE \"{tableName}\"";
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
        throw new InvalidOperationException("Expected NpgsqlConnection for Esri import.");
    }

    private static void ReportProgress(
        IProgress<EsriImportProgress>? progress,
        string jobId,
        EsriImportStatus status,
        EsriImportRequest request,
        string phase,
        int featuresProcessed,
        int? totalFeatures,
        string? layerName = null)
    {
        progress?.Report(new EsriImportProgress
        {
            JobId = jobId,
            Status = status,
            FeaturesProcessed = featuresProcessed,
            EstimatedTotalFeatures = totalFeatures,
            SourceServiceUrl = request.ServiceUrl,
            SourceLayerId = request.LayerId,
            SourceLayerName = layerName,
            TableName = request.TableName,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = phase
        });
    }

    private static partial class Log
    {
        [LoggerMessage(7800, LogLevel.Information,
            "Starting Esri import from {ServiceUrl} layer {LayerId} to table {TableName}")]
        public static partial void ImportStarting(ILogger logger, string serviceUrl, int layerId, string tableName);

        [LoggerMessage(7801, LogLevel.Information,
            "Layer discovered: {LayerName}, {FieldCount} fields, ~{FeatureCount} features")]
        public static partial void LayerDiscovered(ILogger logger, string layerName, int fieldCount, int? featureCount);

        [LoggerMessage(7802, LogLevel.Debug, "Table {TableName} created")]
        public static partial void TableCreated(ILogger logger, string tableName);

        [LoggerMessage(7803, LogLevel.Debug,
            "Batch {BatchNumber} completed: {Inserted} inserted, {Failed} failed, {Total} total")]
        public static partial void BatchCompleted(ILogger logger, int batchNumber, int inserted, int failed, int total);

        [LoggerMessage(7804, LogLevel.Debug, "Spatial index created on {TableName}")]
        public static partial void SpatialIndexCreated(ILogger logger, string tableName);

        [LoggerMessage(7805, LogLevel.Information,
            "Import completed: {TableName}, {FeatureCount} features, {FailedCount} failed, {DurationSeconds:F1}s")]
        public static partial void ImportCompleted(
            ILogger logger, string tableName, int featureCount, int failedCount, double durationSeconds);

        [LoggerMessage(7806, LogLevel.Warning, "Import cancelled: {TableName}")]
        public static partial void ImportCancelled(ILogger logger, string tableName);

        [LoggerMessage(7807, LogLevel.Error, "Import failed: {TableName}")]
        public static partial void ImportFailed(ILogger logger, string tableName, Exception exception);

        [LoggerMessage(7809, LogLevel.Debug, "Feature insert failed: {ErrorMessage}")]
        public static partial void FeatureInsertFailed(ILogger logger, string errorMessage);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Service for importing data from ArcGIS Server services into PostGIS.
/// </summary>
internal sealed partial class GeoservicesImportService : IGeoservicesImportService
{
    private readonly ArcGisRestClient _restClient;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsRegistry _crsRegistry;
    private readonly ILayerPublishingService? _layerPublishingService;
    private readonly ILogger<GeoservicesImportService> _logger;
    private readonly PostgresSchemaConfiguration _schemaConfiguration;

    public GeoservicesImportService(
        ArcGisRestClient restClient,
        IDatabaseConnectionProvider connectionProvider,
        ICrsRegistry crsRegistry,
        ILogger<GeoservicesImportService> logger,
        ILayerPublishingService? layerPublishingService = null,
        PostgresSchemaConfiguration? schemaConfiguration = null)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        _layerPublishingService = layerPublishingService;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _schemaConfiguration = schemaConfiguration ?? new PostgresSchemaConfiguration(
            PostgresSchemaConfiguration.DefaultMetadataSchema,
            PostgresSchemaConfiguration.DefaultDataSchema,
            [PostgresSchemaConfiguration.DefaultDataSchema, "public"]);
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
            cancellationToken,
            request.Credentials);
    }

    /// <inheritdoc />
    public Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        CancellationToken cancellationToken = default)
    {
        return ImportLayerAsync(request, null, cancellationToken);
    }

    private async Task CreateSpatialIndexAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier(tableName + "_geom_idx")} ON {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} USING GIST (geom)";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        Log.SpatialIndexCreated(_logger, tableName);
    }

    private static async Task AnalyzeTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ANALYZE {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)}";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private Task<NpgsqlConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken)
        => _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken);

    private string ResolveTargetSchema(string? requestedSchema)
    {
        var schema = string.IsNullOrWhiteSpace(requestedSchema)
            ? _schemaConfiguration.DefaultOperationalSchema
            : requestedSchema.Trim();

        if (!SchemaSearchPath.IsValidIdentifier(schema))
        {
            throw new ArgumentException("Target schema contains invalid characters.", nameof(requestedSchema));
        }

        return schema;
    }

    private async Task<PublishedLayerSummary?> TryPublishImportedLayerAsync(
        GeoservicesImportRequest request,
        string targetSchema,
        GeoservicesLayerInfo layerInfo,
        List<string> warnings,
        IProgress<GeoservicesImportProgress>? progress,
        string jobId,
        DateTimeOffset startedAt,
        int featuresProcessed,
        CancellationToken cancellationToken)
    {
        if (_layerPublishingService == null)
        {
            warnings.Add("AutoPublish was requested, but no layer publishing service is registered for this server.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.ServiceName))
        {
            warnings.Add("AutoPublish was requested, but no target serviceName was supplied; the imported table was not published.");
            return null;
        }

        try
        {
            ReportProgress(
                progress,
                jobId,
                startedAt,
                GeoservicesImportStatus.Publishing,
                request,
                "Publishing imported layer",
                featuresProcessed,
                featuresProcessed,
                layerInfo.Name);

            var publishRequest = new LayerPublishRequest
            {
                Schema = targetSchema,
                Table = request.TableName,
                LayerName = string.IsNullOrWhiteSpace(layerInfo.Name) ? request.TableName : layerInfo.Name,
                Description = layerInfo.Description,
                GeometryColumn = "geom",
                GeometryType = string.IsNullOrWhiteSpace(layerInfo.GeometryType)
                    ? null
                    : MapEsriGeometryType(layerInfo.GeometryType),
                Srid = request.TargetSrid,
                PrimaryKey = FieldNames.ObjectId,
                Fields = [],
                ServiceName = request.ServiceName,
                Enabled = true
            };

            return await _layerPublishingService.PublishLayerAsync(
                    _connectionProvider.GetConnectionString(),
                    publishRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LayerPublishingException ex)
        {
            warnings.Add($"AutoPublish was requested, but publishing did not complete: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AutoPublishFailed(_logger, request.TableName, request.ServiceName!, ex);
            warnings.Add("AutoPublish was requested, but publishing did not complete.");
            return null;
        }
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
        string? layerName = null,
        int? publishedLayerId = null)
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
            ServiceName = request.ServiceName,
            PublishedLayerId = publishedLayerId,
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

        [LoggerMessage(78215, LogLevel.Warning,
            "GeoServices inventory scan failed for {ServiceUrl}")]
        public static partial void InventoryScanFailed(ILogger logger, string serviceUrl, Exception exception);

        [LoggerMessage(78216, LogLevel.Warning,
            "GeoServices inventory resource scan failed for {ServiceUrl} resource {ResourceId} ({ResourceKind})")]
        public static partial void InventoryResourceScanFailed(
            ILogger logger,
            string serviceUrl,
            int resourceId,
            string resourceKind,
            Exception exception);

        [LoggerMessage(78217, LogLevel.Debug,
            "GeoServices feature count was unavailable for {ServiceUrl} resource {ResourceId}")]
        public static partial void InventoryFeatureCountFailed(
            ILogger logger,
            string serviceUrl,
            int resourceId,
            Exception exception);

        [LoggerMessage(78218, LogLevel.Debug,
            "GeoServices inventory captured {FieldCount} fields for {ServiceUrl} resource {ResourceId}")]
        public static partial void InventoryFieldsExtracted(
            ILogger logger,
            string serviceUrl,
            int resourceId,
            int fieldCount);

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

        [LoggerMessage(7832, LogLevel.Warning, "Auto-publish failed for imported table {TableName} into service {ServiceName}")]
        public static partial void AutoPublishFailed(ILogger logger, string tableName, string serviceName, Exception exception);
    }
}

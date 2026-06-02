// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Service for importing data from ArcGIS Server services into PostGIS.
/// </summary>
internal sealed partial class GeoservicesImportService : IGeoservicesImportService
{
    private readonly ArcGisRestClient _restClient;
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsRegistry _crsRegistry;
    private readonly IEsriConstructCapabilityRegistry _constructCapabilityRegistry;
    private readonly ILayerPublishingService? _layerPublishingService;
    private readonly IGeoServicesStyleConverter? _styleConverter;
    private readonly ILayerStyleCatalog? _styleCatalog;
    private readonly ILogger<GeoservicesImportService> _logger;
    private readonly PostgresSchemaConfiguration _schemaConfiguration;

    public GeoservicesImportService(
        ArcGisRestClient restClient,
        IDatabaseConnectionProvider connectionProvider,
        ICrsRegistry crsRegistry,
        IEsriConstructCapabilityRegistry constructCapabilityRegistry,
        ILogger<GeoservicesImportService> logger,
        ILayerPublishingService? layerPublishingService = null,
        IGeoServicesStyleConverter? styleConverter = null,
        ILayerStyleCatalog? styleCatalog = null,
        PostgresSchemaConfiguration? schemaConfiguration = null)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        _constructCapabilityRegistry = constructCapabilityRegistry ?? throw new ArgumentNullException(nameof(constructCapabilityRegistry));
        _layerPublishingService = layerPublishingService;
        _styleConverter = styleConverter;
        _styleCatalog = styleCatalog;
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
                Enabled = true,
                FieldDomains = BuildFieldDomainMap(layerInfo),
                // Carry the captured Esri subtype set through publish. The subtype field
                // is the canonical 'type' column on the imported layer; the publish path
                // only attaches the subtypes when that column is actually published, so a
                // subtype set referencing a dropped column never false-fails reconciliation.
                Subtypes = layerInfo.Subtypes
            };

            var published = await _layerPublishingService.PublishLayerAsync(
                    _connectionProvider.GetConnectionString(),
                    publishRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            await TryAttachLayerStyleAsync(
                    published,
                    layerInfo,
                    request,
                    warnings,
                    cancellationToken)
                .ConfigureAwait(false);

            return published;
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

    // Projects the captured per-field Esri domains onto the publish request, keyed
    // by source field name. Coded-value domains over the capture cap are not present
    // here (the source parser omitted them and the inventory raised the truncation
    // warning), so they intentionally publish without a domain rather than as a
    // misleading partial lookup.
    private static Dictionary<string, MetadataV2FieldDomain> BuildFieldDomainMap(
        GeoservicesLayerInfo layerInfo)
    {
        var map = new Dictionary<string, MetadataV2FieldDomain>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in layerInfo.Fields)
        {
            if (field.Domain is { } domain && !string.IsNullOrWhiteSpace(field.Name))
            {
                map[field.Name] = domain;
            }
        }

        return map;
    }

    private async Task TryAttachLayerStyleAsync(
        PublishedLayerSummary published,
        GeoservicesLayerInfo layerInfo,
        GeoservicesImportRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (_styleConverter == null || _styleCatalog == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(layerInfo.DrawingInfoJson))
        {
            return;
        }

        JsonElement drawingInfoElement;
        JsonDocument? drawingInfoDocument = null;
        try
        {
            drawingInfoDocument = JsonDocument.Parse(layerInfo.DrawingInfoJson);
            drawingInfoElement = drawingInfoDocument.RootElement.Clone();
        }
        catch (JsonException)
        {
            warnings.Add(
                "Source service returned a malformed 'drawingInfo' payload; the published layer was left unstyled.");
            return;
        }
        finally
        {
            drawingInfoDocument?.Dispose();
        }

        try
        {
            var geometryType = MapEsriGeometryTypeToMetadataV2(layerInfo.GeometryType);
            var conversion = _styleConverter.Convert(
                drawingInfoElement,
                published.LayerId,
                published.LayerName,
                geometryType);

            await _styleCatalog
                .SetStyleAsync(
                    published.LayerId,
                    conversion.MapLibreStyleJson,
                    layerInfo.DrawingInfoJson!,
                    revisedBy: GeoservicesStyleRevisedBy,
                    changeSummary: $"Style attached during Geoservices auto-publish from {request.ServiceUrl}",
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var unsupported in conversion.Unsupported)
            {
                Log.AutoPublishStyleUnsupportedSymbolizer(
                    _logger,
                    published.LayerId,
                    unsupported.Code,
                    unsupported.SymbolizerType);
                warnings.Add(BuildUnsupportedSymbolizerWarning(unsupported));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AutoPublishStyleAttachFailed(_logger, published.LayerId, ex);
            warnings.Add(
                "AutoPublish succeeded, but attaching the converted MapLibre style to the published layer failed; drawingInfo was not persisted.");
        }
    }

    private const string GeoservicesStyleRevisedBy = "geoservices-import";

    private static string BuildUnsupportedSymbolizerWarning(UnsupportedSymbolizerInfo unsupported)
    {
        if (string.IsNullOrWhiteSpace(unsupported.SymbolizerType))
        {
            return $"Imported renderer carried unsupported input ({unsupported.Code}): {unsupported.Guidance}";
        }

        return $"Imported renderer carried unsupported input ({unsupported.Code}, '{unsupported.SymbolizerType}'): {unsupported.Guidance}";
    }

    private static MetadataV2GeometryType MapEsriGeometryTypeToMetadataV2(string? esriGeometryType)
    {
        if (string.IsNullOrWhiteSpace(esriGeometryType))
        {
            return MetadataV2GeometryType.None;
        }

        return esriGeometryType.Trim().ToUpperInvariant() switch
        {
            "ESRIGEOMETRYPOINT" => MetadataV2GeometryType.Point,
            "ESRIGEOMETRYMULTIPOINT" => MetadataV2GeometryType.MultiPoint,
            "ESRIGEOMETRYPOLYLINE" => MetadataV2GeometryType.MultiLineString,
            "ESRIGEOMETRYLINE" => MetadataV2GeometryType.LineString,
            "ESRIGEOMETRYPOLYGON" => MetadataV2GeometryType.MultiPolygon,
            "ESRIGEOMETRYENVELOPE" => MetadataV2GeometryType.Polygon,
            _ => MetadataV2GeometryType.None
        };
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

        [LoggerMessage(7833, LogLevel.Warning,
            "Auto-publish style attach failed for layer {LayerId}; drawingInfo and MapLibre style were not persisted")]
        public static partial void AutoPublishStyleAttachFailed(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(7834, LogLevel.Information,
            "Geoservices auto-publish flagged unsupported style input {Code} ('{SymbolizerType}') on layer {LayerId}")]
        public static partial void AutoPublishStyleUnsupportedSymbolizer(
            ILogger logger,
            int layerId,
            string code,
            string symbolizerType);
    }
}

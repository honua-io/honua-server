// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Migration;

/// <summary>
/// Service for importing data from ArcGIS Server services into PostGIS.
/// </summary>
internal sealed partial class GeoservicesImportService : IGeoservicesImportService
{
    private readonly ArcGisRestClient _restClient;
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsRegistry _crsRegistry;
    private readonly IEsriConstructCapabilityRegistry _constructCapabilityRegistry;
    private readonly IAttachmentStore? _attachmentStore;
    private readonly IMigrationCatalogWriter? _catalogWriter;
    private readonly GeoservicesLayerPublicationService _layerPublicationService;
    private readonly ILogger<GeoservicesImportService> _logger;
    private readonly PostgresSchemaConfiguration _schemaConfiguration;

    public GeoservicesImportService(
        ArcGisRestClient restClient,
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ICrsRegistry crsRegistry,
        IEsriConstructCapabilityRegistry constructCapabilityRegistry,
        ILogger<GeoservicesImportService> logger,
        GeoservicesLayerPublicationService layerPublicationService,
        IAttachmentStore? attachmentStore = null,
        IMigrationCatalogWriter? catalogWriter = null,
        PostgresSchemaConfiguration? schemaConfiguration = null)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        _constructCapabilityRegistry = constructCapabilityRegistry ?? throw new ArgumentNullException(nameof(constructCapabilityRegistry));
        // The published-layer lifecycle (AutoPublish -> style attach -> post-publish reconciliation)
        // is owned by a dedicated delegating service so this importer stays within the collaborator
        // ceiling. It is always supplied (DI-registered); its own collaborators are individually
        // optional so publishing/style/reconciliation each no-op when their dependency is absent.
        _layerPublicationService = layerPublicationService ?? throw new ArgumentNullException(nameof(layerPublicationService));
        _attachmentStore = attachmentStore;
        _catalogWriter = catalogWriter;
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

    /// <summary>
    /// Emits a progress report for an import phase. Shared with
    /// <see cref="GeoservicesLayerPublicationService"/> so the publish/reconcile phases stay on the
    /// same ordered progress channel as the discover/insert/index phases.
    /// </summary>
    internal static void ReportProgress(
        IProgress<GeoservicesImportProgress>? progress,
        string jobId,
        DateTimeOffset startedAt,
        GeoservicesImportStatus status,
        GeoservicesImportRequest request,
        string phase,
        int featuresProcessed,
        int? totalFeatures,
        string? layerName = null,
        int? publishedLayerId = null,
        int attachmentsProcessed = 0,
        int failedAttachments = 0,
        MigrationReconciliationArtifact? reconciliationArtifact = null,
        MigrationCatalogReconciliationReport? catalogReconciliationReport = null)
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
            CurrentPhase = phase,
            AttachmentsProcessed = attachmentsProcessed,
            FailedAttachments = failedAttachments,
            ReconciliationArtifact = reconciliationArtifact,
            CatalogReconciliationReport = catalogReconciliationReport
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

        [LoggerMessage(7835, LogLevel.Information,
            "Starting attachment copy for layer {LayerId}: {FeatureCount} parent features in scope")]
        public static partial void AttachmentCopyStarting(ILogger logger, int layerId, int featureCount);

        [LoggerMessage(7836, LogLevel.Information,
            "Attachment copy completed for layer {LayerId}: {AttachmentCount} copied, {FailedCount} failed")]
        public static partial void AttachmentCopyCompleted(
            ILogger logger, int layerId, int attachmentCount, int failedCount);

        [LoggerMessage(7837, LogLevel.Warning,
            "Failed to copy attachment {AttachmentId} for source feature {SourceObjectId} in layer {LayerId}")]
        public static partial void AttachmentCopyFailed(
            ILogger logger, long attachmentId, long sourceObjectId, int layerId, Exception exception);

        [LoggerMessage(7838, LogLevel.Warning,
            "Attachment metadata query failed for layer {LayerId} batch of {BatchSize} ObjectIds")]
        public static partial void AttachmentQueryBatchFailed(
            ILogger logger, int layerId, int batchSize, Exception exception);

        [LoggerMessage(7839, LogLevel.Debug, "Relationship apply skipped: {Reason}")]
        public static partial void RelationshipApplySkipped(ILogger logger, string reason);

        [LoggerMessage(7840, LogLevel.Warning,
            "Reconciliation gate routed import of table {TableName} to NeedsReview: {Reason}")]
        public static partial void ReconciliationGateBlocked(ILogger logger, string tableName, string reason);

        [LoggerMessage(7841, LogLevel.Warning,
            "Reconciliation gate could not run for table {TableName}; import completed without a reconciliation verdict")]
        public static partial void ReconciliationGateUnavailable(ILogger logger, string tableName, Exception exception);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Db.Postgres.Features.Migration;

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
        // Derived like PostgreSQL's own implicit names. A plain "<table>_geom_idx" is truncated to 63
        // bytes, which for a 63-byte table name is the table's own name, so IF NOT EXISTS silently
        // skipped the index (#4600).
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier(PostgresDerivedRelationNames.Build(tableName, "geom", "idx"))} ON {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} USING GIST (geom)";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        Log.SpatialIndexCreated(_logger, tableName);
    }

    /// <summary>True when a relation (table, index or sequence) with this name exists in the schema.</summary>
    private static async Task<bool> RelationExistsAsync(
        NpgsqlConnection connection,
        string schemaName,
        string relationName,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@qualifiedName) IS NOT NULL";
        cmd.Parameters.AddWithValue("qualifiedName", $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(relationName)}");
        return await cmd.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// Takes the import lease for one target table without waiting. The lease is a session-level
    /// advisory lock on the import connection, so it outlives the data commit and also covers
    /// publishing, attachment copy and reconciliation. <see cref="ReleaseTargetImportLockAsync"/> ends
    /// it, and a dropped connection (a crashed worker) releases it. Returns <c>false</c> when another
    /// import already holds the lease.
    /// </summary>
    private static async Task<bool> TryAcquireTargetImportLockAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(hashtextextended(@lockKey, 0))";
        cmd.Parameters.AddWithValue("lockKey", BuildTargetImportLockKey(schemaName, tableName));
        return await cmd.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// Ends the lease taken by <see cref="TryAcquireTargetImportLockAsync"/>. Runs after the import
    /// transaction has committed or rolled back, so the connection is never in an aborted transaction.
    /// </summary>
    private static async Task ReleaseTargetImportLockAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT pg_advisory_unlock(hashtextextended(@lockKey, 0))";
        cmd.Parameters.AddWithValue("lockKey", BuildTargetImportLockKey(schemaName, tableName));
        await cmd.ExecuteScalarAsync(CancellationToken.None);
    }

    private static string BuildTargetImportLockKey(string schemaName, string tableName)
        => $"honua.geoservices-import:{QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)}";

    /// <summary>
    /// Fences writes to the prior target for the rest of the import transaction. EXCLUSIVE mode
    /// conflicts with INSERT/UPDATE/DELETE but not with SELECT, so readers keep the prior rows while
    /// an edit waits for the swap instead of committing against a table the swap then discards.
    /// </summary>
    private static async Task FenceTargetWritesAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"LOCK TABLE {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} IN EXCLUSIVE MODE";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Name prefix of the staging table a replacement import loads into.</summary>
    internal const string StagingTablePrefix = "honua_import_stage_";

    /// <summary>
    /// Staging table for a replacement import. Derived from the job identity and the target, so two
    /// concurrent jobs never share one, and short enough that PostgreSQL's derived <c>_pkey</c> and
    /// <c>_objectid_seq</c> names are never truncated.
    /// </summary>
    internal static string BuildStagingTableName(string tableName, string jobId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{jobId}\n{tableName}"));
        return StagingTablePrefix + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// Replaces the target with the fully loaded staging table inside the import transaction. The
    /// exclusive lock on the prior target is taken here, at the end of the transfer, not at its start.
    /// </summary>
    private static async Task SwapStagingIntoTargetAsync(
        NpgsqlConnection connection,
        string schemaName,
        string stagingTable,
        string tableName,
        CancellationToken cancellationToken)
    {
        var schema = QuoteIdentifier(schemaName);
        var target = QuoteIdentifier(tableName);
        await using (var swap = connection.CreateCommand())
        {
            swap.CommandText =
                $"DROP TABLE {schema}.{target} CASCADE; "
                + $"ALTER TABLE {schema}.{QuoteIdentifier(stagingTable)} RENAME TO {target};";
            await swap.ExecuteNonQueryAsync(cancellationToken);
        }

        // The rename keeps the staging-derived primary-key and sequence names. Give them the names a
        // first import of the target creates, unless another relation already holds that name.
        await RenameDerivedRelationAsync(
            connection,
            schemaName,
            PostgresDerivedRelationNames.Build(stagingTable, null, "pkey"),
            PostgresDerivedRelationNames.Build(tableName, null, "pkey"),
            (current, renamed) => $"ALTER TABLE {schema}.{target} RENAME CONSTRAINT {current} TO {renamed}",
            cancellationToken);
        await RenameDerivedRelationAsync(
            connection,
            schemaName,
            PostgresDerivedRelationNames.Build(stagingTable, FieldNames.ObjectId, "seq"),
            PostgresDerivedRelationNames.Build(tableName, FieldNames.ObjectId, "seq"),
            (current, renamed) => $"ALTER SEQUENCE {schema}.{current} RENAME TO {renamed}",
            cancellationToken);
    }

    private static async Task RenameDerivedRelationAsync(
        NpgsqlConnection connection,
        string schemaName,
        string currentName,
        string renamedName,
        Func<string, string, string> buildRenameSql,
        CancellationToken cancellationToken)
    {
        if (!await RelationExistsAsync(connection, schemaName, currentName, cancellationToken)
            || await RelationExistsAsync(connection, schemaName, renamedName, cancellationToken))
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = buildRenameSql(QuoteIdentifier(currentName), QuoteIdentifier(renamedName));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
        MigrationCatalogReconciliationReport? catalogReconciliationReport = null,
        string? fidelityVerdict = null,
        MigrationFidelityDifference[]? fidelityDifferences = null)
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
            CatalogReconciliationReport = catalogReconciliationReport,
            FidelityVerdict = fidelityVerdict,
            FidelityDifferences = fidelityDifferences ?? []
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

        [LoggerMessage(7842, LogLevel.Warning,
            "Migration fidelity difference on table {TableName}: [{Code}/{Severity}] {Summary}")]
        public static partial void FidelityDifference(
            ILogger logger, string tableName, string code, string severity, string summary);

        [LoggerMessage(7843, LogLevel.Warning,
            "Replacement of table {TableName} refused: {FailedCount} source records failed to load; the prior target was retained")]
        public static partial void ReplacementRefused(ILogger logger, string tableName, int failedCount);

        [LoggerMessage(7844, LogLevel.Warning,
            "Import into table {TableName} refused: another import is already writing the target")]
        public static partial void TargetImportInProgress(ILogger logger, string tableName);

        [LoggerMessage(7845, LogLevel.Warning,
            "Source population for table {TableName} could not be read; source changes during the transfer are unverified")]
        public static partial void SourcePopulationUnavailable(ILogger logger, string tableName, Exception exception);

        [LoggerMessage(7846, LogLevel.Warning,
            "Import lease on table {TableName} could not be released explicitly; it ends with the connection")]
        public static partial void TargetLeaseReleaseFailed(ILogger logger, string tableName, Exception exception);
    }
}

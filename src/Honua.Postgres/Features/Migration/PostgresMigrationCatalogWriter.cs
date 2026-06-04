// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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
/// PostgreSQL implementation of <see cref="IMigrationCatalogWriter"/>. Persists
/// migrated workspace and layer-group records into <c>honua.services</c> using
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> so re-running an apply is idempotent.
/// </summary>
internal sealed partial class PostgresMigrationCatalogWriter : IMigrationCatalogWriter
{
    private static readonly string[] _defaultFormats = ["JSON", "GeoJSON"];
    private static readonly string[] _defaultCapabilities = ["Query", "Extract"];

    private readonly ILogger<PostgresMigrationCatalogWriter> _logger;

    public PostgresMigrationCatalogWriter(ILogger<PostgresMigrationCatalogWriter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MigrationCatalogWriteOutcome> EnsureCatalogServiceAsync(
        string connectionString,
        MigrationCatalogServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ServiceName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent upsert: ON CONFLICT (service_name) DO NOTHING lets the apply
        // plan re-run safely against the same target without duplicating catalog rows.
        const string sql = """
            INSERT INTO honua.services (
                service_name,
                description,
                srid,
                supported_formats,
                capabilities
            )
            VALUES (@serviceName, @description, @srid, @formats, @capabilities)
            ON CONFLICT (service_name) DO NOTHING
            RETURNING service_name;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@serviceName", request.ServiceName);
        command.Parameters.AddWithValue("@description", request.Description);
        command.Parameters.AddWithValue("@srid", request.Srid);
        command.Parameters.AddWithValue("@formats", _defaultFormats);
        command.Parameters.AddWithValue("@capabilities", _defaultCapabilities);

        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var outcome = inserted is null
            ? MigrationCatalogWriteOutcome.AlreadyExists
            : MigrationCatalogWriteOutcome.Created;

        Log.CatalogServicePersisted(_logger, request.EntryKind, request.ServiceName, outcome.ToString());
        return outcome;
    }

    public async Task<MigrationCatalogWriteOutcome> EnsureDataSourceAsync(
        string connectionString,
        MigrationDataSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DataSourceType);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent upsert on (source_kind, source_id). Slice 2 deliberately
        // does not overwrite existing rows because the source-of-truth for a
        // migrated data source is the source system being scanned, not the
        // Honua catalog.
        const string sql = """
            INSERT INTO honua.migration_data_sources (
                source_kind,
                source_id,
                data_source_type,
                workspace_name,
                display_name,
                connection_summary
            )
            VALUES (@sourceKind, @sourceId, @dataSourceType, @workspaceName, @displayName, @connectionSummary)
            ON CONFLICT (source_kind, source_id) DO NOTHING
            RETURNING source_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceKind", request.SourceKind);
        command.Parameters.AddWithValue("@sourceId", request.SourceId);
        command.Parameters.AddWithValue("@dataSourceType", request.DataSourceType);
        command.Parameters.AddWithValue("@workspaceName", (object?)request.WorkspaceName ?? DBNull.Value);
        command.Parameters.AddWithValue("@displayName", request.DisplayName ?? request.SourceId);
        command.Parameters.AddWithValue("@connectionSummary", request.ConnectionSummary);

        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var outcome = inserted is null
            ? MigrationCatalogWriteOutcome.AlreadyExists
            : MigrationCatalogWriteOutcome.Created;

        Log.DataSourcePersisted(_logger, request.DataSourceType, request.SourceId, outcome.ToString());
        return outcome;
    }

    public async Task<MigrationFeatureCopyOutcome> CopyFeatureDataAsync(
        string connectionString,
        MigrationFeatureCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetTable);

        // Defense in depth: never interpolate identifiers that did not pass the
        // safe-identifier guard. Callers should already have validated, but
        // double-check here so the writer never composes hostile SQL.
        if (!IsSafeIdentifier(request.SourceSchema) ||
            !IsSafeIdentifier(request.SourceTable) ||
            !IsSafeIdentifier(request.TargetSchema) ||
            !IsSafeIdentifier(request.TargetTable))
        {
            throw new ArgumentException("Identifiers must be ASCII letter/digit/underscore only.", nameof(request));
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Ensure the data schema exists.
        await using (var ensureSchema = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS \"{request.TargetSchema}\";",
            connection))
        {
            await ensureSchema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Confirm the source table exists in this database. If not, return
        // SourceMissing so the executor can record a manual-review step.
        const string sourceExistsSql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table
            LIMIT 1;
            """;
        await using (var sourceCheck = new NpgsqlCommand(sourceExistsSql, connection))
        {
            sourceCheck.Parameters.AddWithValue("@schema", request.SourceSchema);
            sourceCheck.Parameters.AddWithValue("@table", request.SourceTable);
            var found = await sourceCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                Log.FeatureCopySourceMissing(_logger, request.SourceSchema, request.SourceTable);
                return new MigrationFeatureCopyOutcome
                {
                    Status = MigrationFeatureCopyStatus.SourceMissing,
                    RowCount = 0
                };
            }
        }

        // Idempotency: if the target already exists, just report the current
        // row count without copying again. ON CONFLICT-style row-level guards
        // are unnecessary because we never partial-fill the target table here.
        const string targetExistsSql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = @schema AND table_name = @table
            LIMIT 1;
            """;
        bool targetExists;
        await using (var targetCheck = new NpgsqlCommand(targetExistsSql, connection))
        {
            targetCheck.Parameters.AddWithValue("@schema", request.TargetSchema);
            targetCheck.Parameters.AddWithValue("@table", request.TargetTable);
            targetExists = (await targetCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) is not null;
        }

        var targetIdent = $"\"{request.TargetSchema}\".\"{request.TargetTable}\"";
        var sourceIdent = $"\"{request.SourceSchema}\".\"{request.SourceTable}\"";

        if (targetExists)
        {
            var existingCount = await CountRowsAsync(connection, targetIdent, cancellationToken).ConfigureAwait(false);
            Log.FeatureCopyAlreadyApplied(_logger, request.TargetSchema, request.TargetTable, existingCount);
            return new MigrationFeatureCopyOutcome
            {
                Status = MigrationFeatureCopyStatus.AlreadyApplied,
                RowCount = existingCount
            };
        }

        // First apply: create the target table with the same columns as the source
        // (including geometry typmod / SRID) then copy all rows. Wrap the
        // structure + data steps in a transaction so a failure mid-copy leaves
        // no half-populated table behind.
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using (var createCmd = new NpgsqlCommand(
                $"CREATE TABLE {targetIdent} (LIKE {sourceIdent} INCLUDING DEFAULTS INCLUDING CONSTRAINTS);",
                connection,
                transaction))
            {
                await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var copyCmd = new NpgsqlCommand(
                $"INSERT INTO {targetIdent} SELECT * FROM {sourceIdent};",
                connection,
                transaction))
            {
                await copyCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var rowCount = await CountRowsAsync(connection, targetIdent, cancellationToken).ConfigureAwait(false);
        Log.FeatureCopyCompleted(_logger, request.SourceSchema, request.SourceTable, request.TargetSchema, request.TargetTable, rowCount);
        return new MigrationFeatureCopyOutcome
        {
            Status = MigrationFeatureCopyStatus.Copied,
            RowCount = rowCount
        };
    }

    public async Task<MigrationCatalogWriteOutcome> EnsureStyleAsync(
        string connectionString,
        MigrationStyleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetStyleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StyleName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent upsert on (source_kind, source_id). Slice 3 deliberately
        // does not overwrite existing rows because the source-of-truth for a
        // migrated style is the source system being scanned; operators who
        // need to refresh diagnostics should delete the row and re-apply.
        const string sql = """
            INSERT INTO honua.migration_styles (
                source_kind,
                source_id,
                workspace_name,
                style_name,
                source_format,
                source_language_version,
                target_style_id,
                source_body,
                converted_body,
                converted_format,
                diagnostics,
                review_disposition
            )
            VALUES (
                @sourceKind,
                @sourceId,
                @workspaceName,
                @styleName,
                @sourceFormat,
                @sourceLanguageVersion,
                @targetStyleId,
                @sourceBody,
                @convertedBody,
                @convertedFormat,
                CAST(@diagnostics AS jsonb),
                @reviewDisposition
            )
            ON CONFLICT (source_kind, source_id) DO NOTHING
            RETURNING source_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceKind", request.SourceKind);
        command.Parameters.AddWithValue("@sourceId", request.SourceId);
        command.Parameters.AddWithValue("@workspaceName", (object?)request.WorkspaceName ?? DBNull.Value);
        command.Parameters.AddWithValue("@styleName", request.StyleName);
        command.Parameters.AddWithValue("@sourceFormat", request.SourceFormat);
        command.Parameters.AddWithValue("@sourceLanguageVersion", (object?)request.SourceLanguageVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("@targetStyleId", request.TargetStyleId);
        command.Parameters.AddWithValue("@sourceBody", (object?)request.SourceBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@convertedBody", (object?)request.ConvertedBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@convertedFormat", (object?)request.ConvertedFormat ?? DBNull.Value);
        command.Parameters.AddWithValue("@diagnostics", string.IsNullOrWhiteSpace(request.DiagnosticsJson) ? "[]" : request.DiagnosticsJson);
        command.Parameters.AddWithValue("@reviewDisposition", request.ReviewDisposition);

        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var outcome = inserted is null
            ? MigrationCatalogWriteOutcome.AlreadyExists
            : MigrationCatalogWriteOutcome.Created;

        Log.StylePersisted(_logger, request.SourceFormat, request.SourceId, request.ReviewDisposition, outcome.ToString());
        return outcome;
    }

    public async Task<MigrationRelationshipApplyOutcome[]> EnsureRelationshipsAsync(
        string connectionString,
        IMetadataV2GraphStore? graphStore,
        MigrationRelationshipApplyRequest[] requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Length == 0)
        {
            return [];
        }

        var orderedRequests = requests
            .OrderBy(static r => r.OriginLayerId)
            .ThenBy(static r => r.SourceRelationshipId, StringComparer.Ordinal)
            .ToArray();

        var graphOutcomes = graphStore == null
            ? new Dictionary<string, GraphRelationshipOutcome>(StringComparer.Ordinal)
            : await ApplyRelationshipGraphPatchAsync(graphStore, orderedRequests, cancellationToken).ConfigureAwait(false);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<MigrationRelationshipApplyOutcome>(orderedRequests.Length);
        var v1Created = 0;
        var v1AlreadyExists = 0;

        foreach (var request in orderedRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // V1 honua.relationships row: required for the FeatureServer
            // queryRelatedRecords path that reads from honua.relationships via the
            // shared IRelationshipStore. The unique key (layer_id, relationship_id)
            // keeps re-apply idempotent.
            var v1Outcome = await InsertRelationshipRowAsync(connection, request, cancellationToken).ConfigureAwait(false);
            if (v1Outcome == MigrationCatalogWriteOutcome.Created)
            {
                v1Created++;
            }
            else
            {
                v1AlreadyExists++;
            }

            graphOutcomes.TryGetValue(request.SourceRelationshipId, out var graphOutcome);
            var combined = CombineRelationshipOutcomes(graphOutcome, v1Outcome, graphStore != null);
            results.Add(new MigrationRelationshipApplyOutcome
            {
                SourceRelationshipId = request.SourceRelationshipId,
                Outcome = combined.Outcome,
                Message = combined.Message,
                TargetRelationshipRef = BuildTargetRelationshipRef(request)
            });
        }

        Log.RelationshipsPersisted(_logger, v1Created, v1AlreadyExists, graphStore == null ? "skipped" : "applied");
        return results.ToArray();
    }

    private static async Task<Dictionary<string, GraphRelationshipOutcome>> ApplyRelationshipGraphPatchAsync(
        IMetadataV2GraphStore graphStore,
        MigrationRelationshipApplyRequest[] orderedRequests,
        CancellationToken cancellationToken)
    {
        // Group requests by origin layer so each resource's relationships list is
        // patched once per save instead of saving per relationship. Each save
        // increments the graph revision, so coalescing keeps the snapshot small
        // and avoids needless cache churn.
        const int MaxAttempts = 2;
        var outcomes = new Dictionary<string, GraphRelationshipOutcome>(StringComparer.Ordinal);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var graph = snapshot.Graph;
            var requestsByOrigin = orderedRequests
                .GroupBy(static r => r.OriginLayerId)
                .OrderBy(static g => g.Key);

            var updatedResources = graph.Resources.ToList();
            var resourcesById = updatedResources
                .Select(static (resource, index) => (resource, index))
                .ToDictionary(item => item.resource.Metadata.Id, item => item.index, StringComparer.Ordinal);

            var graphChanged = false;
            var perRequestOutcomes = new Dictionary<string, GraphRelationshipOutcome>(StringComparer.Ordinal);

            foreach (var group in requestsByOrigin)
            {
                var originResourceId = BuildResourceId(group.Key);
                if (!resourcesById.TryGetValue(originResourceId, out var index))
                {
                    foreach (var req in group)
                    {
                        perRequestOutcomes[req.SourceRelationshipId] = new GraphRelationshipOutcome(
                            MigrationCatalogWriteOutcome.AlreadyExists,
                            $"Origin resource '{originResourceId}' not present in the Metadata v2 graph; v2 relationship entry skipped.");
                    }
                    continue;
                }

                var origin = updatedResources[index];
                var existingRelationships = origin.Relationships.ToList();
                var existingIds = new HashSet<string>(existingRelationships.Select(static r => r.Id), StringComparer.Ordinal);

                foreach (var req in group)
                {
                    var relationshipId = BuildRelationshipId(req);
                    if (existingIds.Contains(relationshipId))
                    {
                        perRequestOutcomes[req.SourceRelationshipId] = new GraphRelationshipOutcome(
                            MigrationCatalogWriteOutcome.AlreadyExists,
                            $"Relationship '{relationshipId}' already present on resource '{originResourceId}'; v2 graph entry unchanged.");
                        continue;
                    }

                    existingRelationships.Add(BuildRelationshipMetadata(req, relationshipId));
                    existingIds.Add(relationshipId);
                    perRequestOutcomes[req.SourceRelationshipId] = new GraphRelationshipOutcome(
                        MigrationCatalogWriteOutcome.Created,
                        $"Appended relationship '{relationshipId}' to resource '{originResourceId}' in the Metadata v2 graph.");
                    graphChanged = true;
                }

                if (graphChanged)
                {
                    updatedResources[index] = origin with { Relationships = existingRelationships };
                }
            }

            if (!graphChanged)
            {
                foreach (var kv in perRequestOutcomes)
                {
                    outcomes[kv.Key] = kv.Value;
                }
                return outcomes;
            }

            var updatedGraph = graph with
            {
                Revision = Math.Max(graph.Revision + 1, 1),
                GeneratedAt = graph.GeneratedAt,
                Resources = updatedResources
            };

            try
            {
                await graphStore.SaveAsync(updatedGraph, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                foreach (var kv in perRequestOutcomes)
                {
                    outcomes[kv.Key] = kv.Value;
                }
                return outcomes;
            }
            catch (InvalidOperationException) when (attempt < MaxAttempts)
            {
                // Optimistic concurrency conflict: another writer beat us. Re-read
                // the snapshot and rebuild the patch. We retry once; if a second
                // attempt also fails the exception surfaces to the caller so the
                // apply step can record a failed outcome.
            }
        }

        return outcomes;
    }

    private static async Task<MigrationCatalogWriteOutcome> InsertRelationshipRowAsync(
        NpgsqlConnection connection,
        MigrationRelationshipApplyRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO honua.relationships (
                layer_id,
                relationship_id,
                name,
                related_layer_id,
                relationship_type,
                origin_foreign_key,
                destination_foreign_key,
                description
            )
            VALUES (@layerId, @relationshipId, @name, @relatedLayerId, @relationshipType,
                    @originForeignKey, @destinationForeignKey, NULL)
            ON CONFLICT (layer_id, relationship_id) DO NOTHING
            RETURNING relationship_id;
            """;

        // honua.relationships has CHECK constraints on relationship_id > 0 and
        // relationship_type ∈ {esriRelRoleOrigin, esriRelRoleDestination,
        // esriRelRoleAny}. Stable-hash the source identifier when the source did
        // not advertise an integer id so the row still satisfies the schema.
        var relationshipId = request.EsriRelationshipId ?? DeriveStableRelationshipId(request.SourceRelationshipId);
        var relationshipType = NormalizeRelationshipRole(request.Role);
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? $"rel-{relationshipId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : request.Name!;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@layerId", request.OriginLayerId);
        command.Parameters.AddWithValue("@relationshipId", relationshipId);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@relatedLayerId", request.RelatedLayerId);
        command.Parameters.AddWithValue("@relationshipType", relationshipType);
        command.Parameters.AddWithValue("@originForeignKey", request.OriginKeyField);
        command.Parameters.AddWithValue("@destinationForeignKey", request.DestinationKeyField);

        var inserted = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return inserted is null
            ? MigrationCatalogWriteOutcome.AlreadyExists
            : MigrationCatalogWriteOutcome.Created;
    }

    private static (MigrationCatalogWriteOutcome Outcome, string Message) CombineRelationshipOutcomes(
        GraphRelationshipOutcome? graphOutcome,
        MigrationCatalogWriteOutcome v1Outcome,
        bool graphRequested)
    {
        var v1Message = v1Outcome == MigrationCatalogWriteOutcome.Created
            ? "Inserted honua.relationships row."
            : "honua.relationships row already present.";

        if (!graphRequested)
        {
            return (
                v1Outcome,
                $"{v1Message} Metadata v2 graph write skipped (no graph store provided).");
        }

        if (graphOutcome is null)
        {
            return (
                MigrationCatalogWriteOutcome.AlreadyExists,
                $"{v1Message} Metadata v2 graph write skipped: no matching origin resource.");
        }

        var combinedOutcome = graphOutcome.Outcome == MigrationCatalogWriteOutcome.Created ||
                              v1Outcome == MigrationCatalogWriteOutcome.Created
            ? MigrationCatalogWriteOutcome.Created
            : MigrationCatalogWriteOutcome.AlreadyExists;

        return (combinedOutcome, $"{graphOutcome.Message} {v1Message}");
    }

    private static MetadataV2Relationship BuildRelationshipMetadata(
        MigrationRelationshipApplyRequest request,
        string relationshipId)
    {
        return new MetadataV2Relationship
        {
            Id = relationshipId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? relationshipId : request.Name!,
            RelatedResourceId = BuildResourceId(request.RelatedLayerId),
            Role = string.IsNullOrWhiteSpace(request.Role) ? "origin" : request.Role!,
            Cardinality = MapCardinalityToV2(request.Cardinality),
            OriginField = request.OriginKeyField,
            DestinationField = request.DestinationKeyField,
            EsriRelationshipId = request.EsriRelationshipId
        };
    }

    private static string BuildRelationshipId(MigrationRelationshipApplyRequest request)
    {
        var idPart = request.EsriRelationshipId.HasValue
            ? request.EsriRelationshipId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : SanitizeRelationshipIdSuffix(request.SourceRelationshipId);
        return $"rel-{request.OriginLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{idPart}";
    }

    private static string BuildTargetRelationshipRef(MigrationRelationshipApplyRequest request)
        => BuildRelationshipId(request);

    private static string BuildResourceId(int layerId)
        => $"res-layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string MapCardinalityToV2(string normalizedCardinality)
    {
        return normalizedCardinality switch
        {
            "1:1" => "one-to-one",
            "1:N" => "one-to-many",
            "M:N" => "many-to-many",
            _ => "one-to-many"
        };
    }

    private static string NormalizeRelationshipRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return "esriRelRoleOrigin";
        }

        var trimmed = role.Trim();
        if (trimmed.Equals("origin", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("esriRelRoleOrigin", StringComparison.OrdinalIgnoreCase))
        {
            return "esriRelRoleOrigin";
        }
        if (trimmed.Equals("destination", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("esriRelRoleDestination", StringComparison.OrdinalIgnoreCase))
        {
            return "esriRelRoleDestination";
        }
        if (trimmed.Equals("any", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("esriRelRoleAny", StringComparison.OrdinalIgnoreCase))
        {
            return "esriRelRoleAny";
        }

        return "esriRelRoleOrigin";
    }

    private static int DeriveStableRelationshipId(string sourceRelationshipId)
    {
        // honua.relationships CHECK requires relationship_id > 0; the FNV-1a 32-bit
        // hash gives us a deterministic positive integer when the source did not
        // advertise an integer relationship id.
        const uint Prime = 16777619;
        var hash = 2166136261u;
        foreach (var ch in sourceRelationshipId)
        {
            hash ^= ch;
            hash *= Prime;
        }

        var shifted = (int)(hash & 0x7FFFFFFFu);
        return shifted == 0 ? 1 : shifted;
    }

    private static string SanitizeRelationshipIdSuffix(string sourceRelationshipId)
    {
        var builder = new System.Text.StringBuilder(sourceRelationshipId.Length);
        foreach (var ch in sourceRelationshipId)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }
        var result = builder.ToString().Trim('-');
        return string.IsNullOrEmpty(result) ? "unknown" : result;
    }

    private sealed record GraphRelationshipOutcome(MigrationCatalogWriteOutcome Outcome, string Message);

    private static async Task<long> CountRowsAsync(NpgsqlConnection connection, string identifier, CancellationToken cancellationToken)
    {
        await using var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {identifier};", connection);
        var raw = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw switch
        {
            long l => l,
            int i => i,
            _ => Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static bool IsSafeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        foreach (var ch in identifier)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(7960, LogLevel.Information, "Migration catalog writer ensured {EntryKind} service '{ServiceName}' ({Outcome})")]
        public static partial void CatalogServicePersisted(ILogger logger, string entryKind, string serviceName, string outcome);

        [LoggerMessage(7961, LogLevel.Information, "Migration catalog writer ensured {DataSourceType} data source '{SourceId}' ({Outcome})")]
        public static partial void DataSourcePersisted(ILogger logger, string dataSourceType, string sourceId, string outcome);

        [LoggerMessage(7962, LogLevel.Information, "Migration feature copy completed: {SourceSchema}.{SourceTable} -> {TargetSchema}.{TargetTable} ({RowCount} rows)")]
        public static partial void FeatureCopyCompleted(ILogger logger, string sourceSchema, string sourceTable, string targetSchema, string targetTable, long rowCount);

        [LoggerMessage(7963, LogLevel.Information, "Migration feature copy already applied: {TargetSchema}.{TargetTable} present ({RowCount} rows)")]
        public static partial void FeatureCopyAlreadyApplied(ILogger logger, string targetSchema, string targetTable, long rowCount);

        [LoggerMessage(7964, LogLevel.Warning, "Migration feature copy source missing: {SourceSchema}.{SourceTable}")]
        public static partial void FeatureCopySourceMissing(ILogger logger, string sourceSchema, string sourceTable);

        [LoggerMessage(7965, LogLevel.Information, "Migration catalog writer ensured {SourceFormat} style '{SourceId}' (disposition={Disposition}, {Outcome})")]
        public static partial void StylePersisted(ILogger logger, string sourceFormat, string sourceId, string disposition, string outcome);

        [LoggerMessage(7966, LogLevel.Information, "Migration catalog writer ensured relationships: created={Created}, already-exists={AlreadyExists}, v2-graph={GraphStatus}")]
        public static partial void RelationshipsPersisted(ILogger logger, int created, int alreadyExists, string graphStatus);
    }
}

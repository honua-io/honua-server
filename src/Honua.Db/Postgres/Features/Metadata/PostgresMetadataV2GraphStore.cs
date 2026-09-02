// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Metadata;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="IMetadataV2GraphStore"/>.
/// The canonical graph document is persisted as JSONB in <c>metadata_v2_snapshots</c>;
/// sidecar tables (resources/services/publications/storage_bindings/connections) are
/// refreshed in the same transaction. <c>metadata_v2_current</c> tracks the active
/// revision per environment.
/// </summary>
internal sealed class PostgresMetadataV2GraphStore : IMetadataV2GraphStore, IMetadataV2GraphWriteBaseReader
{
    // Shared with schema-coupled metadata publishers such as the demo STAC seed.
    // The environment hash is the second key so unrelated environments can publish concurrently.
    internal const int MetadataWriteLockNamespace = 144047714;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly IMetadataV2GraphCacheInvalidator? _cacheInvalidator;
    private readonly IDatabaseSchemaGuard _schemaGuard;
    private readonly string _environment;
    private readonly string _schemaName;
    private readonly string _snapshotsTable;
    private readonly string _currentTable;
    private readonly string _resourcesIdxTable;
    private readonly string _servicesIdxTable;
    private readonly string _publicationsIdxTable;
    private readonly string _storageBindingsIdxTable;
    private readonly string _connectionsIdxTable;
    private MetadataV2GraphSnapshot? _cachedCurrent;

    public PostgresMetadataV2GraphStore(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        string environment,
        IDatabaseSchemaGuard schemaGuard,
        string? schemaName = null,
        IMetadataV2GraphCacheInvalidator? cacheInvalidator = null)
    {
        ArgumentNullException.ThrowIfNull(connectionProvider);
        if (string.IsNullOrWhiteSpace(environment))
        {
            throw new ArgumentException("Environment must be set.", nameof(environment));
        }

        _connectionProvider = connectionProvider;
        _cacheInvalidator = cacheInvalidator;
        _schemaGuard = schemaGuard ?? throw new ArgumentNullException(nameof(schemaGuard));
        _environment = environment;
        _schemaName = string.IsNullOrWhiteSpace(schemaName) ? "honua" : schemaName.Trim();
        _snapshotsTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_snapshots", schemaName);
        _currentTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_current", schemaName);
        _resourcesIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_resources_idx", schemaName);
        _servicesIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_services_idx", schemaName);
        _publicationsIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_publications_idx", schemaName);
        _storageBindingsIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_storage_bindings_idx", schemaName);
        _connectionsIdxTable = Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_connections_idx", schemaName);
    }

    public async ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var current = await TryLoadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            // No Metadata v2 snapshot has been activated for this environment. Before the
            // v2 cutover every protocol read the legacy V1 catalog (honua.services /
            // honua.layers / the shared JSONB-attributes `features` table) directly, and
            // the SQL test seeds still synthesize a compat snapshot from it
            // (honua.seed_metadata_v2_compat_snapshot, tests/seed/base-schema.sql). A
            // production or CITE deployment that only ever populated the V1 catalog has no
            // activated snapshot, so throwing here regressed OGC API Features /collections
            // (and the collection-detail/items paths) to HTTP 500 alongside every other
            // protocol that resolves through this same shared metadata seam. Restore parity
            // by synthesizing the equivalent snapshot from the V1 catalog at read time.
            // (honua-server#1412.)
            current = await TryBuildCompatSnapshotFromV1CatalogAsync(cancellationToken).ConfigureAwait(false);

            // When both the V2 snapshot table and the V1 catalog are absent or empty the
            // server is freshly deployed with zero published datasets. Every catalog-style
            // endpoint (STAC /stac, GeoServices /rest/services, OGC API /collections, OData
            // service document, WFS GetCapabilities, …) already handles an empty list
            // gracefully and returns a valid empty response. Returning an empty-but-valid
            // snapshot here makes ALL of those surfaces return 200 with zero
            // items â€” the correct behaviour for a healthy but unpopulated server â€” instead
            // of surfacing a 500. A snapshot that EXISTS in the database but fails to load
            // (network/parse error) still 500s correctly because TryLoadCurrentAsync
            // propagates that exception rather than returning null. (honua-server#1619.)
            current ??= BuildEmptySnapshot();
        }

        if (_cachedCurrent is not null && _cachedCurrent.Etag == current.Etag)
        {
            return _cachedCurrent;
        }

        _cachedCurrent = current;
        return current;
    }

    /// <inheritdoc />
    public Task<MetadataV2GraphSnapshot?> TryGetPersistedCurrentAsync(CancellationToken cancellationToken = default)
        // Write/publish base load: only the genuinely-activated snapshot, never the V1
        // compat synthesis. See IMetadataV2GraphWriteBaseReader for why. (honua-server#1412.)
        => TryLoadCurrentAsync(cancellationToken);

    /// <summary>
    /// Synthesizes a Metadata v2 graph snapshot from the legacy V1 catalog when no
    /// snapshot has been activated for the environment. Read-only: it neither writes to
    /// the metadata_v2 tables nor activates a revision, so an operator's first real
    /// publish (which goes through <see cref="SaveAsync"/>) still takes precedence on the
    /// next read. Returns <c>null</c> when the V1 catalog has no published service layers
    /// (a truly empty database) or when the legacy tables do not exist. (honua-server#1412.)
    /// </summary>
    private async Task<MetadataV2GraphSnapshot?> TryBuildCompatSnapshotFromV1CatalogAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await TryBuildCompatSnapshotFromV1CatalogAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MetadataV2GraphSnapshot?> TryBuildCompatSnapshotFromV1CatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        // Read the V1 catalog from the same schema the store qualifies its v2 tables
        // with (validated + quoted to keep it injection-safe).
        var catalogSchema = Infrastructure.SchemaSearchPath.ValidateAndQuote(_schemaName);
        var sql = MetadataV2CompatSnapshotSql.BuildDocumentFromV1Catalog
            .Replace(MetadataV2CompatSnapshotSql.CatalogSchemaPlaceholder, catalogSchema, StringComparison.Ordinal);

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);

        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not string json || string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var snapshot = MaterializeSnapshot(json, ComputeEtag(json));

            // No published service layers => an empty graph (no services/resources). Treat
            // that as "no snapshot" so callers keep their existing not-found/empty handling
            // rather than serving an empty compat catalog.
            if (snapshot.Graph.Services.Count == 0 && snapshot.Graph.Resources.Count == 0)
            {
                return null;
            }

            return snapshot;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Legacy V1 catalog tables are absent (e.g. a v2-only fixture database). There
            // is no catalog to fall back to; let the caller surface "no snapshot".
            return null;
        }
    }

    private async Task<bool> HasV1CatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT to_regclass(format('%I.%I', @schema, 'layers')) IS NOT NULL
               AND to_regclass(format('%I.%I', @schema, 'service_layers')) IS NOT NULL
               AND to_regclass(format('%I.%I', @schema, 'services')) IS NOT NULL
               AND to_regclass(format('%I.%I', @schema, 'layer_fields')) IS NOT NULL;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("schema", _schemaName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    public async ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
        long revision, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT document, etag
            FROM {_snapshotsTable}
            WHERE environment = @environment AND revision = @revision
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await VerifySchemaFloorAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@environment", _environment);
        command.Parameters.AddWithValue("@revision", revision);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var json = reader.GetString(0);
        var etag = reader.GetString(1);
        return MaterializeSnapshot(json, etag);
    }

    public async Task<MetadataV2GraphSnapshot> SaveAsync(
        MetadataV2Graph graph,
        string? expectedEtag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var validation = MetadataV2GraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                $"Metadata v2 graph failed validation: {string.Join("; ", validation.Errors)}");
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await VerifySchemaFloorAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transactionId = await PostgresTransactionOutcomeObserver
            .CaptureTransactionIdAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        // Lock before reading metadata_v2_current. FOR UPDATE cannot lock an absent bootstrap
        // row, so the advisory lock is the authoritative serialization seam for both bootstrap
        // and established environments. Every publisher uses this lock/order before touching
        // the current pointer, preventing revision-1 overwrite and cross-writer deadlocks.
        await AcquireEnvironmentWriteLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var current = await ReadCurrentStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (expectedEtag is not null)
        {
            // A deployment without a v2 current row still returns an exact synthesized ETag:
            // either a V1 compatibility graph or the empty graph. Recompute that bootstrap
            // base after acquiring the authoritative environment lock so V1 catalog changes
            // are detected just like a stale v2 pointer. No other non-null ETag is accepted
            // without a v2 current.
            var actualEtag = current?.Etag;
            if (current is null)
            {
                var hasV1Catalog = await HasV1CatalogAsync(
                    connection, transaction, cancellationToken).ConfigureAwait(false);
                actualEtag = hasV1Catalog
                    ? (await TryBuildCompatSnapshotFromV1CatalogAsync(
                        connection, transaction, cancellationToken).ConfigureAwait(false))?.Etag
                    : null;
                actualEtag ??= BuildEmptySnapshot().Etag;
            }

            if (!string.Equals(actualEtag, expectedEtag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Metadata v2 etag mismatch for environment '{_environment}': expected {expectedEtag} but found {actualEtag ?? "<none>"}.");
            }
        }

        // Revisions are a store-owned allocation, not a caller-owned identifier. The
        // caller necessarily builds its document before this lock is acquired; another
        // publisher may therefore have consumed the proposed revision, and an interrupted
        // writer may have left a higher orphan snapshot. Allocate above every retained
        // snapshot while holding the shared environment lock.
        var revision = await ReadNextRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        graph = graph with { Revision = revision };
        var json = JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph);
        var etag = ComputeEtag(json);

        // Bootstrap reconciliation (honua-server#1395): when no current snapshot is
        // activated for this environment the caller (LoadCurrentOrEmptyGraphAsync) started
        // from an empty graph and is forcing a first write at a low revision. A shared or
        // partially-written database can still carry orphaned sidecar rows (e.g. a prior
        // RefreshSidecarsAsync that committed before metadata_v2_current did). On databases
        // created before migration 046 those rows also collided with the then-unique
        // idx_metadata_v2_services_name and surfaced as a raw Postgres 23505. When
        // bootstrapping, clear stale sidecar rows for the whole environment (all revisions)
        // rather than only the target (environment, revision), so the first write reconciles
        // cleanly instead of 500ing the layer-publish path and never leaves orphaned
        // revisions behind.
        var isBootstrap = current is null;

        await UpsertSnapshotAsync(connection, transaction, graph, json, etag, cancellationToken).ConfigureAwait(false);
        await RefreshSidecarsAsync(connection, transaction, graph, clearStaleEnvironmentRows: isBootstrap, cancellationToken).ConfigureAwait(false);
        await UpsertCurrentAsync(connection, transaction, graph.Revision, etag, cancellationToken).ConfigureAwait(false);

        var snapshot = new MetadataV2GraphSnapshot(graph, etag, DateTimeOffset.UtcNow);
        var commitWasReconciled = false;
        try
        {
            await FeatureDataAccess.CommitEditTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (FeatureEditCommitOutcomeUnknownException commitException)
        {
            // SaveAsync is the mutation-receipt boundary for every graph writer. If PostgreSQL made
            // this exact xid durable before the acknowledgement was lost, preserving the receipt lets
            // the caller either commit its paired catalog transaction or compensate this graph write.
            // A raw commit exception would leave the caller with no mutation identity and an orphan graph.
            var committed = await PostgresTransactionOutcomeObserver
                .TryObserveCommitAsync(_connectionProvider, transactionId)
                .ConfigureAwait(false);
            if (committed == false)
            {
                throw;
            }

            if (committed is null)
            {
                _cachedCurrent = null;
                _cacheInvalidator?.Invalidate(_environment);
                throw new MetadataV2GraphCommitOutcomeUnknownException(
                    snapshot,
                    transactionId,
                    commitException);
            }

            commitWasReconciled = true;
        }

        // A concurrent writer may have advanced current while the lost acknowledgement was being
        // reconciled. The xid proves this snapshot committed, but not that it is still current, so do
        // not regress the process-local cache to it in that path.
        _cachedCurrent = commitWasReconciled ? null : snapshot;

        // Drop the shared read-through snapshot cache for this environment so read surfaces
        // (MCP tools, REST/OGC metadata) observe the committed mutation immediately on this node
        // instead of waiting out the TTL. Other nodes remain TTL-bounded. This is the canonical
        // catalog write, so every mutation path (admin publish, migration/import, release
        // reconcilers) invalidates through here. (mcp A2 hot-path caching.)
        _cacheInvalidator?.Invalidate(_environment);

        return snapshot;
    }

    public async Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(
        long revision,
        string? expectedCurrentEtag,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await VerifySchemaFloorAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await AcquireEnvironmentWriteLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (expectedCurrentEtag is not null &&
            !string.Equals(current?.Etag, expectedCurrentEtag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Metadata v2 etag mismatch for environment '{_environment}': expected {expectedCurrentEtag} but found {current?.Etag ?? "<none>"}.");
        }

        var target = await ReadSnapshotAsync(connection, transaction, revision, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Metadata v2 revision {revision} is not retained for environment '{_environment}'.");

        // A bootstrap write can clear sidecars for every retained revision before it
        // activates its new snapshot. Rebuild the target revision's derived indexes from
        // the immutable document before repointing current so the activated graph and its
        // lookup surfaces become visible atomically without allocating a new revision.
        await RefreshSidecarsAsync(
            connection,
            transaction,
            target.Graph,
            clearStaleEnvironmentRows: false,
            cancellationToken).ConfigureAwait(false);
        await UpsertCurrentAsync(connection, transaction, revision, target.Etag, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);

        _cachedCurrent = target;
        _cacheInvalidator?.Invalidate(_environment);
        return target;
    }

    private async Task AcquireEnvironmentWriteLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT pg_advisory_xact_lock(@namespace, hashtext(@environment))";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@namespace", MetadataWriteLockNamespace);
        command.Parameters.AddWithValue("@environment", _environment);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MetadataV2GraphSnapshot?> TryLoadCurrentAsync(CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT s.document, s.etag
            FROM {_currentTable} c
            JOIN {_snapshotsTable} s ON s.environment = c.environment AND s.revision = c.revision
            WHERE c.environment = @environment
            """;
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await VerifySchemaFloorAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@environment", _environment);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var json = reader.GetString(0);
        var etag = reader.GetString(1);
        return MaterializeSnapshot(json, etag);
    }

    private Task VerifySchemaFloorAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        => _schemaGuard.VerifyRequirementAsync(
            connection,
            DatabaseSchemaRequirement.MetadataV2Snapshot,
            cancellationToken);

    private async Task<(long Revision, string Etag)?> ReadCurrentStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        // FOR UPDATE serializes concurrent SaveAsync calls on the environment's current row.
        // Without it the etag check is a TOCTOU race: two writers carrying the same
        // expectedEtag both pass the comparison, compute the same next revision, and the
        // second snapshot upsert silently overwrites the first writer's document. With the
        // row lock the loser blocks until the winner commits, observes the winner's etag,
        // and surfaces the existing mismatch InvalidOperationException instead.
        var sql = $"SELECT revision, etag FROM {_currentTable} WHERE environment = @environment FOR UPDATE";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private async Task<MetadataV2GraphSnapshot?> ReadSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long revision,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT document, etag FROM {_snapshotsTable} WHERE environment = @environment AND revision = @revision";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        command.Parameters.AddWithValue("@revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MaterializeSnapshot(reader.GetString(0), reader.GetString(1));
    }

    private async Task<long> ReadNextRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT COALESCE(MAX(revision), 0) + 1 FROM {_snapshotsTable} WHERE environment = @environment";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task UpsertSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataV2Graph graph,
        string json,
        string etag,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_snapshotsTable}
                (environment, revision, schema_version, api_version, document, etag, generated_at)
            VALUES
                (@environment, @revision, @schema_version, @api_version, @document, @etag, @generated_at)
            ON CONFLICT (environment, revision) DO UPDATE SET
                schema_version = EXCLUDED.schema_version,
                api_version    = EXCLUDED.api_version,
                document       = EXCLUDED.document,
                etag           = EXCLUDED.etag,
                generated_at   = EXCLUDED.generated_at
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        command.Parameters.AddWithValue("@revision", graph.Revision);
        command.Parameters.AddWithValue("@schema_version", graph.SchemaVersion);
        command.Parameters.AddWithValue("@api_version", graph.ApiVersion);
        command.Parameters.Add(new NpgsqlParameter("@document", NpgsqlDbType.Jsonb) { Value = json });
        command.Parameters.AddWithValue("@etag", etag);
        command.Parameters.AddWithValue("@generated_at", graph.GeneratedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long revision,
        string etag,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_currentTable} (environment, revision, etag)
            VALUES (@environment, @revision, @etag)
            ON CONFLICT (environment) DO UPDATE SET
                revision     = EXCLUDED.revision,
                etag         = EXCLUDED.etag,
                activated_at = NOW()
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        command.Parameters.AddWithValue("@revision", revision);
        command.Parameters.AddWithValue("@etag", etag);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshSidecarsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataV2Graph graph,
        bool clearStaleEnvironmentRows,
        CancellationToken cancellationToken)
    {
        // Wipe sidecars for this (environment, revision) and rewrite. Cheap and simple.
        // On a bootstrap write (no activated current snapshot) also clear any orphaned
        // rows for the whole environment so a stale partial write does not collide with
        // the unique service-name index. (honua-server#1395.)
        var revisionScope = clearStaleEnvironmentRows
            ? string.Empty
            : " AND revision = @revision";
        var deleteSql = new[]
        {
            $"DELETE FROM {_resourcesIdxTable} WHERE environment = @environment{revisionScope}",
            $"DELETE FROM {_servicesIdxTable} WHERE environment = @environment{revisionScope}",
            $"DELETE FROM {_publicationsIdxTable} WHERE environment = @environment{revisionScope}",
            $"DELETE FROM {_storageBindingsIdxTable} WHERE environment = @environment{revisionScope}",
            $"DELETE FROM {_connectionsIdxTable} WHERE environment = @environment{revisionScope}",
        };
        foreach (var sql in deleteSql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            if (!clearStaleEnvironmentRows)
            {
                cmd.Parameters.AddWithValue("@revision", graph.Revision);
            }
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var resource in graph.Resources)
        {
            var sql = $"""
                INSERT INTO {_resourcesIdxTable}
                    (environment, revision, resource_id, name, namespace, type, primary_storage_binding_id)
                VALUES
                    (@environment, @revision, @id, @name, @namespace, @type, @primary)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", resource.Metadata.Id);
            cmd.Parameters.AddWithValue("@name", resource.Metadata.Name);
            // Namespace column kept in the SQL schema for forward-compat;
            // MetadataV2ObjectMetadata.Namespace was removed in design slice 65/N.
            cmd.Parameters.AddWithValue("@namespace", DBNull.Value);
            cmd.Parameters.AddWithValue("@type", resource.Type.ToString());
            cmd.Parameters.AddWithValue("@primary", (object?)resource.PrimaryStorageBindingId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var service in graph.Services)
        {
            var sql = $"""
                INSERT INTO {_servicesIdxTable}
                    (environment, revision, service_id, name, service_type, route)
                VALUES
                    (@environment, @revision, @id, @name, @type, @route)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", service.Metadata.Id);
            cmd.Parameters.AddWithValue("@name", service.Metadata.Name);
            cmd.Parameters.AddWithValue("@type", (object?)service.PrimaryProtocol ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@route", (object?)service.Route ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var pub in graph.Publications)
        {
            var sql = $"""
                INSERT INTO {_publicationsIdxTable}
                    (environment, revision, publication_id, service_id, resource_id, storage_binding_id,
                     publication_type, path, layer_index, service_local_id)
                VALUES
                    (@environment, @revision, @id, @service_id, @resource_id, @sb,
                     @type, @path, @layer_index, @local_id)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", pub.Metadata.Id);
            cmd.Parameters.AddWithValue("@service_id", pub.ServiceId);
            cmd.Parameters.AddWithValue("@resource_id", pub.ResourceId);
            cmd.Parameters.AddWithValue("@sb", (object?)pub.StorageBindingId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", pub.PublicationType.ToString());
            cmd.Parameters.AddWithValue("@path", (object?)pub.Path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@layer_index", (object?)pub.LayerIndex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@local_id", (object?)pub.ServiceLocalId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var binding in graph.StorageBindings)
        {
            var sql = $"""
                INSERT INTO {_storageBindingsIdxTable}
                    (environment, revision, storage_binding_id, resource_id, connection_id, storage_type, locator)
                VALUES
                    (@environment, @revision, @id, @resource_id, @connection_id, @type, @locator)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", binding.Metadata.Id);
            cmd.Parameters.AddWithValue("@resource_id", binding.ResourceId);
            cmd.Parameters.AddWithValue("@connection_id", (object?)binding.ConnectionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", binding.StorageType.ToString());
            cmd.Parameters.AddWithValue("@locator", binding.Locator);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var conn in graph.Connections)
        {
            var sql = $"""
                INSERT INTO {_connectionsIdxTable}
                    (environment, revision, connection_id, name, type, provider)
                VALUES
                    (@environment, @revision, @id, @name, @type, @provider)
                """;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@environment", _environment);
            cmd.Parameters.AddWithValue("@revision", graph.Revision);
            cmd.Parameters.AddWithValue("@id", conn.Metadata.Id);
            cmd.Parameters.AddWithValue("@name", conn.Metadata.Name);
            cmd.Parameters.AddWithValue("@type", conn.Type.ToString());
            cmd.Parameters.AddWithValue("@provider", (object?)conn.Provider ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds an empty-but-valid in-memory snapshot for a freshly deployed server with no
    /// published datasets. All catalog-style read surfaces (STAC, GeoServices, OGC API
    /// Features, OData, WFS, …) enumerate the collections/services list and return a valid
    /// empty response when the list is zero-length, so this snapshot produces 200s with no
    /// items rather than 500s. It is never persisted to the database. (honua-server#1619.)
    /// </summary>
    private MetadataV2GraphSnapshot BuildEmptySnapshot()
    {
        var graph = new MetadataV2Graph
        {
            Environment = _environment,
            Revision = 0,
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        return new MetadataV2GraphSnapshot(graph, "\"empty\"", DateTimeOffset.UtcNow);
    }

    private static MetadataV2GraphSnapshot MaterializeSnapshot(string json, string etag)
    {
        var graph = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Graph);
        if (graph is null)
        {
            throw new InvalidDataException("Stored Metadata v2 document is empty.");
        }
        return new MetadataV2GraphSnapshot(graph, etag, DateTimeOffset.UtcNow);
    }

    private static string ComputeEtag(string json)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }
}

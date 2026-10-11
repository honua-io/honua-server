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
internal sealed class PostgresMetadataV2GraphStore : IMetadataV2GraphStore, IMetadataV2GraphRevisionStager, IMetadataV2GraphWriteBaseReader
{
    // Shared with schema-coupled metadata publishers such as the demo STAC seed.
    // The environment hash is the second key so unrelated environments can publish concurrently.
    internal const int MetadataWriteLockNamespace = 144047714;

    // Kept in addition to the current revision, any revision still ahead of current,
    // and revisions named by an in-flight release package or content publication.
    // Once a later save passes a staged candidate, that candidate survives only while
    // it remains inside this window or a package/publication still references it.
    internal const int MetadataV2RetainedSnapshotCount = 32;

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
                throw new MetadataV2GraphConcurrencyException(
                    $"Metadata v2 etag mismatch for environment '{_environment}': expected {expectedEtag} but found {actualEtag ?? "<none>"}.",
                    expectedEtag,
                    actualEtag);
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
        await PruneSnapshotsAsync(connection, transaction, graph.Revision, cancellationToken).ConfigureAwait(false);

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
        // instead of waiting out the TTL. When Redis is composed the invalidator also broadcasts to
        // every other node (RedisMetadataV2GraphCacheInvalidator); without it, other nodes remain
        // TTL-bounded. This is the canonical
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
            throw new MetadataV2GraphConcurrencyException(
                $"Metadata v2 etag mismatch for environment '{_environment}': expected {expectedCurrentEtag} but found {current?.Etag ?? "<none>"}.",
                expectedCurrentEtag,
                current?.Etag);
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
        await PruneSnapshotsAsync(connection, transaction, revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);

        _cachedCurrent = target;
        _cacheInvalidator?.Invalidate(_environment);
        return target;
    }

    /// <inheritdoc />
    public async Task<MetadataV2GraphSnapshot> StageAsync(
        MetadataV2Graph graph,
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

        // Same lock and store-owned revision allocation as SaveAsync, so a staged candidate can
        // never collide with a concurrent publish. Neither the current pointer nor the derived
        // sidecar indexes are touched: canonical readers stay on the active revision, and
        // ActivateRevisionAsync rebuilds the target's sidecars when it repoints current.
        await AcquireEnvironmentWriteLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var revision = await ReadNextRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        graph = graph with { Revision = revision };
        var json = JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph);
        var etag = ComputeEtag(json);

        await UpsertSnapshotAsync(connection, transaction, graph, json, etag, cancellationToken).ConfigureAwait(false);
        await PruneSnapshotsAsync(connection, transaction, graph.Revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);

        return new MetadataV2GraphSnapshot(graph, etag, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<bool> DiscardStagedAsync(
        long revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await VerifySchemaFloorAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await AcquireEnvironmentWriteLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (current?.Revision == revision)
        {
            throw new InvalidOperationException(
                $"Metadata v2 revision {revision} is current for environment '{_environment}' and cannot be discarded.");
        }

        // A staged revision has no sidecar rows unless it was once activated; clear them either way
        // so a discarded candidate leaves no derived lookup rows behind.
        var deleteSql = new[]
        {
            $"DELETE FROM {_resourcesIdxTable} WHERE environment = @environment AND revision = @revision",
            $"DELETE FROM {_servicesIdxTable} WHERE environment = @environment AND revision = @revision",
            $"DELETE FROM {_publicationsIdxTable} WHERE environment = @environment AND revision = @revision",
            $"DELETE FROM {_storageBindingsIdxTable} WHERE environment = @environment AND revision = @revision",
            $"DELETE FROM {_connectionsIdxTable} WHERE environment = @environment AND revision = @revision",
            $"DELETE FROM {_snapshotsTable} WHERE environment = @environment AND revision = @revision",
        };

        var deleted = 0;
        foreach (var sql in deleteSql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@environment", _environment);
            command.Parameters.AddWithValue("@revision", revision);
            deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return deleted > 0;
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

        if (graph.Resources.Count > 0)
        {
            await InsertResourceSidecarsAsync(connection, transaction, graph, cancellationToken).ConfigureAwait(false);
        }

        if (graph.Services.Count > 0)
        {
            await InsertServiceSidecarsAsync(connection, transaction, graph, cancellationToken).ConfigureAwait(false);
        }

        if (graph.Publications.Count > 0)
        {
            await InsertPublicationSidecarsAsync(connection, transaction, graph, cancellationToken).ConfigureAwait(false);
        }

        if (graph.StorageBindings.Count > 0)
        {
            await InsertStorageBindingSidecarsAsync(connection, transaction, graph, cancellationToken).ConfigureAwait(false);
        }

        if (graph.Connections.Count > 0)
        {
            await InsertConnectionSidecarsAsync(connection, transaction, graph, cancellationToken).ConfigureAwait(false);
        }

        // Lookup rows are for the active revision. Older revisions stay readable from their documents.
        await DeleteSupersededSidecarsAsync(connection, transaction, graph.Revision, cancellationToken).ConfigureAwait(false);
    }

    private Task InsertResourceSidecarsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataV2Graph graph,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_resourcesIdxTable}
                (environment, revision, resource_id, name, namespace, type, primary_storage_binding_id)
            SELECT @environment, @revision, resource_id, name, namespace, type, primary_storage_binding_id
            FROM unnest(
                @resource_ids::text[],
                @names::text[],
                @namespaces::text[],
                @types::text[],
                @primary_storage_binding_ids::text[])
                AS sidecar(resource_id, name, namespace, type, primary_storage_binding_id)
            """;
        // Namespace stays null: MetadataV2ObjectMetadata.Namespace was removed in design slice 65/N.
        return InsertSidecarRowsAsync(
            connection,
            transaction,
            sql,
            graph.Revision,
            command =>
            {
                AddTextArray(command, "@resource_ids", SelectArray(graph.Resources, resource => resource.Metadata.Id));
                AddTextArray(command, "@names", SelectArray(graph.Resources, resource => resource.Metadata.Name));
                AddTextArray(command, "@namespaces", new string?[graph.Resources.Count]);
                AddTextArray(command, "@types", SelectArray(graph.Resources, resource => resource.Type.ToString()));
                AddTextArray(command, "@primary_storage_binding_ids", SelectArray(graph.Resources, resource => resource.PrimaryStorageBindingId));
            },
            cancellationToken);
    }

    private Task InsertServiceSidecarsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataV2Graph graph,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_servicesIdxTable}
                (environment, revision, service_id, name, service_type, route)
            SELECT @environment, @revision, service_id, name, service_type, route
            FROM unnest(@service_ids::text[], @names::text[], @service_types::text[], @routes::text[])
                AS sidecar(service_id, name, service_type, route)
            """;
        return InsertSidecarRowsAsync(
            connection,
            transaction,
            sql,
            graph.Revision,
            command =>
            {
                AddTextArray(command, "@service_ids", SelectArray(graph.Services, service => service.Metadata.Id));
                AddTextArray(command, "@names", SelectArray(graph.Services, service => service.Metadata.Name));
                AddTextArray(command, "@service_types", SelectArray(graph.Services, service => service.PrimaryProtocol));
                AddTextArray(command, "@routes", SelectArray(graph.Services, service => service.Route));
            },
            cancellationToken);
    }

    private Task InsertPublicationSidecarsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataV2Graph graph,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_publicationsIdxTable}
                (environment, revision, publication_id, service_id, resource_id, storage_binding_id,
                 publication_type, path, layer_index, service_local_id)
            SELECT @environment, @revision, publication_id, service_id, resource_id, storage_binding_id,
                   publication_type, path, layer_index, service_local_id
            FROM unnest(
                @publication_ids::text[],
                @service_ids::text[],
                @resource_ids::text[],
                @storage_binding_ids::text[],
                @publication_types::text[],
                @paths::text[],
                @layer_indexes::int[],
                @service_local_ids::text[])
                AS sidecar(
                    publication_id, service_id, resource_id, storage_binding_id,
                    publication_type, path, layer_index, service_local_id)
            """;
        return InsertSidecarRowsAsync(
            connection,
            transaction,
            sql,
            graph.Revision,
            command =>
            {
                AddTextArray(command, "@publication_ids", SelectArray(graph.Publications, publication => publication.Metadata.Id));
                AddTextArray(command, "@service_ids", SelectArray(graph.Publications, publication => publication.ServiceId));
                AddTextArray(command, "@resource_ids", SelectArray(graph.Publications, publication => publication.ResourceId));
                AddTextArray(command, "@storage_binding_ids", SelectArray(graph.Publications, publication => publication.StorageBindingId));
                AddTextArray(command, "@publication_types", SelectArray(graph.Publications, publication => publication.PublicationType.ToString()));
                AddTextArray(command, "@paths", SelectArray(graph.Publications, publication => publication.Path));
                command.Parameters.Add(new NpgsqlParameter("@layer_indexes", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                {
                    Value = SelectArray(graph.Publications, publication => publication.LayerIndex),
                });
                AddTextArray(command, "@service_local_ids", SelectArray(graph.Publications, publication => publication.ServiceLocalId));
            },
            cancellationToken);
    }

    private Task InsertStorageBindingSidecarsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataV2Graph graph,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_storageBindingsIdxTable}
                (environment, revision, storage_binding_id, resource_id, connection_id, storage_type, locator)
            SELECT @environment, @revision, storage_binding_id, resource_id, connection_id, storage_type, locator
            FROM unnest(
                @storage_binding_ids::text[],
                @resource_ids::text[],
                @connection_ids::text[],
                @storage_types::text[],
                @locators::text[])
                AS sidecar(storage_binding_id, resource_id, connection_id, storage_type, locator)
            """;
        return InsertSidecarRowsAsync(
            connection,
            transaction,
            sql,
            graph.Revision,
            command =>
            {
                AddTextArray(command, "@storage_binding_ids", SelectArray(graph.StorageBindings, binding => binding.Metadata.Id));
                AddTextArray(command, "@resource_ids", SelectArray(graph.StorageBindings, binding => binding.ResourceId));
                AddTextArray(command, "@connection_ids", SelectArray(graph.StorageBindings, binding => binding.ConnectionId));
                AddTextArray(command, "@storage_types", SelectArray(graph.StorageBindings, binding => binding.StorageType.ToString()));
                AddTextArray(command, "@locators", SelectArray(graph.StorageBindings, binding => binding.Locator));
            },
            cancellationToken);
    }

    private Task InsertConnectionSidecarsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MetadataV2Graph graph,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_connectionsIdxTable}
                (environment, revision, connection_id, name, type, provider)
            SELECT @environment, @revision, connection_id, name, type, provider
            FROM unnest(@connection_ids::text[], @names::text[], @types::text[], @providers::text[])
                AS sidecar(connection_id, name, type, provider)
            """;
        return InsertSidecarRowsAsync(
            connection,
            transaction,
            sql,
            graph.Revision,
            command =>
            {
                AddTextArray(command, "@connection_ids", SelectArray(graph.Connections, item => item.Metadata.Id));
                AddTextArray(command, "@names", SelectArray(graph.Connections, item => item.Metadata.Name));
                AddTextArray(command, "@types", SelectArray(graph.Connections, item => item.Type.ToString()));
                AddTextArray(command, "@providers", SelectArray(graph.Connections, item => item.Provider));
            },
            cancellationToken);
    }

    private async Task InsertSidecarRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        long revision,
        Action<NpgsqlCommand> bindArrays,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        command.Parameters.AddWithValue("@revision", revision);
        bindArrays(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddTextArray(NpgsqlCommand command, string name, Array values)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values });

    private static TResult[] SelectArray<TSource, TResult>(IReadOnlyList<TSource> source, Func<TSource, TResult> selector)
    {
        var values = new TResult[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            values[index] = selector(source[index]);
        }

        return values;
    }

    private async Task DeleteSupersededSidecarsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long revision,
        CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            $"DELETE FROM {_resourcesIdxTable} WHERE environment = @environment AND revision <> @revision",
            $"DELETE FROM {_servicesIdxTable} WHERE environment = @environment AND revision <> @revision",
            $"DELETE FROM {_publicationsIdxTable} WHERE environment = @environment AND revision <> @revision",
            $"DELETE FROM {_storageBindingsIdxTable} WHERE environment = @environment AND revision <> @revision",
            $"DELETE FROM {_connectionsIdxTable} WHERE environment = @environment AND revision <> @revision",
        };
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            command.Parameters.AddWithValue("@environment", _environment);
            command.Parameters.AddWithValue("@revision", revision);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PruneSnapshotsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long keepRevision,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var releasePackagesExist = await RelationExistsAsync(
            connection, transaction, "metadata_v2_release_packages", cancellationToken).ConfigureAwait(false);
        var contentVersionsExist = await RelationExistsAsync(
            connection, transaction, "content_publication_versions", cancellationToken).ConfigureAwait(false);
        var releaseClause = releasePackagesExist
            ? $"""
              AND NOT EXISTS (
                  SELECT 1
                  FROM {Infrastructure.SchemaSearchPath.QualifyTable("metadata_v2_release_packages", _schemaName)} AS package
                  WHERE package.source_environment = s.environment
                    AND package.source_revision = s.revision
                    AND package.status IN ('draft', 'ready', 'staged'))
              """
            : string.Empty;
        var contentClause = contentVersionsExist
            ? $"""
              AND NOT EXISTS (
                  SELECT 1
                  FROM {Infrastructure.SchemaSearchPath.QualifyTable("content_publication_versions", _schemaName)} AS version
                  WHERE version.source_metadata_revision = s.revision)
              """
            : string.Empty;
        var sql = $"""
            DELETE FROM {_snapshotsTable} AS s
            WHERE s.environment = @environment
              AND s.revision <> @keep_revision
              AND (@current_revision IS NULL OR s.revision <> @current_revision)
              AND (@current_revision IS NULL OR s.revision <= @current_revision)
              AND s.revision NOT IN (
                  SELECT retained.revision
                  FROM {_snapshotsTable} AS retained
                  WHERE retained.environment = @environment
                  ORDER BY retained.revision DESC
                  LIMIT @retain)
              {releaseClause}
              {contentClause}
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@environment", _environment);
        command.Parameters.AddWithValue("@keep_revision", keepRevision);
        command.Parameters.Add(new NpgsqlParameter("@current_revision", NpgsqlDbType.Bigint)
        {
            Value = current is { } state ? state.Revision : DBNull.Value,
        });
        command.Parameters.AddWithValue("@retain", MetadataV2RetainedSnapshotCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RelationExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = @schema
                  AND table_name = @table)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@schema", _schemaName);
        command.Parameters.AddWithValue("@table", tableName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
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

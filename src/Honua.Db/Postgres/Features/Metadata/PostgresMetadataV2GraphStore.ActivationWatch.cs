// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Metadata;

internal sealed partial class PostgresMetadataV2GraphStore : ICatalogActivationWatchStore
{
    private string ActivationWatchesTable => Infrastructure.SchemaSearchPath.QualifyTable("catalog_activation_watches", _schemaName);

    public async Task<IReadOnlyList<CatalogActivationWatch>> ReadPendingAsync(CancellationToken cancellationToken = default)
        => await ReadWatchesAsync("(state = 'watching' OR (state = 'rolled_back' AND purge_completed_at IS NULL))", cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<CatalogActivationWatch>> ReadRecentRollbacksAsync(CancellationToken cancellationToken = default)
        => ReadWatchesAsync("state = 'rolled_back'", cancellationToken);

    private async Task<IReadOnlyList<CatalogActivationWatch>> ReadWatchesAsync(string statePredicate, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT id, revision, expected_etag, activated_at, rollback_revision, rollback_change_id, rollback_reason
              FROM {ActivationWatchesTable}
             WHERE environment = @environment AND {statePredicate}
             ORDER BY id DESC LIMIT 128
            """, connection);
        command.Parameters.AddWithValue("environment", _environment);
        var watches = new List<CatalogActivationWatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            watches.Add(new()
            {
                Id = reader.GetInt64(0),
                Revision = reader.GetInt64(1),
                ExpectedEtag = reader.GetString(2),
                ActivatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                RollbackRevision = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                ChangeId = reader.IsDBNull(5) ? null : reader.GetString(5),
                Reason = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }
        return watches;
    }

    public async Task<bool> TryRollbackAsync(CatalogRollbackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await VerifySchemaFloorAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireEnvironmentWriteLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var priorDocument = await ReadRollbackDocumentAsync(transaction, request.ActivationId, cancellationToken).ConfigureAwait(false);
        if (priorDocument is null)
        {
            return false;
        }
        var graph = JsonSerializer.Deserialize(priorDocument, MetadataV2JsonContext.Default.MetadataV2Graph)
            ?? throw new InvalidDataException("The prior catalog document is missing.");
        var restored = await PublishGraphAsync(transaction, graph, cancellationToken).ConfigureAwait(false);
        await RecordRollbackAsync(transaction, (request, restored.Revision), cancellationToken).ConfigureAwait(false);
        try
        {
            await FeatureDataAccess.CommitEditTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A lost acknowledgement is retried from the durable journal, never by publishing twice.
            _cachedCurrent = null;
            _cacheInvalidator?.Invalidate(_environment);
        }
        return true;
    }

    public Task ExpireAsync(long activationId, CancellationToken cancellationToken = default) =>
        UpdateWatchAsync(activationId, "SET state = 'expired' WHERE state = 'watching'", cancellationToken);

    public Task CompletePurgeAsync(long activationId, CancellationToken cancellationToken = default) =>
        UpdateWatchAsync(activationId, "SET purge_completed_at = clock_timestamp() WHERE state = 'rolled_back'", cancellationToken);

    private async Task UpdateWatchAsync(long activationId, string update, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"UPDATE {ActivationWatchesTable} {update} AND id = @id AND environment = @environment", connection);
        command.Parameters.AddWithValue("id", activationId);
        command.Parameters.AddWithValue("environment", _environment);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadRollbackDocumentAsync(
        NpgsqlTransaction transaction, long activationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT watch.previous_document::text FROM {ActivationWatchesTable} watch
              JOIN {_currentTable} current ON current.environment = watch.environment
             WHERE watch.id = @id AND watch.environment = @environment AND watch.state = 'watching'
               AND current.revision = watch.revision AND current.etag = watch.expected_etag
               AND watch.activated_at >= clock_timestamp() - @bake_seconds * interval '1 second'
             FOR UPDATE OF watch
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("id", activationId);
        command.Parameters.AddWithValue("environment", _environment);
        command.Parameters.AddWithValue("bake_seconds", CatalogActivationErrorWatch.BakePeriod.TotalSeconds);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task RecordRollbackAsync(
        NpgsqlTransaction transaction, (CatalogRollbackRequest Request, long Revision) rollback, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {ActivationWatchesTable}
               SET state = 'rolled_back', rollback_revision = @revision,
                   rollback_change_id = @change_id, rollback_reason = @reason
             WHERE id = @id AND environment = @environment;
            UPDATE {ActivationWatchesTable} SET state = 'rollback'
             WHERE environment = @environment AND revision = @revision AND state = 'watching';
            """, transaction.Connection, transaction);
        command.Parameters.AddWithValue("id", rollback.Request.ActivationId);
        command.Parameters.AddWithValue("environment", _environment);
        command.Parameters.AddWithValue("revision", rollback.Revision);
        command.Parameters.AddWithValue("change_id", $"catalog-watch-{rollback.Request.ActivationId}");
        command.Parameters.AddWithValue("reason", rollback.Request.Reason);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MetadataV2GraphSnapshot> PublishGraphAsync(
        NpgsqlTransaction transaction, MetadataV2Graph graph, CancellationToken cancellationToken)
    {
        var connection = transaction.Connection!;
        var current = await ReadCurrentStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        graph = await ReconcileReservationsAsync(transaction, graph, cancellationToken).ConfigureAwait(false);
        graph = graph with { Revision = await ReadNextRevisionAsync(connection, transaction, cancellationToken).ConfigureAwait(false) };
        var json = JsonSerializer.Serialize(graph, MetadataV2JsonContext.Default.MetadataV2Graph);
        var etag = ComputeEtag(json);
        await UpsertSnapshotAsync(connection, transaction, graph, json, etag, cancellationToken).ConfigureAwait(false);
        await RefreshSidecarsAsync(connection, transaction, graph, clearStaleEnvironmentRows: current is null, cancellationToken).ConfigureAwait(false);
        await UpsertCurrentAsync(connection, transaction, graph.Revision, etag, cancellationToken).ConfigureAwait(false);
        await PruneSnapshotsAsync(connection, transaction, graph.Revision, cancellationToken).ConfigureAwait(false);
        return new MetadataV2GraphSnapshot(graph, etag, DateTimeOffset.UtcNow);
    }
}

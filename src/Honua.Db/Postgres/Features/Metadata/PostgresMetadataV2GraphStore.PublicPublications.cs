// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Metadata;

internal sealed partial class PostgresMetadataV2GraphStore : IPublicPublicationJournal
{
    private string PublicGenerationsTable => SchemaSearchPath.QualifyTable("public_publication_generations", _schemaName);

    public async Task<IPublicPublicationSession> OpenAsync(PublicPublicationKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.PublicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Version);
        var owner = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction? transaction = null;
        try
        {
            var connection = new NpgsqlConnectionLease(owner, owner.RequireNpgsqlConnection());
            await VerifySchemaFloorAsync(connection, cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await PublicPublicationWriteFence.LockAsync(transaction, cancellationToken).ConfigureAwait(false);
            var context = new PublicPublicationSessionContext(_environment, key)
            {
                GenerationsTable = PublicGenerationsTable,
                CurrentTable = _currentTable
            };
            return new PublicPublicationSession(connection, transaction, context);
        }
        catch
        {
            try
            {
                if (transaction is not null)
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<PublicPublicationGeneration>> ReadOutstandingAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await VerifySchemaFloorAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {PublicPublicationColumns} FROM {PublicGenerationsTable} WHERE environment = @environment AND state <> 'revoked' ORDER BY publication_id, version", connection);
        command.Parameters.AddWithValue("environment", _environment);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var generations = new List<PublicPublicationGeneration>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            generations.Add(ReadPublicPublication(reader));
        }
        return generations;
    }

    private const string PublicPublicationColumns = "publication_id, version, service_id, layer_index, metadata_revision, destination_bucket, destination_base_url, cdn_distribution_id, state, purge_ids";

    private static PublicPublicationGeneration ReadPublicPublication(NpgsqlDataReader reader) => new()
    {
        Reservation = new PublicPublicationReservation
        {
            Key = new PublicPublicationKey(reader.GetString(0), reader.GetString(1)),
            ServiceId = reader.GetString(2),
            LayerIndex = reader.GetInt32(3),
            MetadataRevision = reader.GetInt64(4),
            DestinationBucket = reader.GetString(5),
            DestinationBaseUrl = reader.GetString(6),
            CdnDistributionId = reader.GetString(7)
        },
        State = reader.GetString(8) switch
        {
            "pending" => PublicPublicationState.Pending,
            "ready" => PublicPublicationState.Ready,
            "revoking" => PublicPublicationState.Revoking,
            "revoked" => PublicPublicationState.Revoked,
            _ => throw new InvalidOperationException("Unknown public publication state.")
        },
        PurgeIds = reader.GetFieldValue<string[]>(9)
    };

    private sealed record PublicPublicationSessionContext(string Environment, PublicPublicationKey Key)
    {
        public required string GenerationsTable { get; init; }
        public required string CurrentTable { get; init; }
    }

    private sealed class PublicPublicationSession(
        NpgsqlConnectionLease connection,
        NpgsqlTransaction transaction,
        PublicPublicationSessionContext context) : IPublicPublicationSession
    {
        private const string KeyPredicate = "environment = @environment AND publication_id = @publication AND version = @version";

        public async Task<PublicPublicationGeneration?> ReadAsync(CancellationToken cancellationToken = default)
        {
            await using var command = Command($"SELECT {PublicPublicationColumns} FROM {context.GenerationsTable} WHERE {KeyPredicate}");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPublicPublication(reader) : null;
        }

        public async Task ReserveAsync(PublicPublicationReservation reservation, CancellationToken cancellationToken = default)
        {
            if (reservation.Key != context.Key)
            {
                throw new ArgumentException("Reservation must match the locked generation.", nameof(reservation));
            }
            await using var command = Command($"""
                INSERT INTO {context.GenerationsTable}
                    (environment, publication_id, version, service_id, layer_index, metadata_revision,
                     destination_bucket, destination_base_url, cdn_distribution_id)
                SELECT @environment, @publication, @version, @service, @layer, @revision, @bucket, @base_url, @distribution
                WHERE EXISTS (SELECT 1 FROM {context.CurrentTable} WHERE environment = @environment AND revision = @revision)
                """);
            command.Parameters.AddWithValue("service", reservation.ServiceId);
            command.Parameters.AddWithValue("layer", reservation.LayerIndex);
            command.Parameters.AddWithValue("revision", reservation.MetadataRevision);
            command.Parameters.AddWithValue("bucket", reservation.DestinationBucket);
            command.Parameters.AddWithValue("base_url", reservation.DestinationBaseUrl);
            command.Parameters.AddWithValue("distribution", reservation.CdnDistributionId);
            await RequireTransitionAsync(command, cancellationToken).ConfigureAwait(false);
        }

        public Task MarkCopyCompleteAsync(CancellationToken cancellationToken = default) => TransitionAsync(
            "state = 'ready', copy_completed_at = clock_timestamp()", "state = 'pending'", cancellationToken);

        public Task BeginRevocationAsync(CancellationToken cancellationToken = default) => TransitionAsync(
            "state = 'revoking'", "state IN ('pending', 'ready', 'revoking')", cancellationToken);

        public Task RecordOriginDeletedAsync(CancellationToken cancellationToken = default) => TransitionAsync(
            "origin_deleted_at = clock_timestamp()", "state = 'revoking'", cancellationToken);

        public async Task RecordPurgeSubmittedAsync(PublicPublicationPurge purge, CancellationToken cancellationToken = default)
        {
            ValidatePurge(purge);
            await using var command = Command($"""
                UPDATE {context.GenerationsTable}
                SET purge_ids = ARRAY(SELECT DISTINCT id FROM unnest(purge_ids || @ids) AS ids(id) ORDER BY id)
                WHERE {KeyPredicate} AND state = 'revoking' AND origin_deleted_at IS NOT NULL
                    AND cdn_distribution_id = @distribution
                """);
            AddPurgeParameters(command, purge);
            await RequireTransitionAsync(command, cancellationToken).ConfigureAwait(false);
        }

        public async Task RecordRevocationCompletedAsync(PublicPublicationPurge completedPurge, CancellationToken cancellationToken = default)
        {
            ValidatePurge(completedPurge);
            await using var command = Command($"""
                UPDATE {context.GenerationsTable}
                SET state = 'revoked', purge_completed_at = clock_timestamp(), revoked_at = clock_timestamp()
                WHERE {KeyPredicate} AND state = 'revoking' AND origin_deleted_at IS NOT NULL
                    AND cdn_distribution_id = @distribution AND cardinality(purge_ids) > 0
                    AND purge_ids @> @ids AND purge_ids <@ @ids
                """);
            AddPurgeParameters(command, completedPurge);
            await RequireTransitionAsync(command, cancellationToken).ConfigureAwait(false);
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitSafelyAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        private NpgsqlCommand Command(string sql)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("environment", context.Environment);
            command.Parameters.AddWithValue("publication", context.Key.PublicationId);
            command.Parameters.AddWithValue("version", context.Key.Version);
            return command;
        }

        private async Task TransitionAsync(string assignment, string condition, CancellationToken cancellationToken)
        {
            await using var command = Command($"UPDATE {context.GenerationsTable} SET {assignment} WHERE {KeyPredicate} AND {condition}");
            await RequireTransitionAsync(command, cancellationToken).ConfigureAwait(false);
        }

        private static async Task RequireTransitionAsync(NpgsqlCommand command, CancellationToken cancellationToken)
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Public publication state or metadata revision does not permit this transition.");
            }
        }

        private static void ValidatePurge(PublicPublicationPurge purge)
        {
            ArgumentNullException.ThrowIfNull(purge);
            ArgumentException.ThrowIfNullOrWhiteSpace(purge.DistributionId);
            if (purge.InvalidationIds.Count == 0 || purge.InvalidationIds.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Every submitted invalidation requires a provider receipt.", nameof(purge));
            }
        }

        private static void AddPurgeParameters(NpgsqlCommand command, PublicPublicationPurge purge)
        {
            command.Parameters.AddWithValue("distribution", purge.DistributionId);
            command.Parameters.AddWithValue("ids", purge.InvalidationIds.Distinct(StringComparer.Ordinal).ToArray());
        }
    }
}

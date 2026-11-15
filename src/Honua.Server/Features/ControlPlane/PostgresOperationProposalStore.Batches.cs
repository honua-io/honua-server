// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

internal sealed partial class PostgresOperationProposalStore
{
    public Task<string> ReserveBatchAsync(OperationProposalBatchCandidate candidate, CancellationToken cancellationToken = default)
        => _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_batches} AS batch (batch_key, idempotency_key, proposal_id)
                    VALUES (@batch_key, @idempotency_key, @proposal_id)
                    ON CONFLICT (batch_key) DO UPDATE SET
                        idempotency_key = CASE WHEN batch.completed THEN EXCLUDED.idempotency_key ELSE batch.idempotency_key END,
                        proposal_id = CASE WHEN batch.completed THEN EXCLUDED.proposal_id ELSE batch.proposal_id END,
                        completed = FALSE
                    RETURNING idempotency_key
                    """, connection);
                command.Parameters.AddWithValue("batch_key", NpgsqlDbType.Text, candidate.BatchKey);
                command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, candidate.IdempotencyKey);
                command.Parameters.AddWithValue("proposal_id", NpgsqlDbType.Text, candidate.ProposalId);
                return (string)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            }, StatementRepeat.NotAfterSend, cancellationToken);

    public Task RetargetOpenBatchAsync(string previousProposalId, string renewedProposalId, CancellationToken cancellationToken = default)
        => UpdateOpenBatchAsync(
            $"""
            UPDATE {_batches}
            SET proposal_id = @renewed
            WHERE proposal_id = @previous AND completed = FALSE
            """,
            previousProposalId,
            renewedProposalId,
            cancellationToken);

    public Task CompleteOpenBatchAsync(string proposalId, CancellationToken cancellationToken = default)
        => _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    UPDATE {_batches}
                    SET completed = TRUE
                    WHERE proposal_id = @previous AND completed = FALSE
                    """, connection);
                command.Parameters.AddWithValue("previous", NpgsqlDbType.Text, proposalId);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, StatementRepeat.NotAfterSend, cancellationToken);

    private Task<int> UpdateOpenBatchAsync(
        string sql, string previousProposalId, string renewedProposalId, CancellationToken cancellationToken)
        => _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("previous", NpgsqlDbType.Text, previousProposalId);
                command.Parameters.AddWithValue("renewed", NpgsqlDbType.Text, renewedProposalId);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, StatementRepeat.NotAfterSend, cancellationToken);
}

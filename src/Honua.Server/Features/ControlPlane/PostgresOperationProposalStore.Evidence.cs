// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

// Signed proposal evidence is single-use: the consumption row commits in the proposal's transaction, so
// a second proposal carrying the same signed transcript is refused on every task.
internal sealed partial class PostgresOperationProposalStore
{
    private const string EvidenceIdentityMissingMessage = "The signed proposal evidence does not identify its tenant, signing key and run.";

    private async Task ConsumeEvidenceAsync(
        NpgsqlTransaction transaction,
        OperationProposal created,
        CancellationToken token)
    {
        if (created.Evidence is not { } evidence)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(evidence.TenantId)
            || string.IsNullOrWhiteSpace(evidence.TranscriptKeyId)
            || string.IsNullOrWhiteSpace(evidence.RunNonce))
        {
            throw new OperationProposalEvidenceConsumedException(EvidenceIdentityMissingMessage);
        }

        if (await TryInsertConsumptionAsync(transaction, created, token).ConfigureAwait(false))
        {
            return;
        }

        await transaction.RollbackAsync(token).ConfigureAwait(false);
        throw new OperationProposalEvidenceConsumedException(OperationProposalEvidenceConsumedException.AlreadyUsedMessage);
    }

    // The retention sweep removes a consumption row only a day after its evidence expired, far beyond the
    // verify-to-commit interval and any clock skew, so evidence verified just before expiry cannot reuse it.
    private Task<int> DeleteExpiredConsumptionsAsync(int batchSize, CancellationToken cancellationToken)
        => _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT tenant_id, transcript_key_id, run_nonce FROM {_evidenceConsumptions}
                        WHERE expires_at < now() - interval '1 day'
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_evidenceConsumptions} AS consumed USING expired
                    WHERE consumed.tenant_id = expired.tenant_id
                        AND consumed.transcript_key_id = expired.transcript_key_id
                        AND consumed.run_nonce = expired.run_nonce
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private async Task<bool> TryInsertConsumptionAsync(
        NpgsqlTransaction transaction,
        OperationProposal created,
        CancellationToken token)
    {
        var evidence = created.Evidence!;
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_evidenceConsumptions} (tenant_id, transcript_key_id, run_nonce, proposal_id, expires_at)
            VALUES (@tenant_id, @transcript_key_id, @run_nonce, @proposal_id, @expires_at)
            ON CONFLICT (tenant_id, transcript_key_id, run_nonce) DO NOTHING
            """,
            transaction.Connection, transaction);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, evidence.TenantId);
        command.Parameters.AddWithValue("transcript_key_id", NpgsqlDbType.Text, evidence.TranscriptKeyId);
        command.Parameters.AddWithValue("run_nonce", NpgsqlDbType.Text, evidence.RunNonce);
        command.Parameters.AddWithValue("proposal_id", NpgsqlDbType.Text, created.ProposalId);
        command.Parameters.AddWithValue("expires_at", NpgsqlDbType.TimestampTz, evidence.ExpiresAt.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
    }

    private sealed record ProposalInsert(OperationProposal Proposal, TimeSpan? Ttl);
}

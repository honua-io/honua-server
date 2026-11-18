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

        await DeleteExpiredConsumptionsAsync(transaction, token).ConfigureAwait(false);
        if (await TryInsertConsumptionAsync(transaction, created, token).ConfigureAwait(false))
        {
            return;
        }

        await transaction.RollbackAsync(token).ConfigureAwait(false);
        throw new OperationProposalEvidenceConsumedException(OperationProposalEvidenceConsumedException.AlreadyUsedMessage);
    }

    // Expired evidence is refused before it reaches the store, so an expired consumption row guards nothing.
    private async Task DeleteExpiredConsumptionsAsync(NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {_evidenceConsumptions} WHERE expires_at < now()",
            transaction.Connection, transaction);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

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

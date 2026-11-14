// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Coordination;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

internal sealed partial class PostgresOperationProposalStore(
    ControlPlaneStorage storage,
    IOptions<ControlPlaneRetentionOptions> retention,
    ILogger<PostgresOperationProposalStore> logger) : IOperationProposalStore, IExpiredRowSweep
{
    private readonly JobStoreDatabase _database = storage.Database;
    private const string SelectColumns = "version, updated_at, record";

    private readonly string _proposals = storage.Database.Table("operation_proposals");
    private readonly string _notificationSchedules = storage.Database.Table("ops_notification_schedules");
    private readonly OperationLeases _operationLeases = new(storage.Leases, "operation-proposal:");

    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _operationLeases.TryAcquireAsync(new LeaseClaim(operationId, ownerId), leaseDuration, cancellationToken);

    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _operationLeases.RenewAsync(new LeaseClaim(operationId, ownerId), leaseDuration, cancellationToken);

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        => _operationLeases.ReleaseAsync(new LeaseClaim(operationId, ownerId), cancellationToken);

    public async Task<bool> TryCreateAsync(
        OperationProposal proposal,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var created = proposal with
        {
            Version = 0,
            ApprovalExpiresAt = proposal.ApprovalExpiresAt ?? OperationProposalApprovalWindow.GetDeadline(proposal)
        };
        var inserted = await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_proposals} (proposal_id, version, kind, active, updated_at, expires_at, record)
                    VALUES (@proposal_id, @version, @kind, @active, @updated_at, now() + @retention, @record)
                    ON CONFLICT (proposal_id) DO NOTHING
                    """,
                    connection);
                AddRecordParameters(command, created, ttl);
                command.Parameters.Add(new NpgsqlParameter("updated_at", NpgsqlDbType.TimestampTz)
                {
                    Value = created.UpdatedAt.ToUniversalTime()
                });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken).ConfigureAwait(false);

        if (inserted)
        {
            Log.ProposalCreated(logger, created.ProposalId, created.Kind.ToString(), created.Status.ToString());
        }

        return inserted;
    }

    public async Task<OperationProposal?> GetAsync(
        string proposalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            return null;
        }

        return await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT {SelectColumns} FROM {_proposals} WHERE proposal_id = @proposal_id",
                    connection);
                command.Parameters.Add(new NpgsqlParameter("proposal_id", NpgsqlDbType.Text) { Value = proposalId });
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) ? Read(reader) : null;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TrySetAsync(
        OperationProposal proposal,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var expectedVersion = proposal.Version;
        var next = proposal with { Version = expectedVersion + 1 };
        var updated = await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    UPDATE {_proposals}
                    SET version = @version, kind = @kind, active = @active, updated_at = now(),
                        expires_at = now() + @retention,
                        record = jsonb_set(@record::jsonb, ARRAY['approvalExpiresAt'],
                            to_jsonb(COALESCE((record->>'approvalExpiresAt')::timestamptz,
                                (record->>'createdAt')::timestamptz +
                                CASE WHEN record->'autonomyMetadata'->>'severity' = '2'
                                OR (record->'autonomyMetadata'->>'severity' IS NULL
                                    AND record->'plan'->>'riskLevel' = '2')
                                    THEN interval '72 hours' ELSE interval '24 hours' END)))::json
                    WHERE proposal_id = @proposal_id AND version = @expected_version
                        AND (record->>'status' <> '10' OR @next_status = 10)
                        AND (@executing = FALSE OR record->>'status' = '8' OR now() < COALESCE(
                            (record->>'approvalExpiresAt')::timestamptz,
                            (record->>'createdAt')::timestamptz +
                            CASE WHEN record->'autonomyMetadata'->>'severity' = '2'
                                OR (record->'autonomyMetadata'->>'severity' IS NULL
                                    AND record->'plan'->>'riskLevel' = '2')
                                THEN interval '72 hours' ELSE interval '24 hours' END))
                    """,
                    connection);
                AddRecordParameters(command, next, ttl);
                command.Parameters.Add(new NpgsqlParameter("next_status", NpgsqlDbType.Integer) { Value = (int)next.Status });
                command.Parameters.Add(new NpgsqlParameter("executing", NpgsqlDbType.Boolean)
                {
                    Value = next.Status == OperationProposalStatus.Executing
                });
                command.Parameters.Add(new NpgsqlParameter("expected_version", NpgsqlDbType.Bigint) { Value = expectedVersion });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken).ConfigureAwait(false);

        if (updated)
        {
            Log.ProposalUpdated(logger, next.ProposalId, next.Status.ToString());
        }
        else
        {
            Log.ProposalVersionConflict(logger, next.ProposalId, expectedVersion);
        }

        return updated;
    }

    public async Task<IReadOnlyList<OperationProposal>> ListActiveAsync(
        OperationClass? kind = null,
        CancellationToken cancellationToken = default)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT {SelectColumns} FROM {_proposals}
                    WHERE active AND (@kind::text IS NULL OR kind = @kind)
                    ORDER BY updated_at DESC
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text)
                {
                    Value = kind.HasValue ? kind.Value.ToString() : DBNull.Value
                });
                var proposals = new List<OperationProposal>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    proposals.Add(Read(reader));
                }

                return proposals;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken).ConfigureAwait(false);

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT proposal_id FROM {_proposals}
                        WHERE NOT active AND expires_at <= now()
                            AND NOT EXISTS (SELECT 1 FROM {_notificationSchedules} AS notice
                                WHERE notice.source = 'proposal' AND notice.subject_id = {_proposals}.proposal_id
                                    AND notice.closed_at IS NULL)
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_proposals} AS proposal USING expired
                    WHERE proposal.proposal_id = expired.proposal_id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private void AddRecordParameters(NpgsqlCommand command, OperationProposal proposal, TimeSpan? ttl)
    {
        command.Parameters.Add(new NpgsqlParameter("proposal_id", NpgsqlDbType.Text) { Value = proposal.ProposalId });
        command.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Bigint) { Value = proposal.Version });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = proposal.Kind.ToString() });
        command.Parameters.Add(new NpgsqlParameter("active", NpgsqlDbType.Boolean) { Value = !IsTerminal(proposal.Status) });
        command.Parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval)
        {
            Value = ttl ?? retention.Value.Proposals
        });
        command.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json)
        {
            Value = JsonSerializer.Serialize(proposal, OperationProposalJsonContext.Default.OperationProposal)
        });
    }

    private static OperationProposal Read(NpgsqlDataReader reader)
    {
        var proposal = JsonSerializer.Deserialize(reader.GetString(2), OperationProposalJsonContext.Default.OperationProposal)
            ?? throw new InvalidDataException("A stored operation proposal record is empty.");
        return proposal with
        {
            Version = reader.GetInt64(0),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(1)
        };
    }

    private static bool IsTerminal(OperationProposalStatus status)
        => status is OperationProposalStatus.Succeeded
            or OperationProposalStatus.Failed
            or OperationProposalStatus.Rejected
            or OperationProposalStatus.RolledBack
            or OperationProposalStatus.Cancelled
            or OperationProposalStatus.Expired;

    private static partial class Log
    {
        [LoggerMessage(9100, LogLevel.Information, "Created operation proposal {ProposalId} ({Kind}) with status {Status}")]
        public static partial void ProposalCreated(ILogger logger, string proposalId, string kind, string status);

        [LoggerMessage(9101, LogLevel.Debug, "Updated operation proposal {ProposalId} to status {Status}")]
        public static partial void ProposalUpdated(ILogger logger, string proposalId, string status);

        [LoggerMessage(9102, LogLevel.Debug, "Version conflict updating proposal {ProposalId} (expected {ExpectedVersion})")]
        public static partial void ProposalVersionConflict(ILogger logger, string proposalId, long expectedVersion);
    }
}

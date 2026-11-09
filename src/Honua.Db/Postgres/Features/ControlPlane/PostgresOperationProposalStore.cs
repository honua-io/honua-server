// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.ControlPlane;

internal sealed class PostgresOperationProposalStore : IOperationProposalStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _proposals;
    private readonly PostgresOperationLeaseStore _leases;

    public PostgresOperationProposalStore(NpgsqlDataSource dataSource, string? schemaName = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _proposals = SchemaSearchPath.QualifyTable("operation_proposals", schemaName);
        _leases = new PostgresOperationLeaseStore(dataSource, "operation_proposal_leases", schemaName);
    }

    public Task<bool> TryCreateAsync(OperationProposal proposal, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return PersistAsync(proposal with { Version = 0 }, $"""
            INSERT INTO {_proposals} (proposal_id, version, kind, status, updated_at, payload)
            VALUES (@id, @version, @kind, @status, @updated, @payload)
            ON CONFLICT (proposal_id) DO NOTHING
            """, cancellationToken);
    }

    public Task<bool> TrySetAsync(OperationProposal proposal, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var next = proposal with { Version = checked(proposal.Version + 1), UpdatedAt = DateTimeOffset.UtcNow };
        return PersistAsync(next, $"""
            UPDATE {_proposals}
            SET version = @version, kind = @kind, status = @status, updated_at = @updated, payload = @payload
            WHERE proposal_id = @id AND version = @expected
            """, cancellationToken);
    }

    public async Task<OperationProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(proposalId))
        {
            return null;
        }
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT payload::text FROM {_proposals} WHERE proposal_id = @id", connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Text, proposalId);
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return payload is string json
            ? JsonSerializer.Deserialize(json, PostgresOperationProposalJsonContext.Default.OperationProposal)
            : null;
    }

    public async Task<IReadOnlyList<OperationProposal>> ListActiveAsync(OperationClass? kind = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text FROM {_proposals}
            WHERE status <> ALL(@terminal) AND (@kind IS NULL OR kind = @kind)
            ORDER BY updated_at DESC, proposal_id
            """, connection);
        command.Parameters.AddWithValue("terminal", NpgsqlDbType.Array | NpgsqlDbType.Integer,
            Enum.GetValues<OperationProposalStatus>().Where(status => status.IsTerminal()).Select(status => (int)status).ToArray());
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Integer, kind.HasValue ? (object)(int)kind.Value : DBNull.Value);
        var proposals = new List<OperationProposal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            proposals.Add(JsonSerializer.Deserialize(reader.GetString(0), PostgresOperationProposalJsonContext.Default.OperationProposal)
                ?? throw new InvalidOperationException("Stored proposal payload is null."));
        }
        return proposals;
    }

    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _leases.TryAcquireAsync(new PostgresOperationLease(operationId, ownerId, leaseDuration), cancellationToken);

    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => _leases.RenewAsync(new PostgresOperationLease(operationId, ownerId, leaseDuration), cancellationToken);

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        => _leases.ReleaseAsync(operationId, ownerId, cancellationToken);

    private async Task<bool> PersistAsync(OperationProposal proposal, string statement, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.ProposalId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(statement, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Text, proposal.ProposalId);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, proposal.Version);
        command.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, proposal.Version - 1);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Integer, (int)proposal.Kind);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Integer, (int)proposal.Status);
        command.Parameters.AddWithValue("updated", NpgsqlDbType.TimestampTz, proposal.UpdatedAt.ToUniversalTime());
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(proposal, PostgresOperationProposalJsonContext.Default.OperationProposal));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

internal sealed partial class PostgresExecutionJobStore
{
    public Task<bool> TryAcquireLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => ExecuteLeaseStatementAsync(
            new LeaseStatement(
                $"""
                INSERT INTO {_leases} AS lease (lease_key, owner_id, expires_at)
                VALUES (@lease_key, @lease_owner_id, now() + @duration)
                ON CONFLICT (lease_key) DO UPDATE SET owner_id = EXCLUDED.owner_id, expires_at = EXCLUDED.expires_at
                WHERE lease.expires_at <= now()
                """,
                leaseDuration,
                StatementRepeat.NotAfterSend),
            operationId,
            ownerId,
            cancellationToken);

    public Task<bool> RenewLeaseAsync(
        string operationId,
        string ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => ExecuteLeaseStatementAsync(
            new LeaseStatement(
                $"""
                UPDATE {_leases} SET expires_at = now() + @duration
                WHERE lease_key = @lease_key AND owner_id = @lease_owner_id AND expires_at > now()
                """,
                leaseDuration,
                StatementRepeat.SafeToRepeat),
            operationId,
            ownerId,
            cancellationToken);

    public Task ReleaseLeaseAsync(
        string operationId,
        string ownerId,
        CancellationToken cancellationToken = default)
        => ExecuteLeaseStatementAsync(
            new LeaseStatement(
                $"DELETE FROM {_leases} WHERE lease_key = @lease_key AND owner_id = @lease_owner_id",
                Duration: null,
                StatementRepeat.SafeToRepeat),
            operationId,
            ownerId,
            cancellationToken);

    public Task<bool> TrySetIfLeaseOwnedAsync(
        ExecutionJobRecord job,
        OwnedJobLease lease,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.LeaseOperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.OwnerId);
        return CompareAndSetAsync(job, ttl, new OwnedLease(lease.LeaseOperationId, lease.OwnerId), cancellationToken);
    }

    private async Task<bool> ExecuteLeaseStatementAsync(
        LeaseStatement statement,
        string leaseKey,
        string ownerId,
        CancellationToken cancellationToken)
        => await _database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(statement.Text, connection);
                command.Parameters.Add(new NpgsqlParameter("lease_key", NpgsqlDbType.Text) { Value = leaseKey });
                command.Parameters.Add(new NpgsqlParameter("lease_owner_id", NpgsqlDbType.Text) { Value = ownerId });
                if (statement.Duration.HasValue)
                {
                    command.Parameters.Add(new NpgsqlParameter("duration", NpgsqlDbType.Interval) { Value = statement.Duration.Value });
                }

                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            statement.Repeat,
            cancellationToken).ConfigureAwait(false);

    private readonly record struct OwnedLease(string LeaseKey, string OwnerId);

    private readonly record struct LeaseStatement(string Text, TimeSpan? Duration, StatementRepeat Repeat);
}

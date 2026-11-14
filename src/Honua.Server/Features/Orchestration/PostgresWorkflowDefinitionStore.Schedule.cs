// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Coordination;
using Npgsql;

namespace Honua.Server.Features.Orchestration;

internal sealed partial class PostgresWorkflowDefinitionStore
{
    public Task<DateTimeOffset?> GetScheduleCursorAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        return storage.Database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                SELECT greatest(completed_ticks, pending_ticks) FROM {_schedules} WHERE workflow_id = @id
                """, connection);
            command.Parameters.AddWithValue("id", workflowId);
            var ticks = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return ticks is long value ? new DateTimeOffset(value, TimeSpan.Zero) : (DateTimeOffset?)null;
        }, StatementRepeat.SafeToRepeat, cancellationToken);
    }

    public async Task AdvanceScheduleCursorAsync(string workflowId, DateTimeOffset fireTime, CancellationToken cancellationToken = default)
        => await WriteScheduleAsync($"""
            INSERT INTO {_schedules} AS current (workflow_id, completed_ticks) VALUES (@id, @ticks)
            ON CONFLICT (workflow_id) DO UPDATE SET
                completed_ticks = greatest(current.completed_ticks, excluded.completed_ticks),
                pending_ticks = CASE WHEN current.pending_ticks <= greatest(current.completed_ticks, excluded.completed_ticks)
                    THEN NULL ELSE current.pending_ticks END
            """, new SchedulePosition(workflowId, fireTime), cancellationToken).ConfigureAwait(false);

    public async Task SetPendingScheduleCursorAsync(string workflowId, DateTimeOffset fireTime, CancellationToken cancellationToken = default)
        => await WriteScheduleAsync($"""
            INSERT INTO {_schedules} AS current (workflow_id, pending_ticks) VALUES (@id, @ticks)
            ON CONFLICT (workflow_id) DO UPDATE SET pending_ticks = greatest(current.pending_ticks, excluded.pending_ticks)
            """, new SchedulePosition(workflowId, fireTime), cancellationToken).ConfigureAwait(false);

    public async Task<bool> TryClaimScheduleFireAsync(
        string workflowId, DateTimeOffset fireTime, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        var claim = ScheduleClaim(workflowId, fireTime);
        if (!await _claims.TryAcquireAsync(claim, ClaimRetention(retention), cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            var cursor = await GetScheduleCursorAsync(workflowId, cancellationToken).ConfigureAwait(false);
            if (cursor is null || cursor.Value.ToUnixTimeSeconds() / SecondsPerMinute < fireTime.ToUnixTimeSeconds() / SecondsPerMinute)
            {
                return true;
            }
        }
        catch
        {
            await _claims.ReleaseAsync(claim, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await _claims.ReleaseAsync(claim, cancellationToken).ConfigureAwait(false);
        return false;
    }

    public Task ReleaseScheduleClaimAsync(string workflowId, DateTimeOffset fireTime, CancellationToken cancellationToken = default)
        => _claims.ReleaseAsync(ScheduleClaim(workflowId, fireTime), cancellationToken);

    private Task WriteScheduleAsync(string sql, SchedulePosition position, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(position.WorkflowId);
        return storage.Database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", position.WorkflowId);
            command.Parameters.AddWithValue("ticks", position.FireTime.UtcTicks);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.NotAfterSend, cancellationToken);
    }

    private const long SecondsPerMinute = 60;

    private LeaseClaim ScheduleClaim(string workflowId, DateTimeOffset fireTime)
        => new("schedule:" + ClaimKey([workflowId, (fireTime.ToUnixTimeSeconds() / SecondsPerMinute)
            .ToString(System.Globalization.CultureInfo.InvariantCulture)]), _claimOwner);

    private sealed record SchedulePosition(string WorkflowId, DateTimeOffset FireTime);
}

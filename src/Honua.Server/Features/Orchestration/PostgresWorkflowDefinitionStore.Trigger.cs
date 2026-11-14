// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Coordination;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Orchestration;

internal sealed partial class PostgresWorkflowDefinitionStore
{
    private readonly OperationLeases _claims = new(storage.Leases, "workflow-fire:");
    private readonly string _claimOwner = Guid.NewGuid().ToString("N");

    public Task<string?> GetTriggerCursorAsync(string workflowId, string cursorKind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorKind);
        return storage.Database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                SELECT marker FROM {_triggers} WHERE workflow_id = @id AND cursor_kind = @kind
                """, connection);
            command.Parameters.AddWithValue("id", workflowId);
            command.Parameters.AddWithValue("kind", cursorKind);
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        }, StatementRepeat.SafeToRepeat, cancellationToken);
    }

    public async Task SetTriggerCursorAsync(string workflowId, string cursorKind, string marker, CancellationToken cancellationToken = default)
    {
        ValidateTrigger(workflowId, cursorKind, marker);
        await storage.Database.ExecuteAsync(async (connection, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_triggers} AS current (workflow_id, cursor_kind, marker, ordinal)
                VALUES (@id, @kind, @marker, @ordinal)
                ON CONFLICT (workflow_id, cursor_kind) DO UPDATE
                    SET marker = excluded.marker, ordinal = excluded.ordinal
                WHERE current.ordinal < excluded.ordinal
                """, connection);
            command.Parameters.AddWithValue("id", workflowId);
            command.Parameters.AddWithValue("kind", cursorKind);
            command.Parameters.AddWithValue("marker", marker);
            command.Parameters.AddWithValue("ordinal", NpgsqlDbType.Bytea, Encoding.BigEndianUnicode.GetBytes(marker));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.NotAfterSend, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> TryClaimTriggerFireAsync(string workflowId, string cursorKind, string marker,
        TimeSpan retention, CancellationToken cancellationToken = default)
    {
        ValidateTrigger(workflowId, cursorKind, marker);
        return _claims.TryAcquireAsync(TriggerClaim(workflowId, cursorKind, marker), ClaimRetention(retention), cancellationToken);
    }

    public Task ReleaseTriggerClaimAsync(string workflowId, string cursorKind, string marker, CancellationToken cancellationToken = default)
    {
        ValidateTrigger(workflowId, cursorKind, marker);
        return _claims.ReleaseAsync(TriggerClaim(workflowId, cursorKind, marker), cancellationToken);
    }

    private LeaseClaim TriggerClaim(string workflowId, string cursorKind, string marker)
        => new("trigger:" + ClaimKey([workflowId, cursorKind, marker]), _claimOwner);

    private static string ClaimKey(string[] identity)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            identity, OrchestrationJsonContext.Default.StringArray)));

    private static TimeSpan ClaimRetention(TimeSpan retention)
        => retention > TimeSpan.Zero ? retention : TimeSpan.FromHours(24);

    private static void ValidateTrigger(string workflowId, string cursorKind, string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
    }
}

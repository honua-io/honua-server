// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Assets;

internal sealed class PostgresAssetCommandReceipts
{
    private readonly string _table = SchemaSearchPath.QualifyTable("asset_command_receipts", "honua");

    // Commands sharing a (service, command ID) serialize on a transaction-scoped advisory lock, so a retry that races the
    // original waits for it and then sees its receipt instead of failing on the receipt key.
    public async Task<AssetCommandResult?> LockAndFindAsync(
        NpgsqlTransaction transaction,
        AssetCommand command,
        CancellationToken cancellationToken)
    {
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope, 0))",
            transaction.Connection,
            transaction))
        {
            lockCommand.Parameters.AddWithValue("scope", NpgsqlDbType.Text, $"asset-command:{command.ServiceId}:{command.CommandId:D}");
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await FindAsync((transaction.Connection!, transaction), command, cancellationToken).ConfigureAwait(false);
    }

    // The stored body is returned unchanged for an identical replay; any other reuse of the ID is refused.
    public async Task<AssetCommandResult?> FindAsync(
        (NpgsqlConnection Connection, NpgsqlTransaction? Transaction) session,
        AssetCommand command,
        CancellationToken cancellationToken)
    {
        await using var select = new NpgsqlCommand(
            $"SELECT request_sha256, response::text, status_code FROM {_table} WHERE service_id = @service_id AND command_id = @command_id",
            session.Connection,
            session.Transaction);
        select.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, command.ServiceId);
        select.Parameters.AddWithValue("command_id", NpgsqlDbType.Uuid, command.CommandId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return string.Equals(reader.GetString(0), command.RequestSha256, StringComparison.Ordinal)
            ? new AssetCommandResult(reader.GetInt16(2) == 409 ? AssetCommandStatus.VersionConflict : AssetCommandStatus.Duplicate, reader.GetString(1))
            : AssetCommandResult.CommandReused;
    }

    public async Task<string> RecordAsync(NpgsqlTransaction transaction, AssetCommandReceipt receipt, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {_table} (service_id, command_id, subject_kind, subject_id, action, actor, request_sha256,
                status_code, response, recorded_at)
            VALUES (@service_id, @command_id, @subject_kind, @subject_id, @action, @actor, @request_sha256, @status_code,
                @response::jsonb, now())
            RETURNING response::text
            """,
            transaction.Connection,
            transaction);
        var command = receipt.Command;
        insert.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, command.ServiceId);
        insert.Parameters.AddWithValue("command_id", NpgsqlDbType.Uuid, command.CommandId);
        insert.Parameters.AddWithValue("subject_kind", NpgsqlDbType.Text, receipt.SubjectKind);
        insert.Parameters.AddWithValue("subject_id", NpgsqlDbType.Uuid, receipt.SubjectId);
        insert.Parameters.AddWithValue("action", NpgsqlDbType.Text, command.Action);
        insert.Parameters.AddWithValue("actor", NpgsqlDbType.Text, command.Actor);
        insert.Parameters.AddWithValue("request_sha256", NpgsqlDbType.Text, command.RequestSha256);
        insert.Parameters.AddWithValue("status_code", NpgsqlDbType.Smallint, receipt.StatusCode);
        insert.Parameters.AddWithValue("response", NpgsqlDbType.Text, receipt.ResponseJson);
        return (string)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}

internal sealed record AssetCommandReceipt(
    AssetCommand Command,
    string SubjectKind,
    Guid SubjectId,
    short StatusCode,
    string ResponseJson);

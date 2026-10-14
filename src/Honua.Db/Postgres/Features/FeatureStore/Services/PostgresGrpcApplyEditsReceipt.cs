// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Reads and writes <c>grpc_apply_edits_receipt</c>. Writes join the caller's edit transaction.
/// </summary>
internal static class PostgresGrpcApplyEditsReceipt
{
    internal const int MaxScopeLength = 128;
    internal const int MaxReceiptBytes = 16 * 1024 * 1024;

    internal static string InsertSql(string qualifiedTable) =>
        $"""
        INSERT INTO {qualifiedTable} AS existing (scope_key, receipt, expires_at)
        VALUES ($1, $2, $3)
        ON CONFLICT (scope_key) DO UPDATE
        SET receipt = EXCLUDED.receipt,
            expires_at = EXCLUDED.expires_at,
            created_at = now()
        WHERE existing.expires_at <= now()
        """;

    internal static string SelectSql(string qualifiedTable) =>
        $"""
        SELECT receipt
        FROM {qualifiedTable}
        WHERE scope_key = $1 AND expires_at > now()
        """;

    internal static async Task<bool> TryWriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string qualifiedTable,
        string scope,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset expiresAt,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);

        await using var command = new NpgsqlCommand(InsertSql(qualifiedTable), connection)
        {
            Transaction = transaction,
        };
        if (commandTimeoutSeconds > 0)
        {
            command.CommandTimeout = commandTimeoutSeconds;
        }

        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = scope });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bytea,
            Value = payload.ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = expiresAt });
        var written = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return written == 1;
    }

    internal static async Task<byte[]?> TryReadAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        string scope,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);

        await using var command = new NpgsqlCommand(SelectSql(qualifiedTable), connection);
        if (commandTimeoutSeconds > 0)
        {
            command.CommandTimeout = commandTimeoutSeconds;
        }

        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = scope });
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is byte[] bytes ? bytes : null;
    }
}

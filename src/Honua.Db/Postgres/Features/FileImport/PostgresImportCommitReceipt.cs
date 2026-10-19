// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.FileImport;

/// <summary>
/// Stores an immutable original result in the destination transaction. This internal
/// persistence primitive grants no authority to execute an import or observe its result.
/// </summary>
internal static class PostgresImportCommitReceipt
{
    internal const int MaximumReceiptBytes = 1024 * 1024;
    private const string FingerprintPrefix = "v1:sha256:";

    internal static async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string honuaSchema,
        string operationId,
        string intentFingerprint,
        ReadOnlyMemory<byte> receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateIdentity(operationId, intentFingerprint);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException("The import receipt must join the destination transaction.");
        }
        if (receipt.Length is < 1 or > MaximumReceiptBytes)
        {
            throw new ArgumentException("The import receipt exceeds its bounds.", nameof(receipt));
        }
        var table = SchemaSearchPath.QualifyTable("import_commit_receipt", honuaSchema);
        // A conflicting insert MUST abort the transaction, including its destination
        // mutation. Never upsert, ignore a conflict, or replace an expired receipt.
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {table} (operation_id, intent_fingerprint, receipt) VALUES ($1, $2, $3)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = operationId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = intentFingerprint });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = receipt.ToArray() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]?> TryReadAsync(
        NpgsqlConnection connection,
        string honuaSchema,
        string operationId,
        string intentFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateIdentity(operationId, intentFingerprint);
        var table = SchemaSearchPath.QualifyTable("import_commit_receipt", honuaSchema);
        await using var command = new NpgsqlCommand(
            $"SELECT intent_fingerprint, receipt FROM {table} WHERE operation_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = operationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (!string.Equals(reader.GetString(0), intentFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The committed import belongs to a different immutable intent.");
        }
        var receipt = reader.GetFieldValue<byte[]>(1);
        if (receipt.Length is < 1 or > MaximumReceiptBytes)
        {
            throw new InvalidOperationException("The committed import receipt is invalid.");
        }
        return receipt;
    }

    private static void ValidateIdentity(string operationId, string intentFingerprint)
    {
        if (operationId is null || operationId.Length != 32 || operationId.Any(static value => !char.IsAsciiHexDigitLower(value))
            || intentFingerprint is null || !intentFingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal)
            || intentFingerprint.Length != FingerprintPrefix.Length + 64
            || intentFingerprint.Skip(FingerprintPrefix.Length).Any(static value => !char.IsAsciiHexDigitLower(value)))
        {
            throw new ArgumentException("A canonical import operation and full job intent fingerprint are required.");
        }
    }
}

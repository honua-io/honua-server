// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal static partial class PostgresGrpcApplyEditsReceipt
{
    internal readonly record struct EvidenceContext
    {
        internal required NpgsqlConnection Connection { get; init; }
        internal NpgsqlTransaction? Transaction { get; init; }
        internal required string Table { get; init; }
        internal required int TimeoutSeconds { get; init; }
    }

    internal readonly record struct EvidencePublication
    {
        internal required string Scope { get; init; }
        internal required ReadOnlyMemory<byte> Payload { get; init; }
        internal required FeatureEditResult Result { get; init; }
    }

    internal static async Task<FeatureEditReceiptReplay?> TryReadWithEvidenceAsync(
        EvidenceContext context, string scope, CancellationToken cancellationToken)
    {
        await DeleteExpiredAsync(context.Connection, context.Transaction, context.Table,
            context.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
        var hasEvidence = await HasEvidenceColumnAsync(context, cancellationToken).ConfigureAwait(false);
        var sql = hasEvidence
            ? $"SELECT receipt, operation_evidence::text FROM {context.Table} WHERE scope_key = $1 AND expires_at > now()"
            : SelectSql(context.Table);
        await using var command = CreateEvidenceCommand(context, sql);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = scope });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var payload = reader.GetFieldValue<byte[]>(0);
        var evidence = hasEvidence && !reader.IsDBNull(1) ? reader.GetString(1) : null;
        return new FeatureEditReceiptReplay
        {
            Payload = payload,
            Evidence = FeatureEditReceiptEvidenceJson.Decode(evidence, payload)
        };
    }

    internal static async Task WriteEvidenceAsync(
        EvidenceContext context, EvidencePublication publication, CancellationToken cancellationToken)
    {
        if (!await HasEvidenceColumnAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        var evidence = FeatureEditReceiptEvidenceJson.Encode(publication.Result, publication.Payload);
        if (evidence is null)
        {
            return;
        }
        await using var command = CreateEvidenceCommand(context,
            $"UPDATE {context.Table} SET operation_evidence = $2 WHERE scope_key = $1");
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = publication.Scope });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = evidence });
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The edit receipt evidence could not be persisted.");
        }
    }

    private static async Task<bool> HasEvidenceColumnAsync(
        EvidenceContext context, CancellationToken cancellationToken)
    {
        await using var command = CreateEvidenceCommand(context,
            "SELECT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) " +
            "AND attname = 'operation_evidence' AND NOT attisdropped)");
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = context.Table });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static NpgsqlCommand CreateEvidenceCommand(EvidenceContext context, string sql)
    {
        var command = new NpgsqlCommand(sql, context.Connection) { Transaction = context.Transaction };
        if (context.TimeoutSeconds > 0)
        {
            command.CommandTimeout = context.TimeoutSeconds;
        }
        return command;
    }
}

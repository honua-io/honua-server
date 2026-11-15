// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    private readonly record struct RowMutationResult<T>(T Value, ImmutableArray<FeatureOperationEvidence> Evidence);
    private readonly record struct EvidenceMutationContext(NpgsqlConnection Connection, NpgsqlTransaction? Transaction);

    private async Task<RowMutationResult<T>> ExecuteEvidenceMutationAsync<T>(
        EvidenceMutationContext context,
        Func<NpgsqlConnection, NpgsqlTransaction?, CancellationToken, Task<T>> mutate,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = context;
        await BeginOperationEvidenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var value = await mutate(connection, transaction, cancellationToken).ConfigureAwait(false);
        var evidence = await ReadOperationEvidenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return new(value, evidence);
    }

    private async Task BeginOperationEvidenceAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (!FeatureOperationEvidenceScope.IsActive)
        {
            return;
        }
        ArgumentNullException.ThrowIfNull(transaction);
        const string sql = """
            WITH capture AS (
                SELECT EXISTS (
                    SELECT 1 FROM pg_trigger t
                    JOIN pg_class c ON c.oid = t.tgrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'honua' AND c.relname = 'feature_changes'
                      AND t.tgname = 'capture_feature_operation_evidence' AND NOT t.tgisinternal
                      AND (t.tgenabled = 'A' OR
                           t.tgenabled = CASE current_setting('session_replication_role')
                               WHEN 'replica' THEN 'R'::"char" ELSE 'O'::"char" END)) AS available)
            SELECT set_config('honua.capture_operation_evidence', CASE WHEN available THEN 'on' ELSE 'off' END, true),
                   set_config('honua.operation_evidence', CASE WHEN available THEN '[]' ELSE '' END, true)
            FROM capture
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImmutableArray<FeatureOperationEvidence>> ReadOperationEvidenceAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (!FeatureOperationEvidenceScope.IsActive)
        {
            return default;
        }
        ArgumentNullException.ThrowIfNull(transaction);
        await using var command = new NpgsqlCommand(
            "SELECT NULLIF(current_setting('honua.operation_evidence', true), '')", connection, transaction);
        ApplyCommandTimeout(command, _queryTimeoutSeconds);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is not string captured)
        {
            return default;
        }
        using var document = JsonDocument.Parse(captured);
        return document.RootElement.EnumerateArray().Select(FeatureOperationEvidenceJson.Read).ToImmutableArray();
    }
}

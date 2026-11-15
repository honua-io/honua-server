// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Protocols.Wfs20;

internal sealed class PostgresWfsStoredQueryStore(JobStoreDatabase database) : IWfsStoredQueryStore
{
    private readonly string _queries = database.Table("wfs_stored_queries");

    private readonly string _create = $"""
        WITH scope_size AS (
            SELECT count(*) AS stored FROM {database.Table("wfs_stored_queries")} WHERE scope = @scope),
        inserted AS (
            INSERT INTO {database.Table("wfs_stored_queries")} (scope, query_id, definition)
            SELECT @scope, @query_id, @definition FROM scope_size WHERE stored < @max_per_scope
            ON CONFLICT DO NOTHING
            RETURNING 1)
        SELECT (SELECT count(*) FROM inserted), (SELECT stored FROM scope_size),
            EXISTS (SELECT 1 FROM {database.Table("wfs_stored_queries")}
                    WHERE scope = @scope AND query_id = @query_id)
        """;

    public Task<IReadOnlyList<string>> ListAsync(string scope, CancellationToken cancellationToken)
        => database.ExecuteAsync<IReadOnlyList<string>>(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT definition FROM {_queries} WHERE scope = @scope ORDER BY query_id",
                    connection);
                command.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Text) { Value = scope });
                var definitions = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    definitions.Add(reader.GetString(0));
                }

                return definitions;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task<string?> GetAsync(WfsStoredQueryKey key, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT definition FROM {_queries} WHERE scope = @scope AND query_id = @query_id",
                    connection);
                AddKey(command, key);
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task<WfsStoredQueryCreateResult> TryCreateAsync(WfsStoredQueryEntry entry, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var transaction = await connection.BeginTransactionAsync(
                    System.Data.IsolationLevel.ReadCommitted, token).ConfigureAwait(false);
                await LockScopeAsync(transaction, entry.Key.Scope, token).ConfigureAwait(false);
                var result = await InsertAsync(transaction, entry, token).ConfigureAwait(false);
                await transaction.CommitSafelyAsync(token).ConfigureAwait(false);
                return result;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    public Task<bool> DeleteAsync(WfsStoredQueryKey key, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"DELETE FROM {_queries} WHERE scope = @scope AND query_id = @query_id",
                    connection);
                AddKey(command, key);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            },
            StatementRepeat.NotAfterSend,
            cancellationToken);

    private async Task LockScopeAsync(NpgsqlTransaction transaction, string scope, CancellationToken cancellationToken)
    {
        // The count statement needs a fresh snapshot after the preceding creator commits.
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope_lock, 0))",
            transaction.Connection!, transaction);
        command.Parameters.Add(new NpgsqlParameter("scope_lock", NpgsqlDbType.Text)
        {
            Value = $"wfs-stored-query:{_queries}:{scope}"
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WfsStoredQueryCreateResult> InsertAsync(
        NpgsqlTransaction transaction, WfsStoredQueryEntry entry, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_create, transaction.Connection!, transaction);
        AddKey(command, entry.Key);
        command.Parameters.Add(new NpgsqlParameter("definition", NpgsqlDbType.Json) { Value = entry.Definition });
        command.Parameters.Add(new NpgsqlParameter("max_per_scope", NpgsqlDbType.Bigint) { Value = IWfsStoredQueryStore.MaxPerScope });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reader.GetInt64(0) == 1)
        {
            return WfsStoredQueryCreateResult.Created;
        }

        return reader.GetBoolean(2) || reader.GetInt64(1) < IWfsStoredQueryStore.MaxPerScope
            ? WfsStoredQueryCreateResult.DuplicateId
            : WfsStoredQueryCreateResult.ScopeFull;
    }

    private static void AddKey(NpgsqlCommand command, WfsStoredQueryKey key)
    {
        command.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Text) { Value = key.Scope });
        command.Parameters.Add(new NpgsqlParameter("query_id", NpgsqlDbType.Text) { Value = key.NormalizedQueryId });
    }
}

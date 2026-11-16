// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.ControlPlane;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed record ConfigurationHistoryQuery(string Kind, string Key, int Limit = 50)
{
    public long? BeforeGeneration { get; init; }
}

internal sealed record ConfigurationHistoryEntry(long Generation, DateTimeOffset RecordedAt)
{
    public bool Deleted { get; init; }
}
internal sealed record ConfigurationRestore(string Key, long Generation, long ExpectedGeneration);

internal sealed partial class PostgresConfigurationDocuments
{
    private readonly string _history = database.Table("configuration_document_history");

    public Task<IReadOnlyList<ConfigurationHistoryEntry>> ReadHistoryAsync(ConfigurationHistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, 100);
        return database.ExecuteAsync<IReadOnlyList<ConfigurationHistoryEntry>>(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                SELECT generation, payload IS NULL, recorded_at FROM {_history}
                WHERE kind = @kind AND key = @key AND generation < @before
                ORDER BY generation DESC LIMIT @limit
                """, connection);
            command.Parameters.AddWithValue("kind", query.Kind);
            command.Parameters.AddWithValue("key", query.Key);
            command.Parameters.AddWithValue("before", query.BeforeGeneration ?? long.MaxValue);
            command.Parameters.AddWithValue("limit", query.Limit);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var history = new List<ConfigurationHistoryEntry>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                history.Add(new(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(2)) { Deleted = reader.GetBoolean(1) });
            }
            return history;
        }, StatementRepeat.SafeToRepeat, cancellationToken);
    }

    public Task<bool> RestoreAsync<T>(ConfigurationCollection<T> collection, ConfigurationRestore restore, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(restore.Generation);
        ArgumentOutOfRangeException.ThrowIfNegative(restore.ExpectedGeneration);
        return database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            if (await LockGenerationAsync(connection, token).ConfigureAwait(false) != restore.ExpectedGeneration) { return false; }
            var change = await ReadRestoreChangeAsync(connection, (collection, restore), token).ConfigureAwait(false);
            if (change is null) { return false; }
            await using var command = CreateWriteCommand(connection, change);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) { return false; }
            await transaction.CommitSafelyAsync(token).ConfigureAwait(false);
            return true;
        }, StatementRepeat.NotAfterSend, cancellationToken);
    }

    private async Task<ConfigurationChange?> ReadRestoreChangeAsync<T>(NpgsqlConnection connection,
        (ConfigurationCollection<T> Collection, ConfigurationRestore Restore) target, CancellationToken token)
    {
        var (collection, restore) = target;
        await using var command = new NpgsqlCommand($"""
            SELECT history.payload, COALESCE(document.revision, 0)
            FROM {_history} history LEFT JOIN {_documents} document
              ON document.kind = history.kind AND document.key = history.key
            WHERE history.kind = @kind AND history.key = @key AND history.generation = @generation
            """, connection);
        command.Parameters.AddWithValue("kind", collection.Kind);
        command.Parameters.AddWithValue("key", restore.Key);
        command.Parameters.AddWithValue("generation", restore.Generation);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) { return null; }
        var payload = reader.IsDBNull(0) ? null : reader.GetFieldValue<byte[]>(0);
        if (payload is not null)
        {
            _ = JsonSerializer.Deserialize(Protector(collection.Kind).Unprotect(payload), collection.Type)
                ?? throw new JsonException("Configuration document is empty.");
        }
        return new(collection.Kind, restore.Key, reader.GetInt64(1), payload);
    }
}

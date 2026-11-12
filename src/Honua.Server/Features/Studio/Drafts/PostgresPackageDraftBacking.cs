// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Studio.Drafts;

// Expiry is decided against the database clock, so every node agrees on when a draft ends. The ttl
// the ledger passes to reads is already recorded in expires_at by the write.
internal sealed class PostgresPackageDraftBacking(JobStoreDatabase database) : IPackageDraftBacking, IExpiredRowSweep
{
    private const string LiveDraft = "kind = @kind AND draft_id = @draft_id AND expires_at > now()";

    private readonly string _drafts = database.Table("package_drafts");

    public Task UpsertAsync(
        PackageDraftKind kind,
        string id,
        PackageDraftRecord record,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (ttl <= TimeSpan.Zero)
        {
            return RemoveAsync(kind, id, cancellationToken);
        }

        return database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    INSERT INTO {_drafts} (kind, draft_id, owner_id, written_at, record, expires_at)
                    VALUES (@kind, @draft_id, @owner_id, @written_at, @record, now() + @ttl)
                    ON CONFLICT (kind, draft_id) DO UPDATE SET
                        owner_id = EXCLUDED.owner_id, written_at = EXCLUDED.written_at,
                        record = EXCLUDED.record, expires_at = EXCLUDED.expires_at
                    """,
                    connection);
                AddKey(command, kind, id);
                command.Parameters.Add(new NpgsqlParameter("owner_id", NpgsqlDbType.Text) { Value = record.OwnerId });
                command.Parameters.Add(new NpgsqlParameter("written_at", NpgsqlDbType.TimestampTz) { Value = record.WrittenAt.ToUniversalTime() });
                command.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json) { Value = Encode(kind, record) });
                command.Parameters.Add(new NpgsqlParameter("ttl", NpgsqlDbType.Interval) { Value = ttl });
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);
    }

    public Task<PackageDraftRecord?> TryGetAsync(
        PackageDraftKind kind,
        string id,
        TimeSpan ttl,
        CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT owner_id, written_at, record::text FROM {_drafts} WHERE {LiveDraft}",
                    connection);
                AddKey(command, kind, id);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return null;
                }

                var ownerId = reader.GetString(0);
                var writtenAt = reader.GetFieldValue<DateTimeOffset>(1);
                return Decode(kind, new PackageDraftRecord(ownerId, writtenAt, null, null), reader.GetString(2));
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task<IReadOnlyList<PackageDraftLiveEntry>> ListLiveOldestFirstAsync(
        PackageDraftKind kind,
        TimeSpan ttl,
        CancellationToken cancellationToken)
        => database.ExecuteAsync<IReadOnlyList<PackageDraftLiveEntry>>(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    SELECT draft_id, written_at FROM {_drafts}
                    WHERE kind = @kind AND expires_at > now()
                    ORDER BY written_at, draft_id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = KindName(kind) });
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var live = new List<PackageDraftLiveEntry>();
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    live.Add(new PackageDraftLiveEntry(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1)));
                }

                return live;
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task RemoveAsync(PackageDraftKind kind, string id, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"DELETE FROM {_drafts} WHERE kind = @kind AND draft_id = @draft_id",
                    connection);
                AddKey(command, kind, id);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
        => database.ExecuteAsync(
            async (connection, _, token) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    WITH expired AS (
                        SELECT kind, draft_id FROM {_drafts}
                        WHERE expires_at <= now()
                        ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
                    DELETE FROM {_drafts} AS draft USING expired
                    WHERE draft.kind = expired.kind AND draft.draft_id = expired.draft_id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            StatementRepeat.SafeToRepeat,
            cancellationToken);

    private static string Encode(PackageDraftKind kind, PackageDraftRecord record) => kind switch
    {
        PackageDraftKind.Map => JsonSerializer.Serialize(record.Map, PackagingJsonContext.Default.MapPackage),
        PackageDraftKind.App => JsonSerializer.Serialize(record.App, PackagingJsonContext.Default.AppPackage),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static PackageDraftRecord? Decode(PackageDraftKind kind, PackageDraftRecord stored, string packageJson)
        => kind switch
        {
            PackageDraftKind.Map => JsonSerializer.Deserialize(packageJson, PackagingJsonContext.Default.MapPackage) is { } map
                ? stored with { Map = map }
                : null,
            PackageDraftKind.App => JsonSerializer.Deserialize(packageJson, PackagingJsonContext.Default.AppPackage) is { } app
                ? stored with { App = app }
                : null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static void AddKey(NpgsqlCommand command, PackageDraftKind kind, string id)
    {
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = KindName(kind) });
        command.Parameters.Add(new NpgsqlParameter("draft_id", NpgsqlDbType.Text) { Value = id });
    }

    private static string KindName(PackageDraftKind kind) => kind switch
    {
        PackageDraftKind.Map => "map",
        PackageDraftKind.App => "app",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

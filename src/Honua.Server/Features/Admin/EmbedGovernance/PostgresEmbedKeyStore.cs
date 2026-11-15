// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.EmbedGovernance.Abstractions;
using Honua.Core.Features.EmbedGovernance.Domain;
using Honua.Server.Features.Console.Services;
using Honua.Server.Features.Infrastructure.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Admin.EmbedGovernance;

// Embed keys in the system database. A key is one document under the shared configuration generation (ADR-0086),
// so a revocation or rotation on one task is refused on every task. Use is counted in its own table: every task
// adds to the same rate window, and a request is what records the last use (validation alone only reads).
internal sealed class PostgresEmbedKeyStore(
    PostgresConfigurationDocuments documents,
    JobStoreDatabase database,
    TimeProvider timeProvider,
    IHttpContextAccessor? contexts = null) : IEmbedKeyStore
{
    private readonly ConfigurationCollectionStore<EmbedKeyRecord> _keys = new(
        documents, new("embed-key", ConsoleStateJsonContext.Default.EmbedKeyRecord), contexts);
    private readonly string _usage = database.Table("embed_key_usage");
    private HashIndex? _byHash;

    public async Task<IReadOnlyList<EmbedKeyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var keys = await _keys.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false);
        var lastUsed = await ReadLastUsedAsync(null, cancellationToken).ConfigureAwait(false);
        return keys.Values
            .Select(key => WithLastUsed(key, lastUsed))
            .OrderBy(static key => key.CreatedAt)
            .ToArray();
    }

    public async Task<EmbedKeyCreateResult> CreateAsync(
        string name, EmbedKeyScope scope, DateTimeOffset? expiresAt, string? createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var now = timeProvider.GetUtcNow();
        var generated = EmbedKeyMaterial.Generate();
        var record = new EmbedKeyRecord
        {
            Id = Guid.NewGuid(),
            Name = name,
            KeyPrefix = EmbedKeyMaterial.DisplayPrefix(generated),
            KeyHash = EmbedKeyMaterial.Hash(generated),
            Scope = scope,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = expiresAt,
            CreatedBy = createdBy,
        };
        await _keys.CreateAsync(Key(record.Id), record, cancellationToken).ConfigureAwait(false);
        return new(record, generated);
    }

    public async Task<EmbedKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var keys = await _keys.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false);
        return keys.TryGetValue(Key(id), out var key)
            ? WithLastUsed(key, await ReadLastUsedAsync(id, cancellationToken).ConfigureAwait(false))
            : null;
    }

    public async Task<EmbedKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        string? generated = null;
        var rotated = await _keys.MutateAsync(Key(id), existing =>
        {
            generated = null;
            if (existing is null || existing.RevokedAt is not null)
            {
                return existing;
            }
            generated = EmbedKeyMaterial.Generate();
            return existing with
            {
                KeyPrefix = EmbedKeyMaterial.DisplayPrefix(generated),
                KeyHash = EmbedKeyMaterial.Hash(generated),
                UpdatedAt = now,
                RotatedAt = now,
                LastUsedAt = null,
            };
        }, cancellationToken).ConfigureAwait(false);
        if (rotated is null || generated is null)
        {
            return null;
        }
        // The new secret starts with no recorded use and a fresh rate window.
        await database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"DELETE FROM {_usage} WHERE key_id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.SafeToRepeat, cancellationToken).ConfigureAwait(false);
        return new(rotated, generated);
    }

    public async Task<EmbedKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var revoked = await _keys.MutateAsync(Key(id),
            existing => existing is null ? null : existing with { UpdatedAt = now, RevokedAt = existing.RevokedAt ?? now },
            cancellationToken).ConfigureAwait(false);
        return revoked is null
            ? null
            : WithLastUsed(revoked, await ReadLastUsedAsync(id, cancellationToken).ConfigureAwait(false));
    }

    public async Task<EmbedKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyMaterial))
        {
            return null;
        }
        var keys = await _keys.ReadCurrentByKeyAsync(cancellationToken).ConfigureAwait(false);
        var index = Volatile.Read(ref _byHash);
        if (index is null || !ReferenceEquals(index.Keys, keys))
        {
            index = new(keys, keys.Values.ToDictionary(static key => Convert.ToHexString(key.KeyHash), StringComparer.Ordinal));
            Volatile.Write(ref _byHash, index);
        }
        var now = timeProvider.GetUtcNow();
        return index.ByHash.TryGetValue(Convert.ToHexString(EmbedKeyMaterial.Hash(keyMaterial)), out var record)
            && record.GetStatus(now) == EmbedKeyStatus.Active
            ? new(record with { LastUsedAt = now, UpdatedAt = now })
            : null;
    }

    // One statement on the database clock, so tasks that count the same key at once cannot lose a request
    // and agree on when the window started.
    public Task<int> RecordRequestAsync(Guid id, TimeSpan window, CancellationToken cancellationToken)
    {
        if (window <= TimeSpan.Zero)
        {
            window = TimeSpan.FromMinutes(1);
        }
        return database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_usage} AS stored (key_id, window_started_at, request_count, last_used_at)
                VALUES (@id, now(), 1, now())
                ON CONFLICT (key_id) DO UPDATE SET
                    window_started_at = CASE WHEN stored.window_started_at + @window <= now() THEN now() ELSE stored.window_started_at END,
                    request_count = CASE WHEN stored.window_started_at + @window <= now() THEN 1
                        ELSE LEAST(stored.request_count, 2147483646) + 1 END,
                    last_used_at = now()
                RETURNING request_count
                """, connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.Add(new NpgsqlParameter("window", NpgsqlDbType.Interval) { Value = window });
            return (int)(await command.ExecuteScalarAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Embed key usage was not recorded."));
        }, StatementRepeat.NotAfterSend, cancellationToken);
    }

    private Task<Dictionary<Guid, DateTimeOffset>> ReadLastUsedAsync(Guid? id, CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand(
                $"SELECT key_id, last_used_at FROM {_usage} WHERE @id::uuid IS NULL OR key_id = @id", connection);
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id is { } value ? value : DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var lastUsed = new Dictionary<Guid, DateTimeOffset>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                lastUsed[reader.GetGuid(0)] = reader.GetFieldValue<DateTimeOffset>(1);
            }
            return lastUsed;
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    private static EmbedKeyRecord WithLastUsed(EmbedKeyRecord key, Dictionary<Guid, DateTimeOffset> lastUsed)
        => lastUsed.TryGetValue(key.Id, out var usedAt) ? key with { LastUsedAt = usedAt } : key;

    private static string Key(Guid id) => id.ToString("D");

    private sealed record HashIndex(IReadOnlyDictionary<string, EmbedKeyRecord> Keys, Dictionary<string, EmbedKeyRecord> ByHash);
}

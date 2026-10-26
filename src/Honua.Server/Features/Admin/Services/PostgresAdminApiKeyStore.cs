// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Infrastructure.Authentication;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Admin API keys in the metadata schema, shared by every server instance that uses the same
/// database. It is the registry for multi-instance hosts that have no Redis (AWS Lambda,
/// <c>Deployment:Mode=MultiNode</c>), where a per-process dictionary would leave a key minted on
/// one instance unknown to the others (honua-server#6487). Only the SHA-256 of the key material
/// is stored.
/// </summary>
internal sealed class PostgresAdminApiKeyStore(
    NpgsqlDataSource dataSource,
    TimeProvider? timeProvider = null,
    string? schema = null) : IAdminApiKeyStore
{
    internal const string TableName = "admin_api_keys";

    private const string Columns =
        "id, name, key_prefix, key_hash, permissions, created_at, updated_at, expires_at, last_used_at, rotated_at, revoked_at, created_by, " +
        "allowed_referrers, allowed_ip_ranges";

    private const int DisplayPrefixLength = 12;
    private const int WriteAttempts = 16;

    // LastUsedAt is diagnostic. Rewriting it on every authenticated call is a write per request.
    private static readonly TimeSpan UsageWriteInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly string _table = Qualify(schema);

    public async Task<IReadOnlyList<AdminApiKeyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<AdminApiKeyRecord>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM {_table} ORDER BY created_at, id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public Task<AdminApiKeyCreateResult> CreateAsync(
        string name,
        IReadOnlyList<string> permissions,
        DateTimeOffset? expiresAt,
        string? createdBy,
        CancellationToken cancellationToken)
        => CreateAsync(name, permissions, expiresAt, createdBy, restrictions: null, cancellationToken);

    public async Task<AdminApiKeyCreateResult> CreateAsync(
        string name,
        IReadOnlyList<string> permissions,
        DateTimeOffset? expiresAt,
        string? createdBy,
        AdminApiKeyRestrictions? restrictions,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var key = InMemoryAdminApiKeyStore.GenerateForDurableStore();
        var record = new AdminApiKeyRecord(
            Guid.NewGuid(), name, DisplayPrefix(key), HashKey(key), NormalizePermissions(permissions),
            now, now, expiresAt, null, null, null, createdBy,
            AdminApiKeyRestrictions.Normalize(restrictions?.AllowedReferrers),
            AdminApiKeyRestrictions.Normalize(restrictions?.AllowedIpRanges));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_table} ({Columns})
            VALUES (@id, @name, @prefix, @hash, @permissions, @created, @updated, @expires, NULL, NULL, NULL, @createdBy,
                    @allowedReferrers, @allowedIpRanges)
            """, connection);
        command.Parameters.AddWithValue("id", record.Id);
        command.Parameters.AddWithValue("name", record.Name);
        command.Parameters.AddWithValue("prefix", record.KeyPrefix);
        command.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = record.KeyHash });
        command.Parameters.Add(new NpgsqlParameter("permissions", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = record.Permissions.ToArray()
        });
        command.Parameters.AddWithValue("created", record.CreatedAt);
        command.Parameters.AddWithValue("updated", record.UpdatedAt);
        command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = (object?)record.ExpiresAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("createdBy", NpgsqlDbType.Text) { Value = (object?)record.CreatedBy ?? DBNull.Value });
        command.Parameters.Add(TextArrayOrNull("allowedReferrers", record.AllowedReferrers));
        command.Parameters.Add(TextArrayOrNull("allowedIpRanges", record.AllowedIpRanges));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new AdminApiKeyCreateResult(record, key);
    }

    public async Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadOneAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            var existing = await ReadOneAsync(connection, id, cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            // Rotation keeps ExpiresAt, so rotating an expired key would return material that
            // ValidateAsync rejects at once. Approved-operation credentials are single-purpose.
            if (existing is null || existing.RevokedAt is not null
                || existing.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant)
                || (existing.ExpiresAt.HasValue && existing.ExpiresAt.Value <= now))
            {
                return null;
            }

            var key = InMemoryAdminApiKeyStore.GenerateForDurableStore();
            // Conditional on the hash just read and on the key still being unrevoked: an
            // unconditional write would put an unrevoked record back over a concurrent revocation.
            await using var command = new NpgsqlCommand($"""
                UPDATE {_table}
                SET key_prefix = @prefix, key_hash = @hash, updated_at = @now, rotated_at = @now, last_used_at = NULL
                WHERE id = @id AND key_hash = @previous AND revoked_at IS NULL
                RETURNING {Columns}
                """, connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("prefix", DisplayPrefix(key));
            command.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = HashKey(key) });
            command.Parameters.Add(new NpgsqlParameter("previous", NpgsqlDbType.Bytea) { Value = existing.KeyHash });
            command.Parameters.AddWithValue("now", now);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new AdminApiKeyCreateResult(Read(reader), key);
            }
        }

        throw new InvalidOperationException($"Admin API key {id:D} changed on every rotation attempt.");
    }

    public async Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE {_table}
            SET updated_at = @now, revoked_at = COALESCE(revoked_at, @now)
            WHERE id = @id
            RETURNING {Columns}
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<AdminApiKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyMaterial))
        {
            return null;
        }

        var hash = HashKey(keyMaterial);
        var now = _timeProvider.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        AdminApiKeyRecord? record;
        await using (var select = new NpgsqlCommand($"""
            SELECT {Columns} FROM {_table}
            WHERE key_hash = @hash AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at > @now)
            """, connection))
        {
            select.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = hash });
            select.Parameters.AddWithValue("now", now);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            record = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
        }

        if (record is null || !CryptographicOperations.FixedTimeEquals(hash, record.KeyHash))
        {
            return null;
        }

        if (record.LastUsedAt is { } lastUsed && now - lastUsed < UsageWriteInterval)
        {
            return new AdminApiKeyValidationResult(record);
        }

        // The write is conditional on the key still being the same unrevoked secret, so a
        // revoke or rotation that landed after the read still denies.
        await using var update = new NpgsqlCommand($"""
            UPDATE {_table}
            SET last_used_at = @now, updated_at = @now
            WHERE id = @id AND key_hash = @hash AND revoked_at IS NULL
            RETURNING {Columns}
            """, connection);
        update.Parameters.AddWithValue("id", record.Id);
        update.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = hash });
        update.Parameters.AddWithValue("now", now);
        await using var updated = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await updated.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AdminApiKeyValidationResult(Read(updated))
            : null;
    }

    private async Task<AdminApiKeyRecord?> ReadOneAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM {_table} WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static AdminApiKeyRecord Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetFieldValue<byte[]>(3),
        reader.GetFieldValue<string[]>(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<string[]>(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<string[]>(13));

    private static NpgsqlParameter TextArrayOrNull(string name, IReadOnlyList<string>? values)
        => new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values is { Count: > 0 } ? values.ToArray() : DBNull.Value };

    private static string Qualify(string? schema)
    {
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(string.IsNullOrWhiteSpace(schema) ? "honua" : schema.Trim())
            + "." + builder.QuoteIdentifier(TableName);
    }

    private static string[] NormalizePermissions(IReadOnlyList<string> permissions)
    {
        var normalized = permissions
            .Select(static permission => permission.Trim())
            .Where(static permission => permission.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? ["admin:*"] : normalized;
    }

    private static string DisplayPrefix(string key) => key[..Math.Min(DisplayPrefixLength, key.Length)];

    private static byte[] HashKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

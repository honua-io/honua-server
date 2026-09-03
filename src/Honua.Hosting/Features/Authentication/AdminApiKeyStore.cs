// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace Honua.Infrastructure.Authentication;

internal interface IAdminApiKeyStore
{
    Task<IReadOnlyList<AdminApiKeyRecord>> ListAsync(CancellationToken cancellationToken);

    Task<AdminApiKeyCreateResult> CreateAsync(
        string name,
        IReadOnlyList<string> permissions,
        DateTimeOffset? expiresAt,
        string? createdBy,
        CancellationToken cancellationToken);

    Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken);

    Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken);

    Task<AdminApiKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken);
}

internal sealed record AdminApiKeyRecord(
    Guid Id,
    string Name,
    string KeyPrefix,
    byte[] KeyHash,
    IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RotatedAt,
    DateTimeOffset? RevokedAt,
    string? CreatedBy);

internal sealed record AdminApiKeyCreateResult(AdminApiKeyRecord Record, string Key);

internal sealed record AdminApiKeyValidationResult(AdminApiKeyRecord Record);

internal sealed class InMemoryAdminApiKeyStore(TimeProvider? timeProvider = null) : IAdminApiKeyStore
{
    private const string KeyPrefix = "hnua_";
    private const int KeyByteCount = 32;
    private const int DisplayPrefixLength = 12;

    private readonly ConcurrentDictionary<Guid, AdminApiKeyRecord> _keys = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<IReadOnlyList<AdminApiKeyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AdminApiKeyRecord> result = _keys.Values
            .OrderBy(key => key.CreatedAt)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<AdminApiKeyCreateResult> CreateAsync(
        string name,
        IReadOnlyList<string> permissions,
        DateTimeOffset? expiresAt,
        string? createdBy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var generated = GenerateKeyMaterial();
        var record = new AdminApiKeyRecord(
            Guid.NewGuid(),
            name,
            CreateDisplayPrefix(generated),
            HashKey(generated),
            NormalizePermissions(permissions),
            now,
            now,
            expiresAt,
            LastUsedAt: null,
            RotatedAt: null,
            RevokedAt: null,
            createdBy);

        if (!_keys.TryAdd(record.Id, record))
        {
            throw new InvalidOperationException("Generated duplicate admin API key identifier.");
        }

        return Task.FromResult(new AdminApiKeyCreateResult(record, generated));
    }

    public Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keys.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_keys.TryGetValue(id, out var existing) || existing.RevokedAt is not null)
        {
            return Task.FromResult<AdminApiKeyCreateResult?>(null);
        }

        var now = _timeProvider.GetUtcNow();
        var generated = GenerateKeyMaterial();
        var updated = existing with
        {
            KeyPrefix = CreateDisplayPrefix(generated),
            KeyHash = HashKey(generated),
            UpdatedAt = now,
            RotatedAt = now,
            LastUsedAt = null,
        };

        _keys[id] = updated;
        return Task.FromResult<AdminApiKeyCreateResult?>(new AdminApiKeyCreateResult(updated, generated));
    }

    public Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_keys.TryGetValue(id, out var existing))
        {
            return Task.FromResult<AdminApiKeyRecord?>(null);
        }

        var now = _timeProvider.GetUtcNow();
        var updated = existing with
        {
            UpdatedAt = now,
            RevokedAt = existing.RevokedAt ?? now,
        };

        _ = _keys.TryUpdate(id, updated, existing);
        return Task.FromResult<AdminApiKeyRecord?>(updated);
    }

    public Task<AdminApiKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(keyMaterial))
        {
            return Task.FromResult<AdminApiKeyValidationResult?>(null);
        }

        var providedHash = HashKey(keyMaterial);
        var now = _timeProvider.GetUtcNow();

        foreach (var record in _keys.Values)
        {
            if (record.RevokedAt is not null ||
                (record.ExpiresAt.HasValue && record.ExpiresAt.Value <= now))
            {
                continue;
            }

            if (!CryptographicOperations.FixedTimeEquals(providedHash, record.KeyHash))
            {
                continue;
            }

            var updated = record with
            {
                LastUsedAt = now,
                UpdatedAt = now,
            };
            if (_keys.TryUpdate(record.Id, updated, record))
            {
                return Task.FromResult<AdminApiKeyValidationResult?>(new AdminApiKeyValidationResult(updated));
            }

            // A concurrent revoke/rotate won the update. Do not return a stale
            // validation result; retry against the current dictionary snapshot.
            continue;
        }

        return Task.FromResult<AdminApiKeyValidationResult?>(null);
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

    internal static string GenerateForDurableStore() => GenerateKeyMaterial();

    private static string GenerateKeyMaterial()
    {
        var bytes = RandomNumberGenerator.GetBytes(KeyByteCount);
        return KeyPrefix + Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string CreateDisplayPrefix(string keyMaterial)
    {
        var length = Math.Min(DisplayPrefixLength, keyMaterial.Length);
        return keyMaterial[..length];
    }

    private static byte[] HashKey(string keyMaterial) => SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
}

/// <summary>Redis-backed admin-key registry shared by all server instances.</summary>
internal sealed class RedisAdminApiKeyStore(IConnectionMultiplexer redis, TimeProvider? timeProvider = null) : IAdminApiKeyStore
{
    private const string Prefix = "honua:auth:admin-api-key:";
    private const string IdsKey = Prefix + "ids";
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<AdminApiKeyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = await _database.SetMembersAsync(IdsKey).ConfigureAwait(false);
        var values = ids.Length == 0 ? Array.Empty<RedisValue>() : await _database.StringGetAsync(ids.Select(id => (RedisKey)$"{Prefix}{id}").ToArray()).ConfigureAwait(false);
        return values.Select(Read).Where(static value => value is not null).Cast<AdminApiKeyRecord>().OrderBy(key => key.CreatedAt).ToArray();
    }

    public async Task<AdminApiKeyCreateResult> CreateAsync(string name, IReadOnlyList<string> permissions, DateTimeOffset? expiresAt, string? createdBy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var key = InMemoryAdminApiKeyStore.GenerateForDurableStore();
        var record = new AdminApiKeyRecord(Guid.NewGuid(), name, key[..Math.Min(12, key.Length)], SHA256.HashData(Encoding.UTF8.GetBytes(key)), permissions.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("admin:*").ToArray(), now, now, expiresAt, null, null, null, createdBy);
        await _database.StringSetAsync(BuildKey(record.Id), JsonSerializer.Serialize(record), ResolveTtl(record.ExpiresAt), When.NotExists).ConfigureAwait(false);
        await _database.SetAddAsync(IdsKey, record.Id.ToString("D")).ConfigureAwait(false);
        return new(record, key);
    }

    public async Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken) => await ReadAsync(id, cancellationToken).ConfigureAwait(false);

    public async Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.RevokedAt is not null) return null;
        var now = _timeProvider.GetUtcNow();
        var key = InMemoryAdminApiKeyStore.GenerateForDurableStore();
        var updated = existing with { KeyPrefix = key[..Math.Min(12, key.Length)], KeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key)), UpdatedAt = now, RotatedAt = now, LastUsedAt = null };
        await _database.StringSetAsync(BuildKey(id), JsonSerializer.Serialize(updated), ResolveTtl(updated.ExpiresAt)).ConfigureAwait(false);
        return new(updated, key);
    }

    public async Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null) return null;
        var updated = existing with { UpdatedAt = _timeProvider.GetUtcNow(), RevokedAt = existing.RevokedAt ?? _timeProvider.GetUtcNow() };
        await _database.StringSetAsync(BuildKey(id), JsonSerializer.Serialize(updated), ResolveTtl(updated.ExpiresAt)).ConfigureAwait(false);
        return updated;
    }

    public async Task<AdminApiKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken)
    {
        foreach (var record in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
            if (record.RevokedAt is null && (!record.ExpiresAt.HasValue || record.ExpiresAt > _timeProvider.GetUtcNow()) && CryptographicOperations.FixedTimeEquals(hash, record.KeyHash))
            {
                var updated = record with { LastUsedAt = _timeProvider.GetUtcNow(), UpdatedAt = _timeProvider.GetUtcNow() };
                await _database.StringSetAsync(BuildKey(record.Id), JsonSerializer.Serialize(updated), ResolveTtl(updated.ExpiresAt)).ConfigureAwait(false);
                return new(updated);
            }
        }
        return null;
    }

    private async Task<AdminApiKeyRecord?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Read(await _database.StringGetAsync(BuildKey(id)).ConfigureAwait(false));
    }

    private static AdminApiKeyRecord? Read(RedisValue value) => value.HasValue ? JsonSerializer.Deserialize<AdminApiKeyRecord>((string)value!) : null;
    private static string BuildKey(Guid id) => $"{Prefix}{id:D}";
    private static TimeSpan ResolveTtl(DateTimeOffset? expiresAt) => expiresAt is { } value && value > DateTimeOffset.UtcNow ? value - DateTimeOffset.UtcNow : TimeSpan.FromDays(3650);
}

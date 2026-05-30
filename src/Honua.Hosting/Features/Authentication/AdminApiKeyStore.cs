// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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

        _keys[id] = updated;
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
            _keys[record.Id] = updated;
            return Task.FromResult<AdminApiKeyValidationResult?>(new AdminApiKeyValidationResult(updated));
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

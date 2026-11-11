// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;

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

    // Restrictions are stored in the same write as the key so it never exists unrestricted; null or empty lists leave it unrestricted.
    Task<AdminApiKeyCreateResult> CreateAsync(
        string name,
        IReadOnlyList<string> permissions,
        DateTimeOffset? expiresAt,
        string? createdBy,
        AdminApiKeyRestrictions? restrictions,
        CancellationToken cancellationToken);

    Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken);

    Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken);

    // An approved-operation credential is not a managed key, so a restriction update returns null and leaves its material unchanged.
    Task<AdminApiKeyRecord?> UpdateRestrictionsAsync(
        Guid id,
        AdminApiKeyRestrictions restrictions,
        CancellationToken cancellationToken);

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
    string? CreatedBy,
    // Optional request restrictions (null = unrestricted). Omitted from JSON when null so a
    // record without restrictions serializes byte-for-byte as it did before they existed:
    // the Redis store's conditional usage write compares those bytes.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AllowedReferrers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AllowedIpRanges = null);

internal sealed record AdminApiKeyRestrictions(
    IReadOnlyList<string>? AllowedReferrers,
    IReadOnlyList<string>? AllowedIpRanges)
{
    internal static IReadOnlyList<string>? Normalize(IReadOnlyList<string>? values)
        => values is { Count: > 0 } ? values.ToArray() : null;
}

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
        => CreateAsync(name, permissions, expiresAt, createdBy, restrictions: null, cancellationToken);

    public Task<AdminApiKeyCreateResult> CreateAsync(
        string name,
        IReadOnlyList<string> permissions,
        DateTimeOffset? expiresAt,
        string? createdBy,
        AdminApiKeyRestrictions? restrictions,
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
            createdBy,
            AdminApiKeyRestrictions.Normalize(restrictions?.AllowedReferrers),
            AdminApiKeyRestrictions.Normalize(restrictions?.AllowedIpRanges));

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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = _timeProvider.GetUtcNow();
            // Rotation keeps the record's ExpiresAt, so rotating an expired key would hand back
            // material ValidateAsync immediately rejects. Report it as gone instead.
            if (!_keys.TryGetValue(id, out var existing) || existing.RevokedAt is not null ||
                existing.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant) ||
                (existing.ExpiresAt.HasValue && existing.ExpiresAt.Value <= now))
            {
                return Task.FromResult<AdminApiKeyCreateResult?>(null);
            }

            var generated = GenerateKeyMaterial();
            var updated = existing with
            {
                KeyPrefix = CreateDisplayPrefix(generated),
                KeyHash = HashKey(generated),
                UpdatedAt = now,
                RotatedAt = now,
                LastUsedAt = null,
            };

            // Replace only the snapshot checked above. An unconditional write would put an
            // unrevoked record back over a concurrent revocation.
            if (_keys.TryUpdate(id, updated, existing))
            {
                return Task.FromResult<AdminApiKeyCreateResult?>(new AdminApiKeyCreateResult(updated, generated));
            }
        }
    }

    public Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        while (true)
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

            // A concurrent usage or rotation write replaced the snapshot: revoke the current
            // record rather than report a revocation that was never stored.
            if (_keys.TryUpdate(id, updated, existing))
            {
                return Task.FromResult<AdminApiKeyRecord?>(updated);
            }
        }
    }

    public Task<AdminApiKeyRecord?> UpdateRestrictionsAsync(
        Guid id,
        AdminApiKeyRestrictions restrictions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restrictions);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_keys.TryGetValue(id, out var existing)
                || existing.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant))
            {
                return Task.FromResult<AdminApiKeyRecord?>(null);
            }

            var updated = existing with
            {
                UpdatedAt = _timeProvider.GetUtcNow(),
                AllowedReferrers = AdminApiKeyRestrictions.Normalize(restrictions.AllowedReferrers),
                AllowedIpRanges = AdminApiKeyRestrictions.Normalize(restrictions.AllowedIpRanges),
            };

            if (_keys.TryUpdate(id, updated, existing))
            {
                return Task.FromResult<AdminApiKeyRecord?>(updated);
            }
        }
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

        // codeql[cs/linq/missed-select]: retries a contended key update and returns the first winning record
        foreach (var candidate in _keys.Values)
        {
            var record = candidate;
            while (record is not null &&
                record.RevokedAt is null &&
                (!record.ExpiresAt.HasValue || record.ExpiresAt.Value > now) &&
                CryptographicOperations.FixedTimeEquals(providedHash, record.KeyHash))
            {
                var updated = record with
                {
                    LastUsedAt = now,
                    UpdatedAt = now,
                };
                if (_keys.TryUpdate(record.Id, updated, record))
                {
                    return Task.FromResult<AdminApiKeyValidationResult?>(new AdminApiKeyValidationResult(updated));
                }

                // A concurrent write won the update. Never return the stale snapshot: re-check
                // the current record, so a revoke or rotate still denies while a sibling
                // request's usage write does not.
                record = _keys.TryGetValue(record.Id, out var current) ? current : null;
            }
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

    // This stores caller-supplied key material so a test can present bytes that trip content heuristics; issuance does not use it.
    internal AdminApiKeyCreateResult RegisterKnownMaterial(
        string keyMaterial,
        string name,
        IReadOnlyList<string> permissions,
        DateTimeOffset? expiresAt,
        string? createdBy,
        AdminApiKeyRestrictions? restrictions = null)
    {
        if (string.IsNullOrWhiteSpace(keyMaterial))
        {
            throw new ArgumentException("Key material is required.", nameof(keyMaterial));
        }

        var now = _timeProvider.GetUtcNow();
        var record = new AdminApiKeyRecord(
            Guid.NewGuid(),
            name,
            CreateDisplayPrefix(keyMaterial),
            HashKey(keyMaterial),
            NormalizePermissions(permissions),
            now,
            now,
            expiresAt,
            LastUsedAt: null,
            RotatedAt: null,
            RevokedAt: null,
            createdBy,
            AdminApiKeyRestrictions.Normalize(restrictions?.AllowedReferrers),
            AdminApiKeyRestrictions.Normalize(restrictions?.AllowedIpRanges));

        if (!_keys.TryAdd(record.Id, record))
        {
            throw new InvalidOperationException("Generated duplicate admin API key identifier.");
        }

        return new AdminApiKeyCreateResult(record, keyMaterial);
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

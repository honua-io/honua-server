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

    // IdsKey is the metadata registry and deliberately retains expired records for
    // administrative reads. ActiveIdsKey is the far smaller authentication candidate set
    // so per-request validation does not MGET and deserialize years of expired metadata.
    private const string ActiveIdsKey = Prefix + "active-ids";

    // Every id ActiveIdsKey has ever classified, active or not. IdsKey minus this set is
    // exactly the ids no instance has indexed yet -- a rolling deploy where an old instance
    // (pre-dating this index) still writes IdsKey directly, a restored backup, or a record
    // written out-of-band. Diffing against it lets validation catch up on just those ids
    // instead of re-scanning the whole retained registry on every miss.
    private const string SeenIdsKey = Prefix + "seen-ids";
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
        await _database.StringSetAsync(BuildKey(record.Id), JsonSerializer.Serialize(record, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord), ResolveTtl(record), When.NotExists).ConfigureAwait(false);
        await _database.SetAddAsync(IdsKey, record.Id.ToString("D")).ConfigureAwait(false);
        await MarkActiveAndSeenAsync(record.Id).ConfigureAwait(false);
        return new(record, key);
    }

    public async Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken) => await ReadAsync(id, cancellationToken).ConfigureAwait(false);

    public async Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        // Retention keeps expired records readable here. Rotation preserves ExpiresAt, so
        // rotating one would return 200 with material ValidateAsync rejects outright.
        if (existing is null || !CanAuthenticate(existing, now) || existing.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant)) return null;
        var key = InMemoryAdminApiKeyStore.GenerateForDurableStore();
        var updated = existing with { KeyPrefix = key[..Math.Min(12, key.Length)], KeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key)), UpdatedAt = now, RotatedAt = now, LastUsedAt = null };
        await _database.StringSetAsync(BuildKey(id), JsonSerializer.Serialize(updated, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord), ResolveTtl(updated)).ConfigureAwait(false);
        // Repairs the index for a record indexed before it existed, or pruned by a racing scan.
        await MarkActiveAndSeenAsync(id).ConfigureAwait(false);
        return new(updated, key);
    }

    public async Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null) return null;
        var updated = existing with { UpdatedAt = _timeProvider.GetUtcNow(), RevokedAt = existing.RevokedAt ?? _timeProvider.GetUtcNow() };
        await _database.StringSetAsync(BuildKey(id), JsonSerializer.Serialize(updated, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord), ResolveTtl(updated)).ConfigureAwait(false);
        await _database.SetRemoveAsync(ActiveIdsKey, id.ToString("D")).ConfigureAwait(false);
        return updated;
    }

    public async Task<AdminApiKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));

        foreach (var record in await ListActiveAsync(cancellationToken).ConfigureAwait(false))
        {
            AdminApiKeyRecord? current = record;
            for (var attempt = 0; attempt < 3 && current is not null; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = _timeProvider.GetUtcNow();
                if (current.RevokedAt is not null || (current.ExpiresAt.HasValue && current.ExpiresAt <= now)
                    || !CryptographicOperations.FixedTimeEquals(hash, current.KeyHash))
                {
                    break;
                }

                var updated = current with { LastUsedAt = now, UpdatedAt = now };
                var key = BuildKey(current.Id);
                var transaction = _database.CreateTransaction();
                // Do not unconditionally rewrite the snapshot read by ListAsync: a concurrent
                // revoke or rotate must win, rather than being resurrected by validation.
                transaction.AddCondition(Condition.StringEqual(key, JsonSerializer.Serialize(current, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord)));
                _ = transaction.StringSetAsync(key, JsonSerializer.Serialize(updated, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord), ResolveTtl(updated));
                if (await transaction.ExecuteAsync().ConfigureAwait(false))
                {
                    return new(updated);
                }

                // Another valid request may only have updated LastUsedAt. Re-read and
                // revalidate authority before retrying so benign usage does not cause a 401.
                current = attempt < 2
                    ? await ReadAsync(record.Id, cancellationToken).ConfigureAwait(false)
                    : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the records that can still authenticate and prunes the rest from the active
    /// index. Expired metadata stays in <see cref="IdsKey"/> for administrative reads, so
    /// authentication cost tracks live keys rather than the retention window.
    /// </summary>
    private async Task<IReadOnlyList<AdminApiKeyRecord>> ListActiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ReconcileUnseenIdsAsync(cancellationToken).ConfigureAwait(false);

        var members = await _database.SetMembersAsync(ActiveIdsKey).ConfigureAwait(false);
        var ids = members.Where(static member => Guid.TryParse((string?)member, out _)).ToArray();
        if (ids.Length == 0) return [];

        var values = await _database.StringGetAsync(ids.Select(id => (RedisKey)$"{Prefix}{id}").ToArray()).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var active = new List<AdminApiKeyRecord>(values.Length);
        var stale = new List<RedisValue>();
        for (var index = 0; index < values.Length; index++)
        {
            var record = Read(values[index]);
            if (record is not null && CanAuthenticate(record, now))
            {
                active.Add(record);
            }
            else
            {
                // Evicted, revoked or expired: it can never authenticate again, because
                // RotateAsync refuses to revive it.
                stale.Add(ids[index]);
            }
        }

        if (stale.Count > 0)
        {
            await _database.SetRemoveAsync(ActiveIdsKey, [.. stale]).ConfigureAwait(false);
        }

        active.Sort(static (left, right) => left.CreatedAt.CompareTo(right.CreatedAt));
        return active;
    }

    /// <summary>
    /// Catches the active index up on any id in <see cref="IdsKey"/> it has never classified:
    /// a record from before this index existed, one written by an old instance mid-rollout, or
    /// one restored or written directly. The diff is cheap (server-side set arithmetic) and
    /// empty in the steady state, so this does not reintroduce a per-request full scan.
    /// </summary>
    private async Task ReconcileUnseenIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var unseen = await _database.SetCombineAsync(SetOperation.Difference, [IdsKey, SeenIdsKey]).ConfigureAwait(false);
        var ids = unseen.Where(static member => Guid.TryParse((string?)member, out _)).ToArray();
        if (ids.Length == 0) return;

        var keys = ids.Select(id => (RedisKey)$"{Prefix}{id}").ToArray();
        var values = await _database.StringGetAsync(keys).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var active = new List<RedisValue>(ids.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var record = Read(values[index]);
            if (record is not null && CanAuthenticate(record, now))
            {
                active.Add(ids[index]);
            }
        }

        var batch = _database.CreateBatch();
        var seenTask = batch.SetAddAsync(SeenIdsKey, ids);
        var activeTask = active.Count > 0 ? batch.SetAddAsync(ActiveIdsKey, [.. active]) : Task.CompletedTask;
        batch.Execute();
        await Task.WhenAll(seenTask, activeTask).ConfigureAwait(false);
    }

    private async Task MarkActiveAndSeenAsync(Guid id)
    {
        var member = (RedisValue)id.ToString("D");
        var batch = _database.CreateBatch();
        var activeTask = batch.SetAddAsync(ActiveIdsKey, member);
        var seenTask = batch.SetAddAsync(SeenIdsKey, member);
        batch.Execute();
        await Task.WhenAll(activeTask, seenTask).ConfigureAwait(false);
    }

    private static bool CanAuthenticate(AdminApiKeyRecord record, DateTimeOffset now) =>
        record.RevokedAt is null && (!record.ExpiresAt.HasValue || record.ExpiresAt.Value > now);

    private async Task<AdminApiKeyRecord?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Read(await _database.StringGetAsync(BuildKey(id)).ConfigureAwait(false));
    }

    private static AdminApiKeyRecord? Read(RedisValue value) => value.HasValue ? JsonSerializer.Deserialize((string)value!, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord) : null;
    private static string BuildKey(Guid id) => $"{Prefix}{id:D}";
    private TimeSpan ResolveTtl(AdminApiKeyRecord record)
    {
        var remaining = record.ExpiresAt - _timeProvider.GetUtcNow();
        if (remaining.HasValue && record.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant))
        {
            // Internal replay credentials remain short-lived, including writes racing expiry.
            return remaining.Value > TimeSpan.Zero ? remaining.Value : TimeSpan.FromMilliseconds(1);
        }

        // Credential validity is enforced by ValidateAsync. Keep managed-key metadata
        // for the existing registry retention period after expiry (or the latest write).
        var retention = TimeSpan.FromDays(3650);
        return remaining is { } lifetime && lifetime > TimeSpan.Zero ? lifetime + retention : retention;
    }
}

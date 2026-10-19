// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
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

    /// <summary>
    /// Stores caller-supplied key material. Tests use this to present a fixed credential
    /// whose bytes trip content heuristics (for example "--" or "/*"). Issuance stays on
    /// <see cref="CreateAsync"/>.
    /// </summary>
    internal AdminApiKeyCreateResult RegisterKnownMaterial(
        string keyMaterial,
        string name,
        IReadOnlyList<string> permissions,
        DateTimeOffset? expiresAt,
        string? createdBy)
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
            createdBy);

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

    // Ids whose hash index entry has been written. IdsKey minus this set is the backlog a
    // timer backfills; validation itself never scans it.
    private const string HashIndexedIdsKey = Prefix + "hash-indexed-ids";

    // Bounds the optimistic retries of rotate and revoke. Revocation withdraws the key from
    // authentication first, so only requests already in flight can still contend with it.
    private const int WriteAttempts = 16;

    // LastUsedAt is diagnostic. Rewriting it on every authenticated call is a write per request.
    private static readonly TimeSpan UsageWriteInterval = TimeSpan.FromMinutes(1);
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
        await PinApprovedExpiryAsync(_database, BuildKey(record.Id), record).ConfigureAwait(false);
        await WriteHashIndexAsync(record).ConfigureAwait(false);
        await _database.SetAddAsync(IdsKey, record.Id.ToString("D")).ConfigureAwait(false);
        await _database.SetAddAsync(HashIndexedIdsKey, record.Id.ToString("D")).ConfigureAwait(false);
        await MarkActiveAndSeenAsync(record.Id).ConfigureAwait(false);
        return new(record, key);
    }

    public async Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken) => await ReadAsync(id, cancellationToken).ConfigureAwait(false);

    public async Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _database.StringGetAsync(BuildKey(id)).ConfigureAwait(false);
            var existing = Read(snapshot);
            var now = _timeProvider.GetUtcNow();
            // Retention keeps expired records readable here. Rotation preserves ExpiresAt, so
            // rotating one would return 200 with material ValidateAsync rejects outright.
            if (existing is null || !CanAuthenticate(existing, now) || existing.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant)) return null;
            var key = InMemoryAdminApiKeyStore.GenerateForDurableStore();
            var updated = existing with { KeyPrefix = key[..Math.Min(12, key.Length)], KeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key)), UpdatedAt = now, RotatedAt = now, LastUsedAt = null };
            // Conditional on the snapshot just checked: an unconditional write would put an
            // unrevoked record back over a revocation that landed in between.
            if (!await TryReplaceAsync(id, snapshot, updated).ConfigureAwait(false)) continue;
            // Point the index at the new secret before dropping the old one, so a crash in
            // between fails closed on the retired secret instead of losing both.
            await WriteHashIndexAsync(updated).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(existing.KeyHash, updated.KeyHash))
            {
                await _database.KeyDeleteAsync(HashIndexKey(existing.KeyHash)).ConfigureAwait(false);
            }

            // Repairs the index for a record indexed before it existed, or pruned by a racing scan.
            await MarkActiveAndSeenAsync(id).ConfigureAwait(false);
            return new(updated, key);
        }

        throw new InvalidOperationException($"Admin API key {id:D} changed on every rotation attempt.");
    }

    public async Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        // Withdraw the key from the authentication candidates first, so a client that keeps using
        // it cannot keep winning usage writes and starve the conditional revocation below.
        await _database.SetRemoveAsync(ActiveIdsKey, id.ToString("D")).ConfigureAwait(false);
        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _database.StringGetAsync(BuildKey(id)).ConfigureAwait(false);
            var existing = Read(snapshot);
            if (existing is null) return null;
            var now = _timeProvider.GetUtcNow();
            var updated = existing with { UpdatedAt = now, RevokedAt = existing.RevokedAt ?? now };
            if (await TryReplaceAsync(id, snapshot, updated).ConfigureAwait(false))
            {
                // A rotation that won the race may have re-indexed the key after the removal above.
                await _database.KeyDeleteAsync(HashIndexKey(existing.KeyHash)).ConfigureAwait(false);
                await _database.SetRemoveAsync(ActiveIdsKey, id.ToString("D")).ConfigureAwait(false);
                return updated;
            }
        }

        throw new InvalidOperationException($"Admin API key {id:D} changed on every revocation attempt.");
    }

    /// <summary>Writes <paramref name="updated"/> only if the stored bytes still equal <paramref name="snapshot"/>.</summary>
    private async Task<bool> TryReplaceAsync(Guid id, RedisValue snapshot, AdminApiKeyRecord updated)
    {
        var key = BuildKey(id);
        var transaction = _database.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(key, snapshot));
        _ = transaction.StringSetAsync(key, JsonSerializer.Serialize(updated, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord), ResolveTtl(updated));
        _ = PinApprovedExpiryAsync(transaction, key, updated);
        return await transaction.ExecuteAsync().ConfigureAwait(false);
    }

    public async Task<AdminApiKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
        var indexedId = await _database.StringGetAsync(HashIndexKey(hash)).ConfigureAwait(false);
        if (!indexedId.HasValue || !Guid.TryParse((string?)indexedId, out var id))
        {
            return null;
        }

        var current = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 3 && current is not null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _timeProvider.GetUtcNow();
            if (current.RevokedAt is not null || (current.ExpiresAt.HasValue && current.ExpiresAt <= now)
                || !CryptographicOperations.FixedTimeEquals(hash, current.KeyHash))
            {
                break;
            }

            // A burst of requests for one key shares a single usage write per minute.
            if (current.LastUsedAt is { } lastUsed && now - lastUsed < UsageWriteInterval)
            {
                return new(current);
            }

            var updated = current with { LastUsedAt = now, UpdatedAt = now };
            var key = BuildKey(current.Id);
            var transaction = _database.CreateTransaction();
            // Do not unconditionally rewrite the snapshot read above: a concurrent
            // revoke or rotate must win, rather than being resurrected by validation.
            transaction.AddCondition(Condition.StringEqual(key, JsonSerializer.Serialize(current, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord)));
            _ = transaction.StringSetAsync(key, JsonSerializer.Serialize(updated, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord), ResolveTtl(updated));
            _ = PinApprovedExpiryAsync(transaction, key, updated);
            if (await transaction.ExecuteAsync().ConfigureAwait(false))
            {
                return new(updated);
            }

            // Another valid request may only have updated LastUsedAt. Re-read and
            // revalidate authority before retrying so benign usage does not cause a 401.
            var latest = await ReadAsync(id, cancellationToken).ConfigureAwait(false);
            if (attempt == 2 && latest is not null && IsUsageOnlyChange(current, latest))
            {
                // Out of retries, but the write that beat this one only recorded another request's
                // use of this key: its authority is exactly what was just checked, so a burst of
                // concurrent requests with one key must not turn into 401s.
                return new(latest);
            }

            current = attempt < 2 ? latest : null;
        }

        return null;
    }

    private static bool IsUsageOnlyChange(AdminApiKeyRecord compared, AdminApiKeyRecord latest) =>
        latest.LastUsedAt != compared.LastUsedAt &&
        latest.Id == compared.Id &&
        string.Equals(latest.Name, compared.Name, StringComparison.Ordinal) &&
        string.Equals(latest.KeyPrefix, compared.KeyPrefix, StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(latest.KeyHash, compared.KeyHash) &&
        latest.Permissions.SequenceEqual(compared.Permissions, StringComparer.Ordinal) &&
        latest.CreatedAt == compared.CreatedAt &&
        latest.ExpiresAt == compared.ExpiresAt &&
        latest.RotatedAt == compared.RotatedAt &&
        latest.RevokedAt == compared.RevokedAt &&
        string.Equals(latest.CreatedBy, compared.CreatedBy, StringComparison.Ordinal);

    /// <summary>
    /// Builds <c>hash → id</c> entries for registry ids no instance has indexed yet: a record
    /// written before this index existed, one written by an old instance mid-rollout, or one
    /// restored directly. Runs on a timer, never on the authentication path.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task ReconcileHashIndexAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var unseen = await _database.SetCombineAsync(SetOperation.Difference, [IdsKey, HashIndexedIdsKey]).ConfigureAwait(false);
        if (unseen is not { Length: > 0 })
        {
            return;
        }

        var ids = unseen.Where(static member => Guid.TryParse((string?)member, out _)).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var keys = ids.Select(id => (RedisKey)$"{Prefix}{id}").ToArray();
        var values = await _database.StringGetAsync(keys).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var active = new List<RedisValue>(ids.Length);
        var batch = _database.CreateBatch();
        var writes = new List<Task> { batch.SetAddAsync(HashIndexedIdsKey, ids), batch.SetAddAsync(SeenIdsKey, ids) };
        for (var index = 0; index < values.Length; index++)
        {
            var record = Read(values[index]);
            if (record is null || !CanAuthenticate(record, now))
            {
                continue;
            }

            active.Add(ids[index]);
            writes.Add(batch.StringSetAsync(HashIndexKey(record.KeyHash), record.Id.ToString("D"), ResolveTtl(record)));
            writes.Add(PinApprovedExpiryAsync(batch, HashIndexKey(record.KeyHash), record));
        }

        if (active.Count > 0)
        {
            writes.Add(batch.SetAddAsync(ActiveIdsKey, [.. active]));
        }

        batch.Execute();
        await Task.WhenAll(writes).ConfigureAwait(false);
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

    /// <summary>Redis key for <c>honua:auth:admin-api-key:hash:{sha256}</c> → key id.</summary>
    /// <param name="hash">SHA-256 of the presented or stored key material.</param>
    internal static RedisKey HashIndexKey(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return Prefix + "hash:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task WriteHashIndexAsync(AdminApiKeyRecord record)
    {
        var indexKey = HashIndexKey(record.KeyHash);
        await _database.StringSetAsync(indexKey, record.Id.ToString("D"), ResolveTtl(record)).ConfigureAwait(false);
        await PinApprovedExpiryAsync(_database, indexKey, record).ConfigureAwait(false);
    }

    // ResolveTtl's relative TTL is measured before the write reaches Redis, so a write delayed by
    // load or contention would keep a replay credential past ExpiresAt by that delay. PEXPIREAT
    // anchors its eviction to ExpiresAt itself (and evicts at once when the write lands late).
    private static Task PinApprovedExpiryAsync(IDatabaseAsync database, RedisKey key, AdminApiKeyRecord record)
    {
        if (record.ExpiresAt is not { } expiresAt || !record.Permissions.Any(AdminApiKeyPermission.IsApprovedOperationGrant))
        {
            return Task.CompletedTask;
        }

        return database.KeyExpireAsync(key, expiresAt.UtcDateTime);
    }

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

/// <summary>
/// Backfills the admin API-key hash index off the request path. Authentication reads one
/// hash entry; this loop is what catches records written before that index existed.
/// </summary>
internal sealed class RedisAdminApiKeyIndexMaintenance : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly IAdminApiKeyStore _store;

    /// <summary>
    /// Creates the maintenance loop.
    /// </summary>
    /// <param name="store">The registered admin API-key store.</param>
    public RedisAdminApiKeyIndexMaintenance(IAdminApiKeyStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_store is not RedisAdminApiKeyStore redis)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await redis.ReconcileHashIndexAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RedisException)
            {
                // The next tick retries. A registry backfill must not take the host down.
            }
            catch (TimeoutException)
            {
                // Redis command timeouts are TimeoutException, not RedisException. The next tick retries.
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

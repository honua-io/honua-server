// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Server-side at-most-once store for FeatureServer <c>applyEdits</c> requests (#2250). A client that
/// retries an edit (transient failure, re-sync) supplies a stable <c>Idempotency-Key</c>; the first
/// completed request's response is recorded keyed by that key so a replayed request returns the original
/// result (the original objectIds) without re-applying the edit and creating duplicate features.
///
/// The distributed path uses Redis SET NX for an atomic reservation so concurrent requests carrying the
/// same key race on the reserve; the loser returns 409 instead of both executing and committing duplicate
/// rows (BH5-001). The in-process fallback uses <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>
/// which is natively atomic within a single process.
/// </summary>
internal interface IApplyEditsIdempotencyStore
{
    /// <summary>
    /// Returns the previously-recorded response for an idempotency key within the dedupe window, or
    /// <see langword="null"/> when this is the first time the key has been seen (or when a concurrent
    /// request holds a pending reservation for the same key).
    /// </summary>
    Task<ApplyEditsResponse?> TryGetAsync(
        ApplyEditsIdempotencyScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reserves the idempotency key so only one concurrent request can proceed to
    /// execute the edit. Returns <see langword="true"/> when the reservation is won (this request
    /// is the sole owner); returns <see langword="false"/> when another in-flight request already
    /// holds the reservation (the caller should return 409 Conflict). Once the edit completes,
    /// call <see cref="SetAsync"/> to replace the reservation with the final response.
    /// </summary>
    Task<bool> TryReserveAsync(
        ApplyEditsIdempotencyScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the response for an idempotency key so a later retry replays it instead of re-applying
    /// the edit. Best-effort: a store failure is swallowed so it can never fail an already-applied edit
    /// (the only consequence is the retry is re-applied rather than deduped).
    /// </summary>
    Task SetAsync(
        ApplyEditsIdempotencyScope scope,
        ApplyEditsResponse response,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a reservation taken by <see cref="TryReserveAsync"/> that will never be replaced by a
    /// recorded response (#3052) — the edit threw, was rejected, rolled back, or committed no rows. Without
    /// this the pending sentinel lingers for the whole reservation window and a genuine client retry gets
    /// 409 Conflict instead of a deterministic repeat of the original outcome.
    ///
    /// Release is a compare-and-delete on the pending sentinel: a key that already holds a recorded
    /// response is left untouched, so a late release can never discard another request's committed replay
    /// value and re-open the door to a duplicate edit. Best-effort — the store swallows failures because
    /// this runs on an already-failing path and must never mask the original error.
    ///
    /// Deliberately takes no <see cref="CancellationToken"/>: the release must still run when the
    /// request that owns the reservation was cancelled or timed out — those are exactly the failures
    /// that would otherwise pin the key — so there is no token a caller could correctly pass.
    /// </summary>
    Task ReleaseAsync(ApplyEditsIdempotencyScope scope);
}

/// <summary>
/// Identifies a single idempotent edit. The key is scoped to the principal, service, and layer so one
/// caller's key can never replay another caller's response, and the same key on different layers does
/// not collide.
/// </summary>
/// <param name="ServiceId">The service the edit targets.</param>
/// <param name="LayerId">The layer the edit targets.</param>
/// <param name="Principal">The authenticated principal name, or <c>anonymous</c> when unauthenticated.</param>
/// <param name="IdempotencyKey">The validated client-supplied idempotency key.</param>
internal readonly record struct ApplyEditsIdempotencyScope(
    string ServiceId,
    int LayerId,
    string Principal,
    string IdempotencyKey);

/// <summary>
/// Distributed (Redis-backed) at-most-once store for applyEdits with an in-process fallback when no
/// <see cref="IDistributedCache"/> is configured, mirroring <see cref="DistributedReplicaStore"/>. The
/// distributed path uses Redis SET NX for atomic reservation (BH5-001): concurrent requests carrying the
/// same key race on the reserve; the loser returns 409. The in-process fallback uses
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> which is natively atomic within a process.
/// </summary>
internal sealed class DistributedApplyEditsIdempotencyStore : IApplyEditsIdempotencyStore
{
    private const string KeyPrefix = "featureserver:applyedits:idem:";
    private const int MaxFallbackEntries = 10_000;

    /// <summary>
    /// Pending sentinel value stored during the reservation window. Not valid UTF-8 JSON so
    /// <see cref="Deserialize"/> returns <see langword="null"/> when it is read back.
    /// </summary>
    private static readonly byte[] PendingSentinel = [0xFF];

    /// <summary>
    /// Default dedupe window. A retry within this window of the original request replays the stored
    /// response; after it the key is forgotten and a re-submission is treated as a fresh edit.
    /// </summary>
    internal static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Reservation window: how long a pending sentinel is kept before it expires if the owning
    /// request never completes. Generous to accommodate slow edits; the in-flight request
    /// replaces the sentinel with the real response via <see cref="SetAsync"/>.
    /// </summary>
    internal static readonly TimeSpan ReservationWindow = TimeSpan.FromSeconds(60);

    private readonly IDatabase? _redisDatabase;
    private readonly IDistributedCache? _cache;
    private readonly ILogger<DistributedApplyEditsIdempotencyStore> _logger;
    private readonly ConcurrentDictionary<string, FallbackEntry> _fallback = new(StringComparer.Ordinal);

    public DistributedApplyEditsIdempotencyStore(
        IConnectionMultiplexer? multiplexer,
        IDistributedCache? cache,
        ILogger<DistributedApplyEditsIdempotencyStore> logger)
    {
        _redisDatabase = multiplexer?.GetDatabase();
        _cache = cache;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Backward-compatible overload for callers that do not yet supply an IConnectionMultiplexer.
    public DistributedApplyEditsIdempotencyStore(
        IDistributedCache? cache,
        ILogger<DistributedApplyEditsIdempotencyStore> logger)
        : this(multiplexer: null, cache, logger)
    {
    }

    public async Task<ApplyEditsResponse?> TryGetAsync(
        ApplyEditsIdempotencyScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = BuildKey(scope);
        var now = DateTimeOffset.UtcNow;

        if (_redisDatabase != null)
        {
            try
            {
                var payload = await _redisDatabase.StringGetAsync(key).ConfigureAwait(false);
                if (!payload.HasValue) return null;
                var bytes = (byte[])payload!;
                if (IsPendingSentinel(bytes)) return null; // another request is in-flight
                return Deserialize(bytes);
            }
            // Intentionally generic: Redis can throw a wide range of transport/timeout/auth
            // exceptions here; best-effort — a lookup failure simply means the retry is
            // re-applied rather than deduped.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, ex);
                return null;
            }
        }

        if (_cache == null)
        {
            if (_fallback.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
            {
                if (IsPendingSentinel(entry.Payload))
                    return null; // pending sentinel - another request is in-flight
                return Deserialize(entry.Payload);
            }

            return null;
        }

        try
        {
            var payload = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (payload is null) return null;
            if (IsPendingSentinel(payload)) return null; // another request is in-flight
            return Deserialize(payload);
        }
        // Intentionally generic: the configured IDistributedCache implementation can throw a
        // wide range of provider-specific exceptions; best-effort — a lookup failure simply
        // means the retry is re-applied rather than deduped.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, ex);
            return null;
        }
    }

    public async Task<bool> TryReserveAsync(
        ApplyEditsIdempotencyScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = BuildKey(scope);
        var now = DateTimeOffset.UtcNow;

        if (_redisDatabase != null)
        {
            try
            {
                // Redis SET NX: atomic set-if-absent. Returns true when the key did not exist
                // (reservation won), false when another request already holds the key.
                return await _redisDatabase.StringSetAsync(
                    key,
                    PendingSentinel,
                    ReservationWindow,
                    when: When.NotExists).ConfigureAwait(false);
            }
            // Intentionally generic: Redis can throw a wide range of transport/timeout/auth
            // exceptions here; fail-open rather than blocking the edit request.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, ex);
                return true; // fail-open: let the request proceed; worst case is a duplicate on Redis failure
            }
        }

        // In-process fallback: ConcurrentDictionary.TryAdd is atomic — only one concurrent
        // caller wins; the loser sees false and should return 409.
        // This covers both the no-cache path (_cache == null) and the non-Redis
        // IDistributedCache path: a MemoryDistributedCache / SQL-session-store / Memcached
        // cache has no set-if-absent primitive, so treating it the same as no-cache
        // gives us a single process-level mutex via ConcurrentDictionary. (BH7-002)
        return _fallback.TryAdd(key, new FallbackEntry(PendingSentinel, now.Add(ReservationWindow)));
    }

    public async Task SetAsync(
        ApplyEditsIdempotencyScope scope,
        ApplyEditsResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildKey(scope);
        var payload = Serialize(response);
        var now = DateTimeOffset.UtcNow;

        if (_redisDatabase != null)
        {
            try
            {
                await _redisDatabase.StringSetAsync(key, payload, DedupeWindow).ConfigureAwait(false);
            }
            // Intentionally generic: Redis can throw a wide range of transport/timeout/auth
            // exceptions here; best-effort — failing to record the response must not fail an
            // already-applied edit.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, ex);
            }
            return;
        }

        if (_cache == null)
        {
            // Assignment replaces any existing entry including the pending sentinel.
            _fallback[key] = new FallbackEntry(payload, now.Add(DedupeWindow));
            CleanupFallback(now);
            return;
        }

        try
        {
            await _cache.SetAsync(key, payload, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = DedupeWindow
            }, cancellationToken).ConfigureAwait(false);
        }
        // Intentionally generic: the configured IDistributedCache implementation can throw a
        // wide range of provider-specific exceptions; best-effort — failing to record the
        // response must not fail an already-applied edit.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, ex);
        }

        // TryReserveAsync uses _fallback for the non-Redis IDistributedCache path (BH7-002).
        // Replace the pending reservation sentinel with the committed response so it does not
        // linger for the full ReservationWindow, and trigger CleanupFallback to bound growth.
        _fallback[key] = new FallbackEntry(payload, now.Add(DedupeWindow));
        CleanupFallback(now);
    }

    public async Task ReleaseAsync(ApplyEditsIdempotencyScope scope)
    {
        var key = BuildKey(scope);

        if (_redisDatabase != null)
        {
            try
            {
                // Atomic compare-and-delete: only remove the key while it still holds THIS
                // reservation's pending sentinel. A key that already carries a recorded response
                // (a concurrent owner that completed after this request's reservation lapsed)
                // must survive, otherwise releasing would re-open the duplicate-edit window the
                // reservation exists to close.
                await _redisDatabase.ScriptEvaluateAsync(
                    ReleaseIfPendingScript,
                    [key],
                    [PendingSentinel]).ConfigureAwait(false);
            }
            // Intentionally generic: Redis can throw a wide range of transport/timeout/auth
            // exceptions here; best-effort — a failed release only means the reservation lingers
            // until it expires, which is the pre-fix behavior, and must never mask the original
            // failure that triggered the release.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, ex);
            }

            return;
        }

        // Non-Redis paths: TryReserveAsync always writes the pending sentinel to the in-process
        // fallback dictionary (BH7-002), never to IDistributedCache, so the reservation is released
        // from the fallback only. Recorded responses (which SetAsync writes to both the cache and
        // the fallback) are left in place by the sentinel check.
        //
        // The KeyValuePair overload of TryRemove is the in-process equivalent of the Redis script
        // above: it removes the entry only while it is still byte-for-byte the one just read, so a
        // SetAsync landing between the read and the remove cannot have its recorded response
        // deleted by this release.
        if (_fallback.TryGetValue(key, out var entry) && IsPendingSentinel(entry.Payload))
        {
            _fallback.TryRemove(new KeyValuePair<string, FallbackEntry>(key, entry));
        }
    }

    /// <summary>
    /// Compare-and-delete: removes the reservation key only while it still holds the pending
    /// sentinel supplied as <c>ARGV[1]</c>, leaving a recorded response untouched. Runs
    /// server-side so the read and the delete cannot interleave with another request.
    /// </summary>
    private const string ReleaseIfPendingScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end return 0";

    private static bool IsPendingSentinel(byte[] payload)
        => payload.Length == 1 && payload[0] == 0xFF;

    private static string BuildKey(ApplyEditsIdempotencyScope scope)
    {
        // Hash the principal to prevent colon-based key collisions: a principal name
        // containing ":" would allow crafted names to collide with other key segments.
        // SHA256.HashData is AOT-compatible and allocation-efficient in .NET 6+.
        var principalBytes = System.Text.Encoding.UTF8.GetBytes(scope.Principal);
        var hash = System.Security.Cryptography.SHA256.HashData(principalBytes);
        var principalHash = Convert.ToHexString(hash);
        return string.Concat(
            KeyPrefix,
            scope.ServiceId,
            ":",
            scope.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ":",
            principalHash,
            ":",
            scope.IdempotencyKey);
    }

    private static byte[] Serialize(ApplyEditsResponse response)
        => JsonSerializer.SerializeToUtf8Bytes(response, FeatureServerJsonContext.Default.ApplyEditsResponse);

    private static ApplyEditsResponse? Deserialize(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, FeatureServerJsonContext.Default.ApplyEditsResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void CleanupFallback(DateTimeOffset now)
    {
        foreach (var pair in _fallback.Where(p => p.Value.ExpiresAt <= now))
        {
            _fallback.TryRemove(pair.Key, out _);
        }

        if (_fallback.Count <= MaxFallbackEntries)
        {
            return;
        }

        // Bound memory growth on the no-cache path by evicting the soonest-to-expire entries.
        foreach (var pair in _fallback.OrderBy(static p => p.Value.ExpiresAt).Take(_fallback.Count - MaxFallbackEntries))
        {
            _fallback.TryRemove(pair.Key, out _);
        }
    }

    private readonly record struct FallbackEntry(byte[] Payload, DateTimeOffset ExpiresAt);
}

/// <summary>
/// Reads and validates the <c>Idempotency-Key</c> request header for FeatureServer edits.
/// </summary>
internal static class ApplyEditsIdempotency
{
    /// <summary>
    /// HTTP request header carrying the client-supplied at-most-once key.
    /// </summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>
    /// Maximum accepted key length. Bounds cache-key size and rejects abusive payloads.
    /// </summary>
    public const int MaxKeyLength = 200;

    /// <summary>
    /// Reads the <c>Idempotency-Key</c> header and returns the trimmed key when present and valid.
    /// Returns <see langword="null"/> when the header is absent. Returns <see langword="false"/> via
    /// <paramref name="error"/> when the header is present but malformed (empty, too long, or contains
    /// control characters) so the caller can reject it with a 400 rather than silently ignoring it.
    /// </summary>
    public static bool TryResolveKey(HttpContext httpContext, out string? key, out string? error)
    {
        key = null;
        error = null;

        if (!httpContext.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return true;
        }

        var raw = values.Count > 0 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{HeaderName} header must not be empty.";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxKeyLength)
        {
            error = $"{HeaderName} header must be at most {MaxKeyLength} characters.";
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = $"{HeaderName} header must not contain control characters.";
            return false;
        }

        key = trimmed;
        return true;
    }
}

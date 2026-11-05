// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

// Both at-most-once stores share this reservation mechanic: concurrent same-key requests race on set-if-absent, the loser gets no token, and a store failure is reported without failing the request.
internal sealed class IdempotencyPayloadStore
{
    private const int MaxFallbackEntries = 10_000;

    // The pending payload starts with 0xFF so it is not valid UTF-8 JSON and can never be read back as a recorded response.
    private const byte PendingPrefix = 0xFF;

    internal static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(24);

    internal static readonly TimeSpan ReservationWindow = TimeSpan.FromSeconds(60);

    // Compare-and-delete removes the key only while it still holds this reservation payload, so a recorded response or another request's reservation is left in place.
    private const string ReleaseIfOwnedScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end return 0";

    // The record is written only when the key is empty or still holds this request's reservation, so an expired owner cannot overwrite a newer reservation or receipt.
    private const string SetIfAbsentOrOwnedScript =
        "local current = redis.call('GET', KEYS[1]) " +
        "if (not current) or current == ARGV[1] then redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3]) return 1 end return 0";

    private readonly IDatabase? _redisDatabase;
    private readonly IDistributedCache? _cache;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, FallbackEntry> _fallback = new(StringComparer.Ordinal);

    public IdempotencyPayloadStore(IConnectionMultiplexer? multiplexer, IDistributedCache? cache, TimeProvider? timeProvider = null)
    {
        _redisDatabase = multiplexer?.GetDatabase();
        _cache = cache;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<byte[]?> TryGetAsync(
        string key,
        Action<Exception> onStoreUnavailable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();

        if (_redisDatabase != null)
        {
            try
            {
                var payload = await _redisDatabase.StringGetAsync(key).ConfigureAwait(false);
                if (!payload.HasValue) return null;
                var bytes = (byte[])payload!;
                return IsPendingReservation(bytes) ? null : bytes; // pending: another request is in-flight
            }
            // Intentionally generic: Redis can throw a wide range of transport/timeout/auth
            // exceptions here; best-effort — a lookup failure simply means the retry is
            // re-applied rather than deduped.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onStoreUnavailable(ex);
                return null;
            }
        }

        if (_cache == null)
        {
            if (_fallback.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
            {
                return IsPendingReservation(entry.Payload) ? null : entry.Payload;
            }

            return null;
        }

        try
        {
            var payload = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (payload is null) return null;
            return IsPendingReservation(payload) ? null : payload; // pending: another request is in-flight
        }
        // Intentionally generic: the configured IDistributedCache implementation can throw a
        // wide range of provider-specific exceptions; best-effort — a lookup failure simply
        // means the retry is re-applied rather than deduped.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            onStoreUnavailable(ex);
            return null;
        }
    }

    public Task<string?> TryReserveAsync(
        string key,
        Action<Exception> onStoreUnavailable,
        CancellationToken cancellationToken)
        => TryReserveAsync(key, ReservationWindow, onStoreUnavailable, cancellationToken);

    public async Task<string?> TryReserveAsync(
        string key,
        TimeSpan reservationWindow,
        Action<Exception> onStoreUnavailable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();

        // Unique per reservation (#3052). Storing a shared sentinel made two owners of the same
        // key indistinguishable, so a request whose reservation had already lapsed could delete a
        // retry's live reservation and let a third request execute alongside it.
        var token = Guid.NewGuid().ToString("N");
        var payload = BuildReservationPayload(token);

        if (_redisDatabase != null)
        {
            try
            {
                // Redis SET NX: atomic set-if-absent. Returns true when the key did not exist
                // (reservation won), false when another request already holds the key.
                var won = await _redisDatabase.StringSetAsync(
                    key,
                    payload,
                    reservationWindow,
                    when: When.NotExists).ConfigureAwait(false);
                return won ? token : null;
            }
            // Intentionally generic: Redis can throw a wide range of transport/timeout/auth
            // exceptions here; fail-open rather than blocking the request.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onStoreUnavailable(ex);
                // Fail-open: let the request proceed; worst case is a duplicate on Redis failure.
                // The token is still returned so the caller's release path is uniform — releasing a
                // key that was never written is a no-op.
                return token;
            }
        }

        // An entry past its expiry is dead: TryGetAsync already ignores it, and the periodic sweep only
        // runs when something is recorded, so on a quiet node it would otherwise block this key forever.
        // The KeyValuePair overload removes it only while it is still that same expired entry.
        if (_fallback.TryGetValue(key, out var existing) && existing.ExpiresAt <= now)
        {
            _fallback.TryRemove(new KeyValuePair<string, FallbackEntry>(key, existing));
        }

        // In-process fallback: ConcurrentDictionary.TryAdd is atomic — only one concurrent
        // caller wins; the loser gets null.
        // This covers both the no-cache path (_cache == null) and the non-Redis
        // IDistributedCache path: a MemoryDistributedCache / SQL-session-store / Memcached
        // cache has no set-if-absent primitive, so treating it the same as no-cache
        // gives us a single process-level mutex via ConcurrentDictionary. (BH7-002)
        return _fallback.TryAdd(key, new FallbackEntry(payload, now.Add(reservationWindow)))
            ? token
            : null;
    }

    public async Task SetAsync(
        string key,
        byte[] payload,
        Action<Exception> onStoreUnavailable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();

        if (_redisDatabase != null)
        {
            try
            {
                await _redisDatabase.StringSetAsync(key, payload, DedupeWindow).ConfigureAwait(false);
            }
            // Intentionally generic: Redis can throw a wide range of transport/timeout/auth
            // exceptions here; best-effort — failing to record must not fail an already-applied edit.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onStoreUnavailable(ex);
            }
            return;
        }

        if (_cache == null)
        {
            // Assignment replaces any existing entry including a held reservation.
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
        // wide range of provider-specific exceptions; best-effort — failing to record must not
        // fail an already-applied edit.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            onStoreUnavailable(ex);
        }

        // TryReserveAsync uses _fallback for the non-Redis IDistributedCache path (BH7-002).
        // Replace the held reservation with the committed record so it does not
        // linger for the full ReservationWindow, and trigger CleanupFallback to bound growth.
        _fallback[key] = new FallbackEntry(payload, now.Add(DedupeWindow));
        CleanupFallback(now);
    }

    public async Task<bool> SetIfAbsentOrOwnedAsync(
        string key,
        string reservationToken,
        byte[] payload,
        Action<Exception> onStoreUnavailable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(reservationToken);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var ownedPayload = BuildReservationPayload(reservationToken);
        var now = _time.GetUtcNow();

        if (_redisDatabase != null)
        {
            try
            {
                var written = await _redisDatabase.ScriptEvaluateAsync(
                    SetIfAbsentOrOwnedScript,
                    [key],
                    [ownedPayload, payload, (long)DedupeWindow.TotalMilliseconds]).ConfigureAwait(false);
                return (long)written == 1;
            }
            // Intentionally generic: Redis can throw a wide range of transport/timeout/auth
            // exceptions here; best-effort — failing to record must not fail an already-applied edit.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onStoreUnavailable(ex);
                return false;
            }
        }

        // Non-Redis paths keep reservations in the fallback dictionary (BH7-002), so ownership is decided
        // there before anything is written to the cache.
        if (!TrySetFallbackIfAbsentOrOwned(key, ownedPayload, new FallbackEntry(payload, now.Add(DedupeWindow)), now))
        {
            return false;
        }

        if (_cache != null)
        {
            try
            {
                await _cache.SetAsync(key, payload, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = DedupeWindow
                }, cancellationToken).ConfigureAwait(false);
            }
            // Intentionally generic: the configured IDistributedCache implementation can throw a
            // wide range of provider-specific exceptions; best-effort.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onStoreUnavailable(ex);
            }
        }

        CleanupFallback(now);
        return true;
    }

    // Release compares and deletes this reservation and takes no cancellation token, so a cancelled owner can still free its key.
    public async Task ReleaseAsync(string key, string reservationToken, Action<Exception> onStoreUnavailable)
    {
        ArgumentException.ThrowIfNullOrEmpty(reservationToken);

        var ownedPayload = BuildReservationPayload(reservationToken);

        if (_redisDatabase != null)
        {
            try
            {
                // Atomic compare-and-delete: only remove the key while it still holds THIS
                // reservation's token. Two other states must survive — a recorded response, and a
                // reservation belonging to a different request because this one's window lapsed and
                // a retry re-reserved the key. Deleting either would re-open the duplicate-edit
                // window the reservation exists to close.
                await _redisDatabase.ScriptEvaluateAsync(
                    ReleaseIfOwnedScript,
                    [key],
                    [ownedPayload]).ConfigureAwait(false);
            }
            // Intentionally generic: Redis can throw a wide range of transport/timeout/auth
            // exceptions here; best-effort — a failed release only means the reservation lingers
            // until it expires, and must never mask the original failure that triggered the release.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onStoreUnavailable(ex);
            }

            return;
        }

        // Non-Redis paths: TryReserveAsync always writes the reservation to the in-process fallback
        // dictionary (BH7-002), never to IDistributedCache, so the reservation is released from the
        // fallback only. Recorded payloads (which SetAsync writes to both the cache and the
        // fallback) are left in place by the token check.
        //
        // The KeyValuePair overload of TryRemove is the in-process equivalent of the Redis script
        // above: it removes the entry only while it is still exactly the one just read, so a
        // SetAsync or a fresh reservation landing between the read and the remove cannot be
        // deleted by this release.
        if (_fallback.TryGetValue(key, out var entry) && entry.Payload.AsSpan().SequenceEqual(ownedPayload))
        {
            _fallback.TryRemove(new KeyValuePair<string, FallbackEntry>(key, entry));
        }
    }

    // Each in-process step is an atomic dictionary update conditioned on the entry just read, so a concurrent reservation or record is not overwritten.
    private bool TrySetFallbackIfAbsentOrOwned(string key, byte[] ownedPayload, FallbackEntry record, DateTimeOffset now)
    {
        while (true)
        {
            if (!_fallback.TryGetValue(key, out var current))
            {
                if (_fallback.TryAdd(key, record))
                {
                    return true;
                }

                continue;
            }

            if (current.ExpiresAt > now && !current.Payload.AsSpan().SequenceEqual(ownedPayload))
            {
                return false;
            }

            if (_fallback.TryUpdate(key, record, current))
            {
                return true;
            }
        }
    }

    private static byte[] BuildReservationPayload(string reservationToken)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(reservationToken);
        var payload = new byte[tokenBytes.Length + 1];
        payload[0] = PendingPrefix;
        tokenBytes.CopyTo(payload, 1);
        return payload;
    }

    private static bool IsPendingReservation(byte[] payload)
        => payload.Length >= 1 && payload[0] == PendingPrefix;

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

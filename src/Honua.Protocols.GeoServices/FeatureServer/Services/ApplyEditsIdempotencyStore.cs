// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Server-side at-most-once store for FeatureServer <c>applyEdits</c> requests (#2250). A client that
/// retries an edit (transient failure, re-sync) supplies a stable <c>Idempotency-Key</c>; the first
/// completed request's response is recorded keyed by that key so a replayed request returns the original
/// result (the original objectIds) without re-applying the edit and creating duplicate features.
/// </summary>
internal interface IApplyEditsIdempotencyStore
{
    /// <summary>
    /// Returns the previously-recorded response for an idempotency key within the dedupe window, or
    /// <see langword="null"/> when this is the first time the key has been seen.
    /// </summary>
    Task<ApplyEditsResponse?> TryGetAsync(
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
/// distributed path is a plain keyed write with a dedupe-window TTL: it gives at-most-once for the common
/// retry pattern (a retry that arrives after the first request completed replays the stored response).
/// Two truly-concurrent identical requests can both miss the window before either records its response;
/// closing that race needs an atomic reserve, which <see cref="IDistributedCache"/> does not expose, so it
/// is intentionally out of scope for this slice.
/// </summary>
internal sealed class DistributedApplyEditsIdempotencyStore : IApplyEditsIdempotencyStore
{
    private const string KeyPrefix = "featureserver:applyedits:idem:";
    private const int MaxFallbackEntries = 10_000;

    /// <summary>
    /// Default dedupe window. A retry within this window of the original request replays the stored
    /// response; after it the key is forgotten and a re-submission is treated as a fresh edit.
    /// </summary>
    internal static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(24);

    private readonly IDistributedCache? _cache;
    private readonly ILogger<DistributedApplyEditsIdempotencyStore> _logger;
    private readonly ConcurrentDictionary<string, FallbackEntry> _fallback = new(StringComparer.Ordinal);

    public DistributedApplyEditsIdempotencyStore(
        IDistributedCache? cache,
        ILogger<DistributedApplyEditsIdempotencyStore> logger)
    {
        _cache = cache;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ApplyEditsResponse?> TryGetAsync(
        ApplyEditsIdempotencyScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = BuildKey(scope);
        var now = DateTimeOffset.UtcNow;

        if (_cache == null)
        {
            if (_fallback.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
            {
                return Deserialize(entry.Payload);
            }

            return null;
        }

        try
        {
            var payload = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
            return payload is null ? null : Deserialize(payload);
        }
        catch (Exception ex)
        {
            // Best-effort: a lookup failure simply means the retry is re-applied rather than deduped.
            FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, ex);
            return null;
        }
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

        if (_cache == null)
        {
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
        catch (Exception ex)
        {
            // Best-effort: failing to record the response must not fail an already-applied edit.
            FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, ex);
        }
    }

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
        foreach (var pair in _fallback)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _fallback.TryRemove(pair.Key, out _);
            }
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

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                error = $"{HeaderName} header must not contain control characters.";
                return false;
            }
        }

        key = trimmed;
        return true;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
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
    /// execute the edit. Returns a non-<see langword="null"/> ownership token when the reservation
    /// is won (this request is the sole owner); returns <see langword="null"/> when another
    /// in-flight request already holds the reservation (the caller should return 409 Conflict).
    /// Once the edit completes, call <see cref="SetAsync"/> to replace the reservation with the
    /// final response.
    ///
    /// The token is unique per reservation, not a shared sentinel: it is the value
    /// <see cref="ReleaseAsync"/> compares against, so a request whose reservation already lapsed
    /// (the window expired and a retry re-reserved the key) can never delete the reservation that
    /// now belongs to someone else and let a third request execute concurrently.
    /// </summary>
    Task<string?> TryReserveAsync(
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
    /// recorded response (#3052) — the edit failed before dispatch, was rejected, rolled back, or
    /// committed no rows. Ambiguous failures after dispatch are not released because rows may have
    /// committed. Without release on known no-write paths, the reservation lingers for the whole
    /// reservation window and a genuine client retry gets 409 Conflict instead of a deterministic
    /// repeat of the original outcome.
    ///
    /// Release is a compare-and-delete against <paramref name="reservationToken"/>, the exact token this
    /// caller was handed. Anything else — a recorded response, or a reservation belonging to a different
    /// request because this one's window lapsed — is left untouched, so a late release can never discard
    /// another owner's state and re-open the door to a duplicate edit. Best-effort — the store swallows
    /// failures because this runs on an already-failing path and must never mask the original error.
    ///
    /// Deliberately takes no <see cref="CancellationToken"/>: the release must still run when the
    /// request that owns the reservation was cancelled or timed out before write dispatch — one of
    /// the failures that would otherwise pin the key — so there is no token a caller could correctly
    /// pass.
    /// </summary>
    /// <param name="scope">The scope whose reservation is being released.</param>
    /// <param name="reservationToken">The token returned by the <see cref="TryReserveAsync"/> call that won this reservation.</param>
    Task ReleaseAsync(ApplyEditsIdempotencyScope scope, string reservationToken);
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
/// reservation, recording, expiry and release mechanics live in <see cref="IdempotencyPayloadStore"/>,
/// which the synchronizeReplica upload store shares (#4026); this type owns the applyEdits key shape and
/// response serialization.
/// </summary>
internal sealed class DistributedApplyEditsIdempotencyStore : IApplyEditsIdempotencyStore
{
    private const string KeyPrefix = "featureserver:applyedits:idem:";

    /// <summary>
    /// Default dedupe window. A retry within this window of the original request replays the stored
    /// response; after it the key is forgotten and a re-submission is treated as a fresh edit.
    /// </summary>
    internal static readonly TimeSpan DedupeWindow = IdempotencyPayloadStore.DedupeWindow;

    /// <summary>
    /// Reservation window: how long a reservation is kept before it expires if the owning
    /// request never completes.
    /// </summary>
    internal static readonly TimeSpan ReservationWindow = IdempotencyPayloadStore.ReservationWindow;

    private readonly IdempotencyPayloadStore _payloads;
    private readonly ILogger<DistributedApplyEditsIdempotencyStore> _logger;

    public DistributedApplyEditsIdempotencyStore(
        IConnectionMultiplexer? multiplexer,
        IDistributedCache? cache,
        ILogger<DistributedApplyEditsIdempotencyStore> logger)
    {
        _payloads = new IdempotencyPayloadStore(multiplexer, cache);
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
        var payload = await _payloads.TryGetAsync(BuildKey(scope), ex => LogUnavailable(scope, ex), cancellationToken)
            .ConfigureAwait(false);
        return payload is null ? null : Deserialize(payload);
    }

    public Task<string?> TryReserveAsync(
        ApplyEditsIdempotencyScope scope,
        CancellationToken cancellationToken = default)
        => _payloads.TryReserveAsync(BuildKey(scope), ex => LogUnavailable(scope, ex), cancellationToken);

    public Task SetAsync(
        ApplyEditsIdempotencyScope scope,
        ApplyEditsResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return _payloads.SetAsync(BuildKey(scope), Serialize(response), ex => LogUnavailable(scope, ex), cancellationToken);
    }

    public Task ReleaseAsync(ApplyEditsIdempotencyScope scope, string reservationToken)
        => _payloads.ReleaseAsync(BuildKey(scope), reservationToken, ex => LogUnavailable(scope, ex));

    private void LogUnavailable(ApplyEditsIdempotencyScope scope, Exception exception)
        => FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, exception);

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

        return TryValidateKey(values.Count > 0 ? values[0] : null, $"{HeaderName} header", out key, out error);
    }

    /// <summary>
    /// Validates a client-supplied at-most-once key from any carrier (the header, or the Esri
    /// <c>editsUploadID</c> parameter of synchronizeReplica, #4026) with the same rules: not empty, at
    /// most <see cref="MaxKeyLength"/> characters, no control characters. <paramref name="label"/> names
    /// the carrier in the error message.
    /// </summary>
    public static bool TryValidateKey(string? raw, string label, out string? key, out string? error)
    {
        key = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{label} must not be empty.";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxKeyLength)
        {
            error = $"{label} must be at most {MaxKeyLength} characters.";
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = $"{label} must not contain control characters.";
            return false;
        }

        key = trimmed;
        return true;
    }
}

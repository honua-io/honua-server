// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Infrastructure.Security;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

// Concurrent applyEdits calls with the same key race on a set-if-absent reservation, and the loser returns 409 instead of committing a second copy.
internal interface IApplyEditsIdempotencyStore
{
    Task<ApplyEditsResponse?> TryGetAsync(
        ApplyEditsIdempotencyScope scope,
        CancellationToken cancellationToken = default);

    // The reservation token is unique to this reservation, so a request whose window lapsed cannot delete the reservation that belongs to someone else.
    Task<string?> TryReserveAsync(
        ApplyEditsIdempotencyScope scope,
        CancellationToken cancellationToken = default);

    // The receipt is written only while this request still owns the key, and a store failure is reported as not recorded so it cannot fail an edit that already committed.
    Task<bool> SetAsync(
        ApplyEditsIdempotencyScope scope,
        string reservationToken,
        ApplyEditsResponse response,
        CancellationToken cancellationToken = default);

    // Release runs only for a known no-write, compares and deletes this token alone, and takes no cancellation token so a cancelled owner can still free its key; an ambiguous post-dispatch failure stays held because the rows may have committed.
    Task ReleaseAsync(ApplyEditsIdempotencyScope scope, string reservationToken);
}

// The key is scoped to the caller, service, and layer so one caller's key cannot replay another caller's response.
internal readonly record struct ApplyEditsIdempotencyScope(
    string ServiceId,
    int LayerId,
    IdempotencyCaller Caller,
    string IdempotencyKey);

internal sealed class DistributedApplyEditsIdempotencyStore : IApplyEditsIdempotencyStore
{
    private const string KeyPrefix = "featureserver:applyedits:idem:";

    internal static readonly TimeSpan DedupeWindow = IdempotencyPayloadStore.DedupeWindow;

    // A configured request timeout can stretch the reservation past the default window.
    internal static readonly TimeSpan ReservationWindow = IdempotencyPayloadStore.ReservationWindow;

    // The grace margin covers work that still finishes after the request timeout token fires.
    private static readonly TimeSpan ReservationGrace = TimeSpan.FromSeconds(30);

    private readonly IdempotencyPayloadStore _payloads;
    private readonly TimeSpan _reservationWindow;
    private readonly ILogger<DistributedApplyEditsIdempotencyStore> _logger;

    public DistributedApplyEditsIdempotencyStore(
        IConnectionMultiplexer? multiplexer,
        IDistributedCache? cache,
        ILogger<DistributedApplyEditsIdempotencyStore> logger,
        TimeSpan? reservationWindow = null,
        TimeProvider? timeProvider = null)
    {
        _payloads = new IdempotencyPayloadStore(multiplexer, cache, timeProvider);
        _reservationWindow = reservationWindow is { } window && window > IdempotencyPayloadStore.ReservationWindow
            ? window
            : IdempotencyPayloadStore.ReservationWindow;
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

    // The reservation window is the timeout plus the grace margin and is never shorter than the default, because an expired reservation during the edit would let a retry run concurrently.
    internal static TimeSpan ReservationWindowFor(TimeSpan requestTimeout)
    {
        var window = requestTimeout + ReservationGrace;
        return window > IdempotencyPayloadStore.ReservationWindow ? window : IdempotencyPayloadStore.ReservationWindow;
    }

    public Task<string?> TryReserveAsync(
        ApplyEditsIdempotencyScope scope,
        CancellationToken cancellationToken = default)
        => _payloads.TryReserveAsync(BuildKey(scope), _reservationWindow, ex => LogUnavailable(scope, ex), cancellationToken);

    public Task<bool> SetAsync(
        ApplyEditsIdempotencyScope scope,
        string reservationToken,
        ApplyEditsResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(reservationToken);
        return _payloads.SetIfAbsentOrOwnedAsync(
            BuildKey(scope),
            reservationToken,
            Serialize(response),
            ex => LogUnavailable(scope, ex),
            cancellationToken);
    }

    public Task ReleaseAsync(ApplyEditsIdempotencyScope scope, string reservationToken)
        => _payloads.ReleaseAsync(BuildKey(scope), reservationToken, ex => LogUnavailable(scope, ex));

    private void LogUnavailable(ApplyEditsIdempotencyScope scope, Exception exception)
        => FeatureServerLog.ApplyEditsIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.LayerId, exception);

    private static string BuildKey(ApplyEditsIdempotencyScope scope)
        => string.Concat(
            KeyPrefix,
            scope.ServiceId,
            ":",
            scope.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ":",
            scope.Caller.Fingerprint(),
            ":",
            scope.IdempotencyKey);

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

internal readonly record struct ApplyEditsIdempotencyChoice(string? Key);

internal static class ApplyEditsIdempotency
{
    public const string HeaderName = "Idempotency-Key";

    public const int MaxKeyLength = 200;

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

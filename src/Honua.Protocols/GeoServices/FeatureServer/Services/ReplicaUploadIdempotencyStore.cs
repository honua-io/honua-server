// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Security;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

// A timed-out replica upload retries against the cursor the first attempt committed, so the outcome is recorded or the retry would insert the same adds again.
internal interface IReplicaUploadIdempotencyStore
{
    Task<ReplicaUploadRecord?> TryGetAsync(ReplicaUploadIdempotencyScope scope, CancellationToken cancellationToken = default);

    Task<string?> TryReserveAsync(ReplicaUploadIdempotencyScope scope, CancellationToken cancellationToken = default);

    // The receipt is written after the rows commit and takes no cancellation token, because abandoning it would let a retry duplicate those rows.
    Task<bool> RecordAsync(ReplicaUploadIdempotencyScope scope, string reservationToken, ReplicaUploadRecord record);

    // A reservation is released only when the upload provably committed nothing, so the retry re-attempts it.
    Task ReleaseAsync(ReplicaUploadIdempotencyScope scope, string reservationToken);
}

// The upload key is scoped to the service, replica, and caller, and the key and fingerprint prefixes stay in separate spaces so one cannot replay the other.
internal readonly record struct ReplicaUploadIdempotencyScope(
    string ServiceId,
    string ReplicaId,
    IdempotencyCaller Caller,
    string UploadKey);

internal sealed class ReplicaUploadRecord
{
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    // A partial failure is replayed as that failure, so a retry does not apply the already-committed rows again.
    [JsonPropertyName("failed")]
    public bool Failed { get; set; }

    [JsonPropertyName("appliedAdds")]
    public int AppliedAdds { get; set; }

    [JsonPropertyName("appliedUpdates")]
    public int AppliedUpdates { get; set; }

    [JsonPropertyName("appliedDeletes")]
    public int AppliedDeletes { get; set; }

    [JsonPropertyName("addResults")]
    public ServiceLayerEditResult[]? AddResults { get; set; }

    [JsonPropertyName("conflicts")]
    public SynchronizeReplicaConflict[]? Conflicts { get; set; }

    [JsonPropertyName("serverGeneration")]
    public long ServerGeneration { get; set; }
}

internal sealed class DistributedReplicaUploadIdempotencyStore : IReplicaUploadIdempotencyStore
{
    private const string KeyPrefix = "featureserver:replica-upload:idem:";

    // The grace margin covers conflict attachment and recording that still run after the request timeout token fires.
    private static readonly TimeSpan ReservationGrace = TimeSpan.FromSeconds(30);

    private readonly IdempotencyPayloadStore _payloads;
    private readonly TimeSpan _reservationWindow;
    private readonly ILogger<DistributedReplicaUploadIdempotencyStore> _logger;

    public DistributedReplicaUploadIdempotencyStore(
        IConnectionMultiplexer? multiplexer,
        IDistributedCache? cache,
        ILogger<DistributedReplicaUploadIdempotencyStore> logger,
        TimeSpan? reservationWindow = null,
        TimeProvider? timeProvider = null)
    {
        _payloads = new IdempotencyPayloadStore(multiplexer, cache, timeProvider);
        _reservationWindow = reservationWindow is { } window && window > IdempotencyPayloadStore.ReservationWindow
            ? window
            : IdempotencyPayloadStore.ReservationWindow;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // The upload reservation is the timeout plus the grace margin and is never shorter than the default, because expiry during the upload would let a retry duplicate the adds.
    internal static TimeSpan ReservationWindowFor(TimeSpan requestTimeout)
    {
        var window = requestTimeout + ReservationGrace;
        return window > IdempotencyPayloadStore.ReservationWindow ? window : IdempotencyPayloadStore.ReservationWindow;
    }

    public async Task<ReplicaUploadRecord?> TryGetAsync(
        ReplicaUploadIdempotencyScope scope,
        CancellationToken cancellationToken = default)
    {
        var payload = await _payloads.TryGetAsync(BuildKey(scope), ex => LogUnavailable(scope, ex), cancellationToken)
            .ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload, FeatureServerJsonContext.Default.ReplicaUploadRecord);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<string?> TryReserveAsync(
        ReplicaUploadIdempotencyScope scope,
        CancellationToken cancellationToken = default)
        => _payloads.TryReserveAsync(BuildKey(scope), _reservationWindow, ex => LogUnavailable(scope, ex), cancellationToken);

    public Task<bool> RecordAsync(
        ReplicaUploadIdempotencyScope scope,
        string reservationToken,
        ReplicaUploadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _payloads.SetIfAbsentOrOwnedAsync(
            BuildKey(scope),
            reservationToken,
            JsonSerializer.SerializeToUtf8Bytes(record, FeatureServerJsonContext.Default.ReplicaUploadRecord),
            ex => LogUnavailable(scope, ex),
            CancellationToken.None);
    }

    public Task ReleaseAsync(ReplicaUploadIdempotencyScope scope, string reservationToken)
        => _payloads.ReleaseAsync(BuildKey(scope), reservationToken, ex => LogUnavailable(scope, ex));

    private void LogUnavailable(ReplicaUploadIdempotencyScope scope, Exception exception)
        => FeatureServerLog.ReplicaUploadIdempotencyStoreUnavailable(_logger, scope.ServiceId, scope.ReplicaId, exception);

    private static string BuildKey(ReplicaUploadIdempotencyScope scope)
        => string.Concat(KeyPrefix, scope.ServiceId, ":", scope.ReplicaId, ":", scope.Caller.Fingerprint(), ":", scope.UploadKey);
}

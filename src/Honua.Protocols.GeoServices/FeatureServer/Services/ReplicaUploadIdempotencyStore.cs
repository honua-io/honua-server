// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// At-most-once store for <c>synchronizeReplica</c> uploads (#4026). A field client that times out
/// re-sends the identical upload; the retry passes the replica cursor compare-and-set because it reads
/// the cursor the first attempt committed, so without this store its adds were inserted again. The
/// outcome of every upload that committed rows is recorded so a retry replays it, and a concurrent
/// duplicate is held off by a reservation while the first is still applying.
/// </summary>
internal interface IReplicaUploadIdempotencyStore
{
    /// <summary>
    /// Returns the recorded upload for <paramref name="scope"/> within the dedupe window, or
    /// <see langword="null"/> when none was recorded or a concurrent upload holds the reservation.
    /// </summary>
    Task<ReplicaUploadRecord?> TryGetAsync(ReplicaUploadIdempotencyScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reserves <paramref name="scope"/> for long enough to cover the request; returns an
    /// ownership token, or <see langword="null"/> when another in-flight upload already holds it.
    /// </summary>
    Task<string?> TryReserveAsync(ReplicaUploadIdempotencyScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of an upload that committed rows under <paramref name="scope"/>, unless a
    /// different request now holds that key. Takes no cancellation token: it runs after the edits
    /// committed, when abandoning the record is what would let a retry duplicate them. Best-effort.
    /// </summary>
    Task<bool> RecordAsync(ReplicaUploadIdempotencyScope scope, string reservationToken, ReplicaUploadRecord record);

    /// <summary>
    /// Releases a reservation whose upload provably committed nothing, so a retry re-attempts it.
    /// </summary>
    Task ReleaseAsync(ReplicaUploadIdempotencyScope scope, string reservationToken);
}

/// <summary>
/// Identifies one replica upload. Scoped to the service, replica and principal so one caller's upload
/// can never replay another's, and the same key on a different replica is a distinct upload.
/// </summary>
/// <param name="ServiceId">The replica's stored service id, so request path casing cannot fork the key.</param>
/// <param name="ReplicaId">The replica being synchronized.</param>
/// <param name="Principal">The authenticated principal name, or <c>anonymous</c>.</param>
/// <param name="UploadKey">
/// <c>key:</c> followed by the client-supplied key, or <c>fingerprint:</c> followed by the upload
/// fingerprint and the replica upload cursor it was sent at; the prefixes keep the key spaces apart.
/// </param>
internal readonly record struct ReplicaUploadIdempotencyScope(
    string ServiceId,
    string ReplicaId,
    string Principal,
    string UploadKey);

/// <summary>
/// The recorded outcome of a replica upload that committed rows, replayed to a retry of the same upload.
/// </summary>
internal sealed class ReplicaUploadRecord
{
    /// <summary>SHA-256 fingerprint of the upload inputs, so a reused key with new edits is detected.</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// True when the upload failed after committing some rows; a retry replays the failure instead of
    /// applying the committed rows again.
    /// </summary>
    [JsonPropertyName("failed")]
    public bool Failed { get; set; }

    /// <summary>Uploaded adds applied by the original request.</summary>
    [JsonPropertyName("appliedAdds")]
    public int AppliedAdds { get; set; }

    /// <summary>Uploaded updates applied by the original request.</summary>
    [JsonPropertyName("appliedUpdates")]
    public int AppliedUpdates { get; set; }

    /// <summary>Uploaded deletes applied by the original request.</summary>
    [JsonPropertyName("appliedDeletes")]
    public int AppliedDeletes { get; set; }

    /// <summary>Per-layer add results (server object and global ids) of the original request.</summary>
    [JsonPropertyName("addResults")]
    public ServiceLayerEditResult[]? AddResults { get; set; }

    /// <summary>Conflicts the original request reported.</summary>
    [JsonPropertyName("conflicts")]
    public SynchronizeReplicaConflict[]? Conflicts { get; set; }

    /// <summary>Server generation after the original upload applied.</summary>
    [JsonPropertyName("serverGeneration")]
    public long ServerGeneration { get; set; }
}

/// <summary>
/// Redis-backed <see cref="IReplicaUploadIdempotencyStore"/> with the same in-process fallback and
/// reservation semantics as the applyEdits store (see <see cref="IdempotencyPayloadStore"/>).
/// </summary>
internal sealed class DistributedReplicaUploadIdempotencyStore : IReplicaUploadIdempotencyStore
{
    private const string KeyPrefix = "featureserver:replica-upload:idem:";

    /// <summary>
    /// Margin added to the request timeout when sizing the reservation, covering the work a request
    /// still finishes after its timeout token fires (uncancellable conflict attachment and recording).
    /// </summary>
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

    /// <summary>
    /// Reservation window for uploads bounded by <paramref name="requestTimeout"/>: the timeout plus a
    /// grace margin, and never shorter than the default window. A reservation that expired while its
    /// upload was still applying would let a retry run concurrently and duplicate the adds.
    /// </summary>
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
    {
        // Hash the principal so a name containing ":" cannot collide with other key segments.
        var principalHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope.Principal)));
        return string.Concat(KeyPrefix, scope.ServiceId, ":", scope.ReplicaId, ":", principalHash, ":", scope.UploadKey);
    }
}

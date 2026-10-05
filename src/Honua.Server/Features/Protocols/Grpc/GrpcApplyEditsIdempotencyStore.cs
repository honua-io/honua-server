using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Proto = Geospatial.V1;
using StackExchange.Redis;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>At-most-once store for gRPC ApplyEdits retries, shared through Redis when configured.</summary>
/// <remarks>
/// A retry scope binds the service, layer, effective tenant and the scheme- and
/// issuer-qualified actor (<see cref="CreateScope"/>), so a stored result is replayed only to
/// the caller that produced it. With Redis, the first caller holds an owner-token reservation
/// that is renewed for as long as its edit runs, and its result replaces the reservation only
/// while that token still holds it. A writer that can no longer prove it holds the reservation
/// is cancelled a full safety margin before the reservation could lapse, so a second replica
/// never begins the same edit while the first can still commit. Local state is bounded: key
/// gates are reference counted and dropped when idle, and local result copies live in a
/// size-limited, expiring cache that, with Redis, only holds results Redis could not persist.
/// </remarks>
internal sealed partial class GrpcApplyEditsIdempotencyStore : IDisposable
{
    internal static readonly TimeSpan DefaultResponseWindow = TimeSpan.FromHours(24);
    internal static readonly TimeSpan DefaultReservationWindow = TimeSpan.FromSeconds(60);
    internal const long DefaultLocalResponseBudgetBytes = 64L * 1024 * 1024;
    internal const int DefaultMaxUnpublishedResults = 1024;

    private const string RedisPrefix = "honua:grpc:apply-edits:idempotency:v2:";
    private const byte PendingMarker = 0xFF;
    private const byte ReceiptMarker = 0x01;
    private const int LocalEntryOverheadBytes = 256;
    private static readonly TimeSpan PendingPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxPublishInterval = TimeSpan.FromMilliseconds(250);

    // Reserve KEYS[1] for the caller's token, or return whatever holds it.
    internal const string AcquireScript = """
        if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then return ARGV[1] end
        return redis.call('GET', KEYS[1])
        """;

    internal const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
        return 0
        """;

    // A committed result replaces only the caller's own reservation, or a vacant key.
    internal const string CompleteScript = """
        local current = redis.call('GET', KEYS[1])
        if current == ARGV[1] or not current then
          redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
          return 1
        end
        return 0
        """;

    internal const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private readonly IDatabase? _redis;
    private readonly ILogger _logger;
    private readonly TimeSpan _responseWindow;
    private readonly TimeSpan _reservationWindow;
    private readonly TimeSpan _renewInterval;
    private readonly TimeSpan _publishInterval;
    private readonly int _maxUnpublishedResults;
    private readonly long _unpublishedBudgetBytes;
    private readonly ConcurrentDictionary<string, UnpublishedResult> _unpublished = new(StringComparer.Ordinal);
    private long _unpublishedBytes;
    private int _publisherRunning;
    private readonly TimeSpan _ownershipWindow;
    private readonly MemoryCache _localResponses;
    private readonly Dictionary<string, KeyGate> _gates = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdownSource = new();
    private readonly CancellationToken _shutdown;

    public GrpcApplyEditsIdempotencyStore(
        IConnectionMultiplexer? multiplexer = null,
        ILogger<GrpcApplyEditsIdempotencyStore>? logger = null)
        : this(
            multiplexer,
            logger,
            DefaultReservationWindow,
            DefaultResponseWindow,
            DefaultLocalResponseBudgetBytes,
            DefaultMaxUnpublishedResults)
    {
    }

    internal GrpcApplyEditsIdempotencyStore(
        IConnectionMultiplexer? multiplexer,
        ILogger? logger,
        TimeSpan reservationWindow,
        TimeSpan responseWindow,
        long localResponseBudgetBytes,
        int maxUnpublishedResults = DefaultMaxUnpublishedResults)
    {
        _redis = multiplexer?.GetDatabase();
        _logger = logger ?? NullLogger.Instance;
        _reservationWindow = reservationWindow;
        _responseWindow = responseWindow;
        _renewInterval = reservationWindow / 6;
        _publishInterval = _renewInterval < MaxPublishInterval ? _renewInterval : MaxPublishInterval;
        _maxUnpublishedResults = maxUnpublishedResults;
        _unpublishedBudgetBytes = localResponseBudgetBytes / 4;
        // The owner stops trusting its reservation a third of a window before the reservation
        // itself could expire, measured from when its last successful renewal was sent.
        _ownershipWindow = reservationWindow - (reservationWindow / 3);
        _localResponses = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = localResponseBudgetBytes,
            ExpirationScanFrequency = responseWindow < TimeSpan.FromMinutes(1) ? responseWindow : TimeSpan.FromMinutes(1),
        });
        _shutdown = _shutdownSource.Token;
    }

    internal int GateCount
    {
        get
        {
            lock (_gates)
            {
                return _gates.Count;
            }
        }
    }

    internal int LocalResponseCount => _localResponses.Count;

    internal int UnpublishedResultCount => _unpublished.Count;

    /// <summary>
    /// Builds the retry scope for a client idempotency key: service, layer, effective tenant,
    /// canonical actor and key, length-prefixed and hashed so no component can collide with
    /// another. Returns <see langword="null"/> for an authenticated caller whose identity
    /// cannot be bound to a durable actor.
    /// </summary>
    public static string? CreateScope(HttpContext? httpContext, string? serviceId, int layerId, string clientKey)
    {
        var principal = httpContext?.User;
        string actor;
        if (principal?.Identity?.IsAuthenticated == true)
        {
            var canonical = CanonicalSecurityActor.Resolve(principal);
            if (canonical is null)
            {
                return null;
            }

            actor = canonical.ActorId;
        }
        else
        {
            actor = "anonymous";
        }

        var tenantContext = httpContext?.RequestServices?.GetService<ITenantContext>();
        var tenant = tenantContext is not null
            ? tenantContext.TenantId
            : principal is null ? null : CanonicalSecurityActor.FindStampedValue(principal, CanonicalSecurityActor.EffectiveTenantClaim);

        var canonicalScope = new StringBuilder("grpc-apply-edits/v2");
        AppendComponent(canonicalScope, serviceId ?? string.Empty);
        AppendComponent(canonicalScope, layerId.ToString(CultureInfo.InvariantCulture));
        AppendComponent(canonicalScope, tenant is null ? "none" : "tenant:" + tenant);
        AppendComponent(canonicalScope, actor);
        AppendComponent(canonicalScope, clientKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalScope.ToString())));
    }

    public async Task<Lease> EnterAsync(string scope, CancellationToken cancellationToken)
    {
        var gate = await AcquireGateAsync(scope, cancellationToken).ConfigureAwait(false);
        try
        {
            // With Redis this holds only results whose shared receipt could not be written.
            if (_localResponses.TryGetValue(scope, out Proto.ApplyEditsResponse? local) && local is not null)
            {
                return new Lease(this, scope, gate, local.Clone(), reservation: null);
            }

            if (_redis is null)
            {
                return new Lease(this, scope, gate, response: null, reservation: null);
            }

            RedisKey redisKey = RedisPrefix + scope;
            var token = new byte[17];
            token[0] = PendingMarker;
            RandomNumberGenerator.Fill(token.AsSpan(1));
            while (true)
            {
                var sentAt = Stopwatch.GetTimestamp();
                var held = (byte[]?)await _redis.ScriptEvaluateAsync(
                    AcquireScript,
                    new RedisKey[] { redisKey },
                    new RedisValue[] { token, (long)_reservationWindow.TotalMilliseconds }).ConfigureAwait(false);
                if (held is null)
                {
                    continue;
                }

                if (held.AsSpan().SequenceEqual(token))
                {
                    return new Lease(this, scope, gate, response: null, new Reservation(this, redisKey, token, sentAt));
                }

                if (held.Length > 0 && held[0] == ReceiptMarker)
                {
                    return new Lease(this, scope, gate, Proto.ApplyEditsResponse.Parser.ParseFrom(held, 1, held.Length - 1), reservation: null);
                }

                await Task.Delay(PendingPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            ReleaseGate(scope, gate);
            throw;
        }
    }

    public void Dispose()
    {
        _shutdownSource.Cancel();
        _shutdownSource.Dispose();
        _localResponses.Dispose();
    }

    private static void AppendComponent(StringBuilder builder, string value)
        => builder.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    private async Task<KeyGate> AcquireGateAsync(string scope, CancellationToken cancellationToken)
    {
        KeyGate? gate;
        lock (_gates)
        {
            if (!_gates.TryGetValue(scope, out gate))
            {
                gate = new KeyGate();
                _gates.Add(scope, gate);
            }

            gate.References++;
        }

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return gate;
        }
        catch
        {
            DropGateReference(scope, gate);
            throw;
        }
    }

    private void ReleaseGate(string scope, KeyGate gate)
    {
        gate.Release();
        DropGateReference(scope, gate);
    }

    // A gate leaves the map only when no holder or waiter references it, so callers
    // queued on one key always share one semaphore.
    private void DropGateReference(string scope, KeyGate gate)
    {
        lock (_gates)
        {
            if (--gate.References == 0)
            {
                _gates.Remove(scope);
            }
        }
    }

    private void StoreLocal(string scope, Proto.ApplyEditsResponse response)
    {
        var copy = response.Clone();
        _localResponses.Set(scope, copy, new MemoryCacheEntryOptions
        {
            Size = copy.CalculateSize() + LocalEntryOverheadBytes,
            AbsoluteExpirationRelativeToNow = _responseWindow,
        });
    }

    private async Task<bool> TryWriteReceiptAsync(RedisKey key, byte[] token, byte[] receipt)
    {
        var written = (long)await _redis!.ScriptEvaluateAsync(
            CompleteScript,
            new RedisKey[] { key },
            new RedisValue[] { token, receipt, (long)_responseWindow.TotalMilliseconds }).ConfigureAwait(false);
        return written == 1;
    }

    // Queues a committed result whose shared receipt could not be written. One publisher
    // retries the whole queue at a short fixed interval, so once Redis is reachable again the
    // receipt lands (over this request's own reservation or a vacant key) before a client
    // retry is likely to reach another replica. The queue is bounded by count and bytes.
    private void QueueUnpublishedResult(string scope, RedisKey key, byte[] token, byte[] receipt)
    {
        if (_unpublished.Count >= _maxUnpublishedResults
            || Interlocked.Read(ref _unpublishedBytes) + receipt.Length > _unpublishedBudgetBytes)
        {
            Log.UnpublishedResultDropped(_logger, scope);
            return;
        }

        if (_unpublished.TryAdd(scope, new UnpublishedResult(key, token, receipt, Stopwatch.GetTimestamp())))
        {
            Interlocked.Add(ref _unpublishedBytes, receipt.Length);
        }

        if (Interlocked.CompareExchange(ref _publisherRunning, 1, 0) == 0)
        {
            _ = Task.Run(PublishUnpublishedResultsAsync);
        }
    }

    private async Task PublishUnpublishedResultsAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await Task.Delay(_publishInterval, _shutdown).ConfigureAwait(false);
                foreach (var (scope, pending) in _unpublished)
                {
                    if (Stopwatch.GetElapsedTime(pending.QueuedAt) >= _responseWindow)
                    {
                        RemoveUnpublished(scope, pending);
                        continue;
                    }

                    try
                    {
                        if (!await TryWriteReceiptAsync(pending.Key, pending.Token, pending.Receipt).ConfigureAwait(false))
                        {
                            Log.ReceiptHeldByAnotherRequest(_logger, scope);
                        }

                        RemoveUnpublished(scope, pending);
                    }
                    catch (Exception ex) when (ex is RedisException or TimeoutException)
                    {
                        // Redis is still unavailable; retry the queue on the next tick.
                        break;
                    }
                }

                if (_unpublished.IsEmpty)
                {
                    Volatile.Write(ref _publisherRunning, 0);
                    if (_unpublished.IsEmpty || Interlocked.CompareExchange(ref _publisherRunning, 1, 0) != 0)
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Store disposed.
        }
    }

    private void RemoveUnpublished(string scope, UnpublishedResult pending)
    {
        if (_unpublished.TryRemove(new KeyValuePair<string, UnpublishedResult>(scope, pending)))
        {
            Interlocked.Add(ref _unpublishedBytes, -pending.Receipt.Length);
        }
    }

    private async Task ReleaseReservationAsync(Reservation reservation)
    {
        try
        {
            await _redis!.ScriptEvaluateAsync(
                ReleaseScript,
                new RedisKey[] { reservation.Key },
                new RedisValue[] { reservation.Token }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The reservation still expires on its own time-to-live.
        }
    }

    /// <summary>The caller's hold on one retry scope; dispose it when the edit request ends.</summary>
    public sealed class Lease : IAsyncDisposable
    {
        private readonly GrpcApplyEditsIdempotencyStore _store;
        private readonly string _scope;
        private readonly KeyGate _gate;
        private readonly Reservation? _reservation;
        private CancellationTokenSource? _writeCancellation;
        private bool _writeStarted;
        private bool _completed;
        private bool _disposed;

        internal Lease(
            GrpcApplyEditsIdempotencyStore store,
            string scope,
            KeyGate gate,
            Proto.ApplyEditsResponse? response,
            Reservation? reservation)
        {
            _store = store;
            _scope = scope;
            _gate = gate;
            Response = response;
            _reservation = reservation;
        }

        /// <summary>The stored result to replay, or <see langword="null"/> when this caller must execute.</summary>
        public Proto.ApplyEditsResponse? Response { get; }

        /// <summary>Whether the shared reservation was lost before the edit completed.</summary>
        public bool OwnershipLost => _reservation?.Lost.IsCancellationRequested == true;

        /// <summary>
        /// Confirms the reservation is still held immediately before the write and returns the
        /// token the writer must observe: it is cancelled with the request or as soon as the
        /// reservation can no longer be proven held. Returns <see langword="null"/> when the
        /// reservation is already lost, in which case the caller must not write.
        /// </summary>
        public async Task<CancellationToken?> TryBeginWriteAsync(CancellationToken requestCancellation)
        {
            if (_reservation is null)
            {
                return requestCancellation;
            }

            if (!await _reservation.RenewNowAsync().ConfigureAwait(false))
            {
                return null;
            }

            _writeStarted = true;
            _writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, _reservation.Lost);
            return _writeCancellation.Token;
        }

        /// <summary>Records the committed result so retries in the same scope replay it.</summary>
        public async Task CompleteAsync(Proto.ApplyEditsResponse response)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (_reservation is null)
            {
                _store.StoreLocal(_scope, response);
                return;
            }

            await _reservation.StopAsync().ConfigureAwait(false);
            var body = response.ToByteArray();
            var receipt = new byte[body.Length + 1];
            receipt[0] = ReceiptMarker;
            body.CopyTo(receipt, 1);
            try
            {
                if (!await _store.TryWriteReceiptAsync(_reservation.Key, _reservation.Token, receipt).ConfigureAwait(false))
                {
                    Log.ReceiptHeldByAnotherRequest(_store._logger, _scope);
                }
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // The edit has committed: keep its result replayable here, and keep trying to
                // publish it so other replicas replay it instead of executing again.
                Log.ReceiptWriteFailed(_store._logger, _scope, ex);
                _store.StoreLocal(_scope, response);
                _store.QueueUnpublishedResult(_scope, _reservation.Key, _reservation.Token, receipt);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_reservation is not null)
                {
                    await _reservation.StopAsync().ConfigureAwait(false);
                    if (!_completed && !_writeStarted)
                    {
                        // Nothing was written, so a retry may proceed at once.
                        await _store.ReleaseReservationAsync(_reservation).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _writeCancellation?.Dispose();
                _reservation?.Dispose();
                _store.ReleaseGate(_scope, _gate);
            }
        }
    }

    private sealed record UnpublishedResult(RedisKey Key, byte[] Token, byte[] Receipt, long QueuedAt);

    /// <summary>Serializes callers on one scope; <see cref="References"/> counts holders and waiters.</summary>
    internal sealed class KeyGate() : SemaphoreSlim(1, 1)
    {
        public int References { get; set; }
    }

    /// <summary>A held Redis reservation, renewed in the background until stopped.</summary>
    internal sealed class Reservation : IDisposable
    {
        private readonly GrpcApplyEditsIdempotencyStore _store;
        private readonly CancellationTokenSource _lost = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _renewal;
        private long _ownedUntil;

        public Reservation(GrpcApplyEditsIdempotencyStore store, RedisKey key, byte[] token, long acquireSentAt)
        {
            _store = store;
            Key = key;
            Token = token;
            _ownedUntil = acquireSentAt + ToTimestampTicks(store._ownershipWindow);
            _renewal = Task.Run(RenewUntilStoppedAsync);
        }

        public RedisKey Key { get; }

        public byte[] Token { get; }

        public CancellationToken Lost => _lost.Token;

        public async Task<bool> RenewNowAsync()
        {
            if (_lost.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                return await RenewAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                return RemainingOwnership() > TimeSpan.Zero && !_lost.IsCancellationRequested;
            }
        }

        public async Task StopAsync()
        {
            if (!_stop.IsCancellationRequested)
            {
                await _stop.CancelAsync().ConfigureAwait(false);
            }

            await _renewal.ConfigureAwait(false);
        }

        /// <summary>Releases the token sources; call only after <see cref="StopAsync"/>.</summary>
        public void Dispose()
        {
            _lost.Dispose();
            _stop.Dispose();
        }

        private static long ToTimestampTicks(TimeSpan duration)
            => (long)(duration.TotalSeconds * Stopwatch.Frequency);

        private TimeSpan RemainingOwnership()
            => Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Volatile.Read(ref _ownedUntil));

        private async Task<bool> RenewAsync()
        {
            var sentAt = Stopwatch.GetTimestamp();
            var renewed = (long)await _store._redis!.ScriptEvaluateAsync(
                RenewScript,
                new RedisKey[] { Key },
                new RedisValue[] { Token, (long)_store._reservationWindow.TotalMilliseconds }).ConfigureAwait(false);
            if (renewed != 1)
            {
                MarkLost();
                return false;
            }

            Volatile.Write(ref _ownedUntil, sentAt + ToTimestampTicks(_store._ownershipWindow));
            return true;
        }

        private async Task RenewUntilStoppedAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var remaining = RemainingOwnership();
                if (remaining <= TimeSpan.Zero)
                {
                    MarkLost();
                    return;
                }

                try
                {
                    await Task.Delay(remaining < _store._renewInterval ? remaining : _store._renewInterval, _stop.Token)
                        .ConfigureAwait(false);
                    if (!await RenewAsync().ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is RedisException or TimeoutException)
                {
                    // Keep trying until the ownership deadline; the loop marks the
                    // reservation lost once it can no longer be proven held.
                    Log.ReservationRenewalFailed(_store._logger, ex);
                }
            }
        }

        private void MarkLost()
        {
            if (_stop.IsCancellationRequested || _lost.IsCancellationRequested)
            {
                return;
            }

            Log.ReservationLost(_store._logger);
            _lost.Cancel();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 5632,
            Level = LogLevel.Warning,
            Message = "A keyed gRPC edit lost its idempotency reservation; an uncommitted write is cancelled.")]
        public static partial void ReservationLost(ILogger logger);

        [LoggerMessage(
            EventId = 5633,
            Level = LogLevel.Warning,
            Message = "Could not record the result of keyed gRPC edit {Scope} in Redis; publishing it again in the background.")]
        public static partial void ReceiptWriteFailed(ILogger logger, string scope, Exception exception);

        [LoggerMessage(
            EventId = 5634,
            Level = LogLevel.Warning,
            Message = "The result of keyed gRPC edit {Scope} was not recorded because another request holds its key.")]
        public static partial void ReceiptHeldByAnotherRequest(ILogger logger, string scope);

        [LoggerMessage(
            EventId = 5635,
            Level = LogLevel.Debug,
            Message = "Renewing a keyed gRPC edit reservation failed; retrying until its ownership deadline.")]
        public static partial void ReservationRenewalFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 5636,
            Level = LogLevel.Warning,
            Message = "The result of keyed gRPC edit {Scope} is replayable only on this replica: the queue of results awaiting Redis is full.")]
        public static partial void UnpublishedResultDropped(ILogger logger, string scope);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Geoprocessing;

/// <summary>
/// Serializes the execution-admission check-and-create window across every server node that
/// shares the durable job substrate (#3853).
/// </summary>
/// <remarks>
/// <para>
/// Admission reads the active-job set, decides, and only then does the caller create the durable
/// job record. Two evaluations that overlap in that window both observe the same snapshot and can
/// together exceed the global, per-partition, and cost denominators. A process-local semaphore
/// closes the window on one node only; with several nodes behind a load balancer each node grants
/// the full budget.
/// </para>
/// <para>
/// When a Redis multiplexer is composed — the same Redis that holds the durable job records and
/// their active-set index — the window is fenced by a shared lease: a Redis lock with a bounded
/// TTL, taken before evaluation, re-validated immediately before the record is created, and
/// released as soon as the record exists. The durable active set stays the only source of truth
/// for counts, so there is no separate reservation ledger that could leak on restart, submission
/// failure, or terminal transition: a created job is counted until it turns terminal, a rolled-back
/// job leaves the active set with its terminal transition, and a node that dies holding the lease
/// stops blocking the others once the TTL expires.
/// </para>
/// <para>
/// The shared lease fails closed. When Redis is composed but unreachable, or the lease cannot be
/// taken within the bounded wait, or it expired before the record could be created, the submission
/// is rejected with a backpressure <see cref="GeoprocessingAdmissionException"/> carrying
/// Retry-After — never admitted against per-node state. Without a multiplexer (single-node
/// deployments) only the local gate applies, which is exact for a single node.
/// </para>
/// </remarks>
internal sealed class ExecutionAdmissionCoordinator
{
    /// <summary>Redis key of the cluster-wide admission lease.</summary>
    internal const string SharedLeaseKey = "honua:execution-admission:submission-lease";

    /// <summary>Policy reference when the shared admission state cannot be reached.</summary>
    internal const string UnavailablePolicyRef = "backpressure:admission-coordination:unavailable";

    /// <summary>Policy reference when the shared lease was not obtained within the bounded wait.</summary>
    internal const string ContendedPolicyRef = "backpressure:admission-coordination:contended";

    /// <summary>Policy reference when the lease expired before the job record was created.</summary>
    internal const string LeaseLostPolicyRef = "backpressure:admission-coordination:lease-lost";

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(100);

    // Coordinators composed without DI (direct construction in unit tests) keep the historical
    // process-wide serialization, so two services built side by side still share one gate.
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);

    private readonly SemaphoreSlim _localGate;
    private readonly IOptionsMonitor<ExecutionAdmissionOptions>? _options;
    private readonly ILogger _logger;
    private readonly IDatabase? _redis;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the coordinator. <paramref name="multiplexer"/> is optional: when present every
    /// admission window is fenced by the shared lease; when absent only the node-local gate applies.
    /// </summary>
    public ExecutionAdmissionCoordinator(
        IOptionsMonitor<ExecutionAdmissionOptions> options,
        ILogger<ExecutionAdmissionCoordinator> logger,
        IConnectionMultiplexer? multiplexer = null,
        TimeProvider? timeProvider = null)
        : this(new SemaphoreSlim(1, 1), options, logger, multiplexer?.GetDatabase(), timeProvider ?? TimeProvider.System)
    {
    }

    private ExecutionAdmissionCoordinator(
        SemaphoreSlim localGate,
        IOptionsMonitor<ExecutionAdmissionOptions>? options,
        ILogger logger,
        IDatabase? redis,
        TimeProvider timeProvider)
    {
        _localGate = localGate;
        _options = options;
        _logger = logger;
        _redis = redis;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Node-local coordinator used when none is composed. Serializes through one process-wide gate.
    /// </summary>
    public static ExecutionAdmissionCoordinator ProcessLocal { get; } =
        new(ProcessGate, options: null, NullLogger.Instance, redis: null, TimeProvider.System);

    /// <summary>
    /// Enters the admission window. Dispose the returned lease as soon as the job record has been
    /// created (or the submission has been rejected).
    /// </summary>
    /// <exception cref="GeoprocessingAdmissionException">
    /// The shared lease could not be obtained: Redis is unreachable or the lease stayed contended
    /// beyond <see cref="ExecutionAdmissionOptions.SharedLeaseAcquireTimeoutMilliseconds"/>.
    /// </exception>
    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _localGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var options = _options?.CurrentValue;
        if (_redis is null || options is null || !options.Enabled)
        {
            return new Lease(this, token: null, TimeSpan.Zero, retryAfterSeconds: 0);
        }

        try
        {
            var token = Guid.NewGuid().ToString("N");
            var ttl = TimeSpan.FromSeconds(options.SharedLeaseSeconds);
            var maxWait = TimeSpan.FromMilliseconds(options.SharedLeaseAcquireTimeoutMilliseconds);
            var started = _timeProvider.GetTimestamp();
            var delay = InitialRetryDelay;

            while (true)
            {
                if (await TryTakeAsync(token, ttl, options.DefaultRetryAfterSeconds).ConfigureAwait(false))
                {
                    return new Lease(this, token, ttl, options.DefaultRetryAfterSeconds);
                }

                var waited = _timeProvider.GetElapsedTime(started);
                if (waited >= maxWait)
                {
                    ExecutionAdmissionLog.SharedLeaseContended(_logger, (long)waited.TotalMilliseconds);
                    throw Reject(
                        ContendedPolicyRef,
                        "Execution admission is saturated by concurrent submissions; retry later.",
                        options.DefaultRetryAfterSeconds);
                }

                // Jitter keeps waiting nodes from polling in lockstep behind one holder.
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)delay.TotalMilliseconds + 1));
                await Task.Delay(delay + jitter, _timeProvider, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
        catch
        {
            _localGate.Release();
            throw;
        }
    }

    private async Task<bool> TryTakeAsync(string token, TimeSpan ttl, int retryAfterSeconds)
    {
        try
        {
            return await _redis!.LockTakeAsync(SharedLeaseKey, token, ttl).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            ExecutionAdmissionLog.SharedLeaseUnavailable(_logger, ex);
            throw Reject(
                UnavailablePolicyRef,
                "Shared execution admission state is unavailable; retry later.",
                retryAfterSeconds);
        }
    }

    private static GeoprocessingAdmissionException Reject(string policyRef, string reason, int retryAfterSeconds)
        => new(
            ExecutionAdmissionOutcome.Denied,
            ExecutionAdmissionDimension.Backpressure,
            policyRef,
            reason,
            retryAfterSeconds);

    /// <summary>
    /// Holds the admission window. Disposal releases the shared lease (when held) and the local gate.
    /// </summary>
    internal sealed class Lease : IAsyncDisposable
    {
        private readonly ExecutionAdmissionCoordinator _owner;
        private readonly string? _token;
        private readonly TimeSpan _ttl;
        private readonly int _retryAfterSeconds;
        private int _disposed;

        internal Lease(ExecutionAdmissionCoordinator owner, string? token, TimeSpan ttl, int retryAfterSeconds)
        {
            _owner = owner;
            _token = token;
            _ttl = ttl;
            _retryAfterSeconds = retryAfterSeconds;
        }

        /// <summary>
        /// Whether the window is fenced cluster-wide (true) or on this node only (false).
        /// </summary>
        public bool IsShared => _token is not null;

        /// <summary>
        /// Re-validates the shared lease immediately before the job record is created, extending it
        /// by a full TTL. Throws a backpressure rejection when the lease already expired — another
        /// node may be admitting against the same snapshot — so no record is created.
        /// </summary>
        public async Task EnsureHeldAsync()
        {
            if (_token is null)
            {
                return;
            }

            bool extended;
            try
            {
                extended = await _owner._redis!.LockExtendAsync(SharedLeaseKey, _token, _ttl).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                ExecutionAdmissionLog.SharedLeaseUnavailable(_owner._logger, ex);
                throw Reject(
                    UnavailablePolicyRef,
                    "Shared execution admission state is unavailable; retry later.",
                    _retryAfterSeconds);
            }

            if (!extended)
            {
                ExecutionAdmissionLog.SharedLeaseLost(_owner._logger);
                throw Reject(
                    LeaseLostPolicyRef,
                    "Execution admission could not be confirmed before the job was created; retry later.",
                    _retryAfterSeconds);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (_token is not null)
                {
                    await _owner._redis!.LockReleaseAsync(SharedLeaseKey, _token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The lease TTL bounds how long a failed release can block other nodes.
                ExecutionAdmissionLog.SharedLeaseReleaseFailed(_owner._logger, ex);
            }
            finally
            {
                _owner._localGate.Release();
            }
        }
    }
}

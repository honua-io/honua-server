// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Coordination;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

// The shared lease fails closed when the lease store is composed but unreachable, and there is no separate reservation ledger because the durable active set is the only count.
internal sealed class ExecutionAdmissionCoordinator
{
    internal const string SharedLeaseKey = "execution-admission:submission";

    internal const string UnavailablePolicyRef = "backpressure:admission-coordination:unavailable";

    internal const string ContendedPolicyRef = "backpressure:admission-coordination:contended";

    internal const string LeaseLostPolicyRef = "backpressure:admission-coordination:lease-lost";

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(100);

    // Coordinators composed without DI (direct construction in unit tests) keep the historical
    // process-wide serialization, so two services built side by side still share one gate.
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);

    private readonly SemaphoreSlim _localGate;
    private readonly IOptionsMonitor<ExecutionAdmissionOptions>? _options;
    private readonly ILogger _logger;
    private readonly ILeaseStore? _leases;
    private readonly TimeProvider _timeProvider;

    public ExecutionAdmissionCoordinator(
        IOptionsMonitor<ExecutionAdmissionOptions> options,
        ILogger<ExecutionAdmissionCoordinator> logger,
        ILeaseStore? leases = null,
        TimeProvider? timeProvider = null)
        : this(new SemaphoreSlim(1, 1), options, logger, leases, timeProvider ?? TimeProvider.System)
    {
    }

    private ExecutionAdmissionCoordinator(
        SemaphoreSlim localGate,
        IOptionsMonitor<ExecutionAdmissionOptions>? options,
        ILogger logger,
        ILeaseStore? leases,
        TimeProvider timeProvider)
    {
        _localGate = localGate;
        _options = options;
        _logger = logger;
        _leases = leases;
        _timeProvider = timeProvider;
    }

    public static ExecutionAdmissionCoordinator ProcessLocal { get; } =
        new(ProcessGate, options: null, NullLogger.Instance, leases: null, TimeProvider.System);

    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _localGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var options = _options?.CurrentValue;
        if (_leases is null || options is null || !options.Enabled)
        {
            return new Lease(this, token: null, TimeSpan.Zero, retryAfterSeconds: 0);
        }

        try
        {
            var claim = new LeaseClaim(SharedLeaseKey, Guid.NewGuid().ToString("N"));
            var ttl = TimeSpan.FromSeconds(options.SharedLeaseSeconds);
            var maxWait = TimeSpan.FromMilliseconds(options.SharedLeaseAcquireTimeoutMilliseconds);
            var started = _timeProvider.GetTimestamp();
            var delay = InitialRetryDelay;

            while (true)
            {
                if (await TryTakeAsync(claim, ttl, options.DefaultRetryAfterSeconds, cancellationToken).ConfigureAwait(false))
                {
                    return new Lease(this, claim, ttl, options.DefaultRetryAfterSeconds);
                }

                var waited = _timeProvider.GetElapsedTime(started);
                if (waited >= maxWait)
                {
                    ExecutionAdmissionLeaseLog.SharedLeaseContended(_logger, (long)waited.TotalMilliseconds);
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

    private async Task<bool> TryTakeAsync(LeaseClaim claim, TimeSpan ttl, int retryAfterSeconds, CancellationToken cancellationToken)
    {
        try
        {
            return await _leases!.TryAcquireAsync(claim, ttl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            ExecutionAdmissionLeaseLog.SharedLeaseUnavailable(_logger, ex);
            throw Reject(
                UnavailablePolicyRef,
                "Shared execution admission state is unavailable; retry later.",
                retryAfterSeconds);
        }
    }

    private static ExecutionAdmissionLeaseException Reject(string policyRef, string reason, int retryAfterSeconds)
        => new(
            ExecutionAdmissionOutcome.Denied,
            ExecutionAdmissionDimension.Backpressure,
            policyRef,
            reason,
            retryAfterSeconds);

    internal sealed class Lease : IAsyncDisposable
    {
        private readonly ExecutionAdmissionCoordinator _owner;
        private readonly LeaseClaim? _claim;
        private readonly TimeSpan _ttl;
        private readonly int _retryAfterSeconds;
        private int _disposed;

        internal Lease(ExecutionAdmissionCoordinator owner, LeaseClaim? claim, TimeSpan ttl, int retryAfterSeconds)
        {
            _owner = owner;
            _claim = claim;
            _ttl = ttl;
            _retryAfterSeconds = retryAfterSeconds;
        }

        public bool IsShared => _claim is not null;

        // Re-validate the shared lease immediately before the job record is created; an expired lease throws because another node may be admitting against the same snapshot.
        public async Task EnsureHeldAsync()
        {
            if (_claim is not { } claim)
            {
                return;
            }

            bool extended;
            try
            {
                extended = await _owner._leases!.RenewAsync(claim, _ttl).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                ExecutionAdmissionLeaseLog.SharedLeaseUnavailable(_owner._logger, ex);
                throw Reject(
                    UnavailablePolicyRef,
                    "Shared execution admission state is unavailable; retry later.",
                    _retryAfterSeconds);
            }

            if (!extended)
            {
                ExecutionAdmissionLeaseLog.SharedLeaseLost(_owner._logger);
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
                if (_claim is { } claim)
                {
                    await _owner._leases!.ReleaseAsync(claim).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The lease TTL bounds how long a failed release can block other nodes.
                ExecutionAdmissionLeaseLog.SharedLeaseReleaseFailed(_owner._logger, ex);
            }
            finally
            {
                _owner._localGate.Release();
            }
        }
    }
}

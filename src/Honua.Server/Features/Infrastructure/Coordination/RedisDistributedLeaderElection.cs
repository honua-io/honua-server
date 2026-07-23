// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using StackExchange.Redis;

namespace Honua.Infrastructure.Coordination;

/// <summary>
/// Redis-based distributed leader election with automatic lease renewal.
/// </summary>
internal sealed partial class RedisDistributedLeaderElection : IDistributedLeaderElection, IDisposable
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RedisRetryBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisposeReleaseTimeout = TimeSpan.FromSeconds(1);
    private const string RenewLeadershipScript = @"
        local key = KEYS[1]
        local instanceId = ARGV[1]
        local leaseMilliseconds = tonumber(ARGV[2])

        if redis.call('GET', key) ~= instanceId then
            return 0
        end

        redis.call('PEXPIRE', key, leaseMilliseconds)
        return 1
    ";
    private const string ReleaseLeadershipScript = @"
        local key = KEYS[1]
        local instanceId = ARGV[1]

        local current = redis.call('GET', key)
        if current == instanceId then
            redis.call('DEL', key)
            return 1
        end

        return 0
    ";

    private readonly IDatabase? _redisDb;
    private readonly string _leaderKey;
    private readonly string _instanceId;
    private readonly ILogger<RedisDistributedLeaderElection> _logger;
    private readonly Timer _renewalTimer;
    private readonly bool _allowFallback;

    private volatile bool _isLeader;
    private volatile bool _disposed;
    // Stored as UTC ticks and accessed via Interlocked so the renewal-timer thread and arbitrary
    // caller threads observe a consistent failure timestamp for the retry-backoff window.
    private long _lastRedisFailureTicks = DateTime.MinValue.Ticks;
    // Only ever assigned in the constructor (below), so this can be readonly instead of
    // volatile; a readonly field is safely published once construction completes.
    private readonly bool _useRedis;

    public RedisDistributedLeaderElection(
        string leaderKey,
        IConnectionMultiplexer? redis,
        ILogger<RedisDistributedLeaderElection> logger,
        bool allowFallback = true)
    {
        _leaderKey = leaderKey ?? throw new ArgumentNullException(nameof(leaderKey));
        _instanceId = Environment.MachineName + "_" + Guid.NewGuid().ToString("N")[..8];
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _allowFallback = allowFallback;

        if (redis?.IsConnected == true)
        {
            _redisDb = redis.GetDatabase();
            _useRedis = true;
            Log.DistributedElectionEnabled(_logger, _leaderKey);
        }
        else
        {
            _useRedis = false;
            if (!_allowFallback)
            {
                throw new InvalidOperationException($"Redis is required for distributed leader election '{_leaderKey}' but is not available");
            }
            _isLeader = true; // In fallback mode, this instance is always the leader
            Log.FallbackElectionEnabled(_logger, _leaderKey);
        }

        // Set up automatic lease renewal
        _renewalTimer = new Timer(RenewLeaseAsync, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public bool IsLeader => _isLeader;

    /// <inheritdoc />
    public string InstanceId => _instanceId;

    /// <inheritdoc />
    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        if (!_useRedis || !ShouldUseRedis())
        {
            _isLeader = _allowFallback;
            return _isLeader;
        }

        if (_redisDb == null)
        {
            _isLeader = _allowFallback;
            return _isLeader;
        }

        try
        {
            var acquired = await _redisDb.StringSetAsync(_leaderKey, _instanceId, DefaultLeaseDuration, When.NotExists);

            if (acquired)
            {
                _isLeader = true;
                StartLeaseRenewal();
                Log.LeadershipAcquired(_logger, _leaderKey, _instanceId);
            }
            else
            {
                _isLeader = false;
                Log.LeadershipAcquisitionFailed(_logger, _leaderKey, _instanceId);
            }

            return acquired;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: Redis is best-effort here — any failure (connection,
            // timeout, script error) falls back to local leadership when allowed rather than
            // propagating and crashing the caller.
            Log.LeaderElectionError(_logger, "TryAcquireLeadership", _leaderKey, ex);
            MarkRedisFailure();

            if (_allowFallback)
            {
                _isLeader = true;
                Log.LeadershipFallbackActivated(_logger, _leaderKey);
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !_isLeader)
        {
            return false;
        }

        if (!_useRedis || !ShouldUseRedis())
        {
            if (_allowFallback)
            {
                return true;
            }

            _isLeader = false;
            StopLeaseRenewal();
            return false;
        }

        if (_redisDb == null)
        {
            return _allowFallback;
        }

        try
        {
            var extended = (int)await _redisDb.ScriptEvaluateAsync(
                RenewLeadershipScript,
                new RedisKey[] { _leaderKey },
                new RedisValue[] { _instanceId, (long)DefaultLeaseDuration.TotalMilliseconds }) == 1;

            if (!extended)
            {
                // Lost leadership
                _isLeader = false;
                StopLeaseRenewal();
                Log.LeadershipLost(_logger, _leaderKey, _instanceId);
            }

            return extended;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: Redis is best-effort here — any failure (connection,
            // timeout, script error) falls back to local leadership when allowed rather than
            // propagating and crashing the caller.
            Log.LeaderElectionError(_logger, "Heartbeat", _leaderKey, ex);
            MarkRedisFailure();

            if (_allowFallback)
            {
                Log.HeartbeatFallbackActivated(_logger, _leaderKey);
                return true;
            }

            _isLeader = false;
            StopLeaseRenewal();
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default)
    {
        if (!_isLeader)
        {
            return;
        }

        StopLeaseRenewal();

        if (!_useRedis || _redisDb == null)
        {
            _isLeader = false;
            return;
        }

        try
        {
            // Only release if we still own the lock.
            var released = (int)await _redisDb.ScriptEvaluateAsync(
                ReleaseLeadershipScript,
                new RedisKey[] { _leaderKey },
                new RedisValue[] { _instanceId });

            if (released == 1)
            {
                Log.LeadershipReleased(_logger, _leaderKey, _instanceId);
            }
            else
            {
                Log.LeadershipAlreadyLost(_logger, _leaderKey, _instanceId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: best-effort release of the Redis lock; the finally
            // block below already clears local leadership state regardless of outcome, so
            // a Redis failure here must not propagate to the caller.
            Log.LeaderElectionError(_logger, "ReleaseLeadership", _leaderKey, ex);
        }
        finally
        {
            _isLeader = false;
        }
    }

    private void StartLeaseRenewal()
    {
        if (_disposed)
        {
            return;
        }

        _renewalTimer.Change(LeaseRenewalInterval, LeaseRenewalInterval);
    }

    private void StopLeaseRenewal()
    {
        try
        {
            _renewalTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Expected race with Dispose(): the timer may already be disposed by a concurrent
            // caller. Nothing to do since we are already tearing down.
        }
    }

    private async void RenewLeaseAsync(object? state)
    {
        // async void timer callback: NO exception may escape (no managed handler -> crash).
        // Outer try/catch is the last line of defense even for the disposed/leader checks.
        try
        {
            if (_disposed || !_isLeader)
            {
                return;
            }

            try
            {
                var renewed = await HeartbeatAsync();
                if (!renewed)
                {
                    Log.LeaseRenewalFailed(_logger, _leaderKey, _instanceId);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Intentional broad catch: this is a timer-driven background renewal
                // tick; a single failed heartbeat must not kill the renewal timer, so
                // it is logged and the next tick retries.
                Log.LeaseRenewalError(_logger, _leaderKey, _instanceId, ex);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: async void timer callback — no managed handler
            // exists above this, so any escaping exception here would crash the
            // process. This is the last line of defense; see the comment at the top
            // of this method.
            Log.UnhandledTimerException(_logger, nameof(RenewLeaseAsync), ex);
        }
    }

    private bool ShouldUseRedis()
    {
        if (!_useRedis || _redisDb?.Multiplexer.IsConnected != true)
        {
            return false;
        }

        // If we've had recent Redis failures, wait before retrying
        var lastRedisFailureTicks = Interlocked.Read(ref _lastRedisFailureTicks);
        if (lastRedisFailureTicks != DateTime.MinValue.Ticks &&
            DateTime.UtcNow - new DateTime(lastRedisFailureTicks, DateTimeKind.Utc) < RedisRetryBackoff)
        {
            return false;
        }

        return true;
    }

    private void MarkRedisFailure()
    {
        Interlocked.Exchange(ref _lastRedisFailureTicks, DateTime.UtcNow.Ticks);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopLeaseRenewal();

        // Release leadership if we have it, without making synchronous disposal depend
        // on Redis/network responsiveness.
        if (_isLeader && _useRedis)
        {
            _ = Task.Run(ReleaseLeadershipOnDisposeAsync);
        }

        _disposed = true;
        _renewalTimer.Dispose();
    }

    private async Task ReleaseLeadershipOnDisposeAsync()
    {
        try
        {
            await ReleaseLeadershipAsync().WaitAsync(DisposeReleaseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _isLeader = false;
            Log.DisposeReleaseTimedOut(_logger, _leaderKey, _instanceId, DisposeReleaseTimeout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: best-effort cleanup during Dispose; a Redis
            // failure while releasing the lock must not throw out of the fire-and-forget
            // dispose task, so it is logged and local state is cleared regardless.
            _isLeader = false;
            Log.LeaderElectionError(_logger, "DisposeReleaseLeadership", _leaderKey, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(1201, LogLevel.Information, "Distributed leader election enabled for {LeaderKey}")]
        public static partial void DistributedElectionEnabled(ILogger logger, string leaderKey);

        [LoggerMessage(1202, LogLevel.Information, "Leader election fallback mode enabled for {LeaderKey}")]
        public static partial void FallbackElectionEnabled(ILogger logger, string leaderKey);

        [LoggerMessage(1203, LogLevel.Information, "Leadership acquired for {LeaderKey} by instance {InstanceId}")]
        public static partial void LeadershipAcquired(ILogger logger, string leaderKey, string instanceId);

        [LoggerMessage(1204, LogLevel.Debug, "Leadership acquisition failed for {LeaderKey} by instance {InstanceId}")]
        public static partial void LeadershipAcquisitionFailed(ILogger logger, string leaderKey, string instanceId);

        [LoggerMessage(1205, LogLevel.Warning, "Leadership lost for {LeaderKey} by instance {InstanceId}")]
        public static partial void LeadershipLost(ILogger logger, string leaderKey, string instanceId);

        [LoggerMessage(1206, LogLevel.Information, "Leadership released for {LeaderKey} by instance {InstanceId}")]
        public static partial void LeadershipReleased(ILogger logger, string leaderKey, string instanceId);

        [LoggerMessage(1207, LogLevel.Debug, "Leadership already lost for {LeaderKey} by instance {InstanceId}")]
        public static partial void LeadershipAlreadyLost(ILogger logger, string leaderKey, string instanceId);

        [LoggerMessage(1208, LogLevel.Warning, "Lease renewal failed for {LeaderKey} by instance {InstanceId}")]
        public static partial void LeaseRenewalFailed(ILogger logger, string leaderKey, string instanceId);

        [LoggerMessage(1209, LogLevel.Error, "Lease renewal error for {LeaderKey} by instance {InstanceId}")]
        public static partial void LeaseRenewalError(ILogger logger, string leaderKey, string instanceId, Exception exception);

        [LoggerMessage(1210, LogLevel.Warning, "Leader election operation {Operation} failed for {LeaderKey}")]
        public static partial void LeaderElectionError(ILogger logger, string operation, string leaderKey, Exception exception);

        [LoggerMessage(1211, LogLevel.Information, "Leadership fallback activated for {LeaderKey}")]
        public static partial void LeadershipFallbackActivated(ILogger logger, string leaderKey);

        [LoggerMessage(1212, LogLevel.Debug, "Heartbeat fallback activated for {LeaderKey}")]
        public static partial void HeartbeatFallbackActivated(ILogger logger, string leaderKey);

        [LoggerMessage(1213, LogLevel.Warning, "Timed out waiting {Timeout} to release leadership for {LeaderKey} by instance {InstanceId} during dispose")]
        public static partial void DisposeReleaseTimedOut(ILogger logger, string leaderKey, string instanceId, TimeSpan timeout);

        [LoggerMessage(1214, LogLevel.Error, "Unhandled exception escaped timer callback {Callback}")]
        public static partial void UnhandledTimerException(ILogger logger, string callback, Exception exception);
    }
}

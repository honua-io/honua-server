// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Coordination;

/// <summary>
/// Redis-based distributed leader election with automatic lease renewal.
/// </summary>
internal sealed partial class RedisDistributedLeaderElection : IDistributedLeaderElection, IDisposable
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RedisRetryBackoff = TimeSpan.FromSeconds(30);
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
    private DateTime _lastRedisFailure = DateTime.MinValue;
    private volatile bool _useRedis;

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
        catch (Exception ex)
        {
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
        catch (Exception ex)
        {
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
        catch (Exception ex)
        {
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
        }
    }

    private async void RenewLeaseAsync(object? state)
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
        catch (Exception ex)
        {
            Log.LeaseRenewalError(_logger, _leaderKey, _instanceId, ex);
        }
    }

    private bool ShouldUseRedis()
    {
        if (!_useRedis || _redisDb?.Multiplexer.IsConnected != true)
        {
            return false;
        }

        // If we've had recent Redis failures, wait before retrying
        if (_lastRedisFailure != DateTime.MinValue &&
            DateTime.UtcNow - _lastRedisFailure < RedisRetryBackoff)
        {
            return false;
        }

        return true;
    }

    private void MarkRedisFailure()
    {
        _lastRedisFailure = DateTime.UtcNow;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopLeaseRenewal();

        // Release leadership if we have it
        if (_isLeader && _useRedis)
        {
            try
            {
                _ = Task.Run(async () => await ReleaseLeadershipAsync());
            }
            catch
            {
                // Ignore disposal exceptions
            }
        }

        _disposed = true;
        _renewalTimer.Dispose();
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
    }
}

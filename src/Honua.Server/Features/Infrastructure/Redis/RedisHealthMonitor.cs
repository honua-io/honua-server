// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Redis;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Monitors Redis health and provides circuit breaker functionality.
/// </summary>
internal sealed partial class RedisHealthMonitor : IRedisHealthMonitor, IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly int MaxConsecutiveFailures = 3;

    private readonly IConnectionMultiplexer? _redis;
    private readonly IDatabase? _database;
    private readonly ILogger<RedisHealthMonitor> _logger;
    private readonly Timer _healthCheckTimer;
    private readonly object _stateLock = new();

    private volatile bool _isRedisAvailable;
    private volatile bool _wasRedisEverAvailable;
    private volatile int _consecutiveFailures;
    private DateTimeOffset? _lastSuccessfulContact;
    private DateTimeOffset? _lastFailure;
    private bool _disposed;

    public RedisHealthMonitor(
        IConnectionMultiplexer? redis,
        ILogger<RedisHealthMonitor> logger)
    {
        _redis = redis;
        _database = redis?.GetDatabase();
        _logger = logger;

        // Initialize state based on Redis availability
        _isRedisAvailable = _redis != null;
        _wasRedisEverAvailable = _redis != null;

        if (_redis != null)
        {
            _lastSuccessfulContact = DateTimeOffset.UtcNow;
            Log.RedisHealthMonitorStarted(_logger);
        }
        else
        {
            Log.RedisNotConfigured(_logger);
        }

        // Start periodic health checks
        _healthCheckTimer = new Timer(
            PerformHealthCheck,
            null,
            HealthCheckInterval,
            HealthCheckInterval);
    }

    public bool IsRedisAvailable => _isRedisAvailable;
    public bool WasRedisEverAvailable => _wasRedisEverAvailable;
    public DateTimeOffset? LastSuccessfulContact => _lastSuccessfulContact;
    public DateTimeOffset? LastFailure => _lastFailure;
    public int ConsecutiveFailures => _consecutiveFailures;

    public bool ShouldRetryRedis
    {
        get
        {
            if (_isRedisAvailable || _database == null)
                return false;

            var lastFailure = _lastFailure;
            if (lastFailure == null)
                return true;

            return DateTimeOffset.UtcNow - lastFailure.Value > RetryInterval;
        }
    }

    public void RecordSuccess()
    {
        lock (_stateLock)
        {
            var wasUnavailable = !_isRedisAvailable;
            _isRedisAvailable = true;
            _wasRedisEverAvailable = true;
            _consecutiveFailures = 0;
            _lastSuccessfulContact = DateTimeOffset.UtcNow;

            if (wasUnavailable)
            {
                Log.RedisConnectionRestored(_logger);
            }
        }
    }

    public void RecordFailure(Exception exception)
    {
        lock (_stateLock)
        {
            var wasAvailable = _isRedisAvailable;
            _isRedisAvailable = false;
            _lastFailure = DateTimeOffset.UtcNow;
            _consecutiveFailures++;

            if (wasAvailable || _consecutiveFailures <= MaxConsecutiveFailures)
            {
                Log.RedisConnectionFailed(_logger, _consecutiveFailures, exception);
            }
        }
    }

    public async Task<bool> TestConnectivityAsync(CancellationToken cancellationToken = default)
    {
        if (_database == null)
            return false;

        try
        {
            await _database.PingAsync().ConfigureAwait(false);
            RecordSuccess();
            return true;
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            return false;
        }
    }

    private async void PerformHealthCheck(object? state)
    {
        if (_disposed)
            return;

        try
        {
            await TestConnectivityAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.HealthCheckFailed(_logger, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _healthCheckTimer?.Dispose();
        Log.RedisHealthMonitorDisposed(_logger);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Redis health monitor started")]
        public static partial void RedisHealthMonitorStarted(ILogger logger);

        [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "Redis not configured - health monitoring disabled")]
        public static partial void RedisNotConfigured(ILogger logger);

        [LoggerMessage(EventId = 9003, Level = LogLevel.Information, Message = "Redis connection restored")]
        public static partial void RedisConnectionRestored(ILogger logger);

        [LoggerMessage(EventId = 9004, Level = LogLevel.Warning, Message = "Redis connection failed (consecutive failures: {ConsecutiveFailures})")]
        public static partial void RedisConnectionFailed(ILogger logger, int consecutiveFailures, Exception exception);

        [LoggerMessage(EventId = 9005, Level = LogLevel.Warning, Message = "Redis health check failed")]
        public static partial void HealthCheckFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9006, Level = LogLevel.Information, Message = "Redis health monitor disposed")]
        public static partial void RedisHealthMonitorDisposed(ILogger logger);
    }
}

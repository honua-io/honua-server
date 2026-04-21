// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Redis;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Redis-based distributed leader election with automatic lease renewal and fallback handling.
/// </summary>
internal sealed partial class RedisLeaderElection : RedisServiceBase, IRedisLeaderElection, IDisposable
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(20); // Renew at 1/3 of lease duration
    private static readonly TimeSpan LeadershipCheckInterval = TimeSpan.FromSeconds(5);
<<<<<<< HEAD
=======
    private static readonly TimeSpan DisposeReleaseTimeout = TimeSpan.FromSeconds(1);
>>>>>>> origin/trunk

    private readonly string _nodeId;
    private readonly string _leadershipKey;
    private readonly TimeSpan _leaseDuration;
    private readonly Timer _renewalTimer;
    private readonly object _leadershipLock = new();

    private volatile bool _isLeader;
    private volatile bool _isStarted;
    private volatile bool _disposed;
    private string? _currentLeader;
    private CancellationTokenSource? _stopTokenSource;

    public RedisLeaderElection(
        string leadershipKey,
        IConnectionMultiplexer? redis,
        IRedisHealthMonitor healthMonitor,
        IHostEnvironment hostEnvironment,
        ILogger<RedisLeaderElection> logger,
        TimeSpan? leaseDuration = null,
        string? nodeId = null)
        : base(redis, healthMonitor, RedisFallbackStrategy.AllowLocalInDev, hostEnvironment, logger)
    {
        _leadershipKey = leadershipKey ?? throw new ArgumentNullException(nameof(leadershipKey));
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        _nodeId = nodeId ?? Environment.MachineName + ":" + Environment.ProcessId + ":" + Guid.NewGuid().ToString("N")[..8];

        // Initialize renewal timer (disabled until Start is called)
        _renewalTimer = new Timer(OnRenewalTimer, null, Timeout.Infinite, Timeout.Infinite);

        Log.LeaderElectionInitialized(Logger, _nodeId, _leadershipKey, FallbackMode);
    }

    public string NodeId => _nodeId;
    public string LeadershipKey => _leadershipKey;
    public bool IsLeader => _isLeader;
    public bool IsConfigured => IsRedisConfigured;
    public string? CurrentLeader => _currentLeader;
    public TimeSpan LeaseDuration => _leaseDuration;

    public event EventHandler<Honua.Core.Features.Infrastructure.Redis.LeadershipChangedEventArgs>? LeadershipChanged;

    public async Task<bool> TryAcquireOrExtendLeadershipAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return false;

        try
        {
            bool acquired = await ExecuteWithFallbackAsync(
                "leader-election",
                async (database, ct) =>
                {
                    bool result;

                    if (_isLeader)
                    {
                        // Try to extend existing lease
                        result = await database.LockExtendAsync(_leadershipKey, _nodeId, _leaseDuration).ConfigureAwait(false);
                    }
                    else
                    {
                        // Try to acquire new lease
                        result = await database.LockTakeAsync(_leadershipKey, _nodeId, _leaseDuration).ConfigureAwait(false);
                    }

                    return result;
                },
                fallbackOperation: ct => Task.FromResult(ShouldAllowLocalLeadership()),
                cancellationToken).ConfigureAwait(false);

            UpdateLeadershipStatus(acquired);
            return acquired;
        }
        catch (Exception ex)
        {
            Log.LeadershipAcquisitionFailed(Logger, _nodeId, ex);
            UpdateLeadershipStatus(false);
            return false;
        }
    }

    public async Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !_isLeader)
            return;

        try
        {
            await ExecuteWithFallbackAsync(
                "release-leadership",
                async (database, ct) =>
                {
                    await database.LockReleaseAsync(_leadershipKey, _nodeId).ConfigureAwait(false);
                    return true;
                },
                fallbackOperation: ct => Task.FromResult(true),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LeadershipReleaseFailed(Logger, _nodeId, ex);
        }
        finally
        {
            UpdateLeadershipStatus(false);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isStarted || _disposed)
            return Task.CompletedTask;

        lock (_leadershipLock)
        {
            if (_isStarted || _disposed)
                return Task.CompletedTask;

            _isStarted = true;
            _stopTokenSource = new CancellationTokenSource();

            // Start renewal timer
            _renewalTimer.Change(TimeSpan.Zero, RenewalInterval);

            Log.LeaderElectionStarted(Logger, _nodeId);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStarted || _disposed)
            return;

        lock (_leadershipLock)
        {
            if (!_isStarted || _disposed)
                return;

            _isStarted = false;
            _stopTokenSource?.Cancel();

            // Stop renewal timer
            _renewalTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // Release leadership before stopping
        await ReleaseLeadershipAsync(cancellationToken).ConfigureAwait(false);

        Log.LeaderElectionStopped(Logger, _nodeId);
    }

<<<<<<< HEAD
    private void OnRenewalTimer(object? state)
=======
    private async void OnRenewalTimer(object? state)
>>>>>>> origin/trunk
    {
        if (_disposed || !_isStarted || _stopTokenSource?.IsCancellationRequested == true)
            return;

<<<<<<< HEAD
        // Fire-and-forget task with proper exception handling
        _ = Task.Run(async () =>
        {
            try
            {
                await TryAcquireOrExtendLeadershipAsync(_stopTokenSource?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.LeadershipRenewalFailed(Logger, _nodeId, ex);
            }
        });
=======
        try
        {
            await TryAcquireOrExtendLeadershipAsync(_stopTokenSource?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LeadershipRenewalFailed(Logger, _nodeId, ex);
        }
>>>>>>> origin/trunk
    }

    private void UpdateLeadershipStatus(bool isLeader)
    {
        bool changed;
        string? previousLeader;
        string? newLeader;

        lock (_leadershipLock)
        {
            changed = _isLeader != isLeader;
            previousLeader = _currentLeader;

            _isLeader = isLeader;
            _currentLeader = isLeader ? _nodeId : null;
            newLeader = _currentLeader;
        }

        if (changed)
        {
            Log.LeadershipStatusChanged(Logger, _nodeId, isLeader);
            LeadershipChanged?.Invoke(
                this,
                new Honua.Core.Features.Infrastructure.Redis.LeadershipChangedEventArgs(isLeader, previousLeader, newLeader));
        }
    }

    private bool ShouldAllowLocalLeadership()
    {
        // In fallback mode, only allow leadership if we're in dev/test or Redis was never configured
        return FallbackStrategy.ShouldAllowFallback(HealthMonitor, HostEnvironment);
    }

    protected override void OnRedisRestored()
    {
        Log.RedisRestoredForLeaderElection(Logger, _nodeId);
        // Force re-evaluation of leadership when Redis comes back online
        if (_isStarted)
        {
            _ = Task.Run(async () =>
            {
                await TryAcquireOrExtendLeadershipAsync().ConfigureAwait(false);
            });
        }
    }

    protected override void OnRedisFailure(string operation, Exception exception)
    {
        Log.RedisFailureInLeaderElection(Logger, _nodeId, operation, exception);

        // In fail-fast or production mode, losing Redis means losing leadership
        if (!FallbackStrategy.ShouldAllowFallback(HealthMonitor, HostEnvironment))
        {
            UpdateLeadershipStatus(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

<<<<<<< HEAD
        _disposed = true;

=======
>>>>>>> origin/trunk
        // Stop async operations first
        _stopTokenSource?.Cancel();
        _renewalTimer.Dispose();

        // Release leadership
        if (_isLeader && IsUsingRedis)
        {
            try
            {
<<<<<<< HEAD
                // Fire-and-forget async cleanup to avoid deadlock in Dispose
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ReleaseLeadershipAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.DisposeCleanupFailed(Logger, _nodeId, ex);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.DisposeCleanupFailed(Logger, _nodeId, ex);
            }
        }

=======
                ReleaseLeadershipAsync(CancellationToken.None).WaitAsync(DisposeReleaseTimeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                UpdateLeadershipStatus(false);
                Log.DisposeReleaseTimedOut(Logger, _nodeId, DisposeReleaseTimeout);
            }
            catch (Exception ex)
            {
                UpdateLeadershipStatus(false);
                Log.DisposeCleanupFailed(Logger, _nodeId, ex);
            }
        }
        else
        {
            UpdateLeadershipStatus(false);
        }

        _disposed = true;
>>>>>>> origin/trunk
        _stopTokenSource?.Dispose();
        Log.LeaderElectionDisposed(Logger, _nodeId);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9101, Level = LogLevel.Information,
            Message = "Leader election initialized (NodeId: {NodeId}, Key: {Key}, Mode: {Mode})")]
        public static partial void LeaderElectionInitialized(ILogger logger, string nodeId, string key, RedisFallbackMode mode);

        [LoggerMessage(EventId = 9102, Level = LogLevel.Information,
            Message = "Leader election started (NodeId: {NodeId})")]
        public static partial void LeaderElectionStarted(ILogger logger, string nodeId);

        [LoggerMessage(EventId = 9103, Level = LogLevel.Information,
            Message = "Leader election stopped (NodeId: {NodeId})")]
        public static partial void LeaderElectionStopped(ILogger logger, string nodeId);

        [LoggerMessage(EventId = 9104, Level = LogLevel.Information,
            Message = "Leadership status changed (NodeId: {NodeId}, IsLeader: {IsLeader})")]
        public static partial void LeadershipStatusChanged(ILogger logger, string nodeId, bool isLeader);

        [LoggerMessage(EventId = 9105, Level = LogLevel.Warning,
            Message = "Leadership acquisition failed (NodeId: {NodeId})")]
        public static partial void LeadershipAcquisitionFailed(ILogger logger, string nodeId, Exception exception);

        [LoggerMessage(EventId = 9106, Level = LogLevel.Warning,
            Message = "Leadership release failed (NodeId: {NodeId})")]
        public static partial void LeadershipReleaseFailed(ILogger logger, string nodeId, Exception exception);

        [LoggerMessage(EventId = 9107, Level = LogLevel.Warning,
            Message = "Leadership renewal failed (NodeId: {NodeId})")]
        public static partial void LeadershipRenewalFailed(ILogger logger, string nodeId, Exception exception);

        [LoggerMessage(EventId = 9108, Level = LogLevel.Information,
            Message = "Redis restored for leader election (NodeId: {NodeId})")]
        public static partial void RedisRestoredForLeaderElection(ILogger logger, string nodeId);

        [LoggerMessage(EventId = 9109, Level = LogLevel.Warning,
            Message = "Redis failure in leader election (NodeId: {NodeId}, Operation: {Operation})")]
        public static partial void RedisFailureInLeaderElection(ILogger logger, string nodeId, string operation, Exception exception);

        [LoggerMessage(EventId = 9110, Level = LogLevel.Information,
            Message = "Leader election disposed (NodeId: {NodeId})")]
        public static partial void LeaderElectionDisposed(ILogger logger, string nodeId);

        [LoggerMessage(EventId = 9111, Level = LogLevel.Warning,
            Message = "Cleanup failed during dispose (NodeId: {NodeId})")]
        public static partial void DisposeCleanupFailed(ILogger logger, string nodeId, Exception exception);
<<<<<<< HEAD
=======

        [LoggerMessage(EventId = 9112, Level = LogLevel.Warning,
            Message = "Timed out waiting {Timeout} to release leadership during dispose (NodeId: {NodeId})")]
        public static partial void DisposeReleaseTimedOut(ILogger logger, string nodeId, TimeSpan timeout);
>>>>>>> origin/trunk
    }
}

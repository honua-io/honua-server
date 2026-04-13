// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Redis;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Redis-based distributed job queue with in-memory fallback support.
/// </summary>
internal sealed partial class RedisJobQueue : RedisServiceBase, IRedisJobQueue
{
    private const int MaxFallbackQueueSize = 10000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly string _queueKey;
    private readonly string _processingQueueKey;
    private readonly ConcurrentQueue<string> _fallbackQueue = new();
    private readonly ConcurrentDictionary<string, byte> _fallbackInFlight = new(StringComparer.Ordinal);
    private long _fallbackQueueLength;

    public RedisJobQueue(
        string queueKey,
        IConnectionMultiplexer? redis,
        IRedisHealthMonitor healthMonitor,
        IRedisFallbackStrategy fallbackStrategy,
        IHostEnvironment hostEnvironment,
        ILogger<RedisJobQueue> logger)
        : base(redis, healthMonitor, fallbackStrategy, hostEnvironment, logger)
    {
        _queueKey = queueKey ?? throw new ArgumentNullException(nameof(queueKey));
        _processingQueueKey = $"{queueKey}:processing";

        Log.JobQueueInitialized(Logger, _queueKey, FallbackMode, IsRedisConfigured);
    }

    public string QueueKey => _queueKey;
    public int InFlightCount => _fallbackInFlight.Count;
    public int FallbackQueueLength => (int)Volatile.Read(ref _fallbackQueueLength);

    public async Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await ExecuteWithFallbackAsync(
                "enqueue",
                async (database, ct) =>
                {
                    await database.ListLeftPushAsync(_queueKey, jobId).ConfigureAwait(false);
                    return true;
                },
                fallbackOperation: ct =>
                {
                    EnqueueFallback(jobId);
                    return Task.FromResult(true);
                },
                cancellationToken).ConfigureAwait(false);

            Log.JobEnqueued(Logger, jobId, IsUsingRedis ? "Redis" : "InMemory");
        }
        catch (Exception ex)
        {
            Log.JobEnqueueFailed(Logger, jobId, ex);
            throw;
        }
    }

    public async Task<string?> DequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            // Try to restore Redis if it's currently unavailable
            if (!IsUsingRedis)
            {
                await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
            }

            string? jobId = null;

            try
            {
                jobId = await ExecuteWithFallbackAsync(
                    "dequeue",
                    async (database, ct) =>
                    {
                        var result = await database.ListRightPopLeftPushAsync(_queueKey, _processingQueueKey).ConfigureAwait(false);
                        return result.HasValue ? result.ToString() : null;
                    },
                    fallbackOperation: ct => Task.FromResult(DequeueFallback()),
                    cancellationToken).ConfigureAwait(false);

                if (jobId != null)
                {
                    Log.JobDequeued(Logger, jobId, IsUsingRedis ? "Redis" : "InMemory");
                    return jobId;
                }
            }
            catch (Exception ex)
            {
                Log.JobDequeueFailed(Logger, ex);
                // Continue trying in fallback mode
            }

            // Wait before polling again
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Dequeues a job using default timeout (implements IRedisJobQueue interface).
    /// </summary>
    public Task<string?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        return DequeueAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    public async Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default)
    {
        var fallbackLength = Volatile.Read(ref _fallbackQueueLength);

        try
        {
            var redisLength = await ExecuteWithFallbackAsync(
                "queue-length",
                async (database, ct) =>
                {
                    return await database.ListLengthAsync(_queueKey).ConfigureAwait(false);
                },
                fallbackOperation: ct => Task.FromResult(0L),
                cancellationToken).ConfigureAwait(false);

            return redisLength + fallbackLength;
        }
        catch (Exception ex)
        {
            Log.QueueLengthCheckFailed(Logger, ex);
            return fallbackLength;
        }
    }

    public async Task<long> GetProcessingCountAsync(CancellationToken cancellationToken = default)
    {
        var fallbackInFlight = _fallbackInFlight.Count;

        try
        {
            var redisProcessing = await ExecuteWithFallbackAsync(
                "processing-count",
                async (database, ct) =>
                {
                    return await database.ListLengthAsync(_processingQueueKey).ConfigureAwait(false);
                },
                fallbackOperation: ct => Task.FromResult(0L),
                cancellationToken).ConfigureAwait(false);

            return redisProcessing + fallbackInFlight;
        }
        catch (Exception ex)
        {
            Log.QueueLengthCheckFailed(Logger, ex);
            return fallbackInFlight;
        }
    }

    public async Task CompleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await ExecuteWithFallbackAsync(
                "complete",
                async (database, ct) =>
                {
                    await database.ListRemoveAsync(_processingQueueKey, jobId, 1).ConfigureAwait(false);
                    return true;
                },
                fallbackOperation: ct =>
                {
                    CompleteFallback(jobId);
                    return Task.FromResult(true);
                },
                cancellationToken).ConfigureAwait(false);

            Log.JobCompleted(Logger, jobId, IsUsingRedis ? "Redis" : "InMemory");
        }
        catch (Exception ex)
        {
            Log.JobCompleteFailed(Logger, jobId, ex);
            throw;
        }
    }

    public async Task RecoverInFlightAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ExecuteWithFallbackAsync(
                "recover-in-flight",
                async (database, ct) =>
                {
                    int recoveredCount = 0;
                    RedisValue[] inFlight;

                    do
                    {
                        inFlight = await database.ListRangeAsync(_processingQueueKey, 0, 99).ConfigureAwait(false);
                        foreach (var jobValue in inFlight)
                        {
                            ct.ThrowIfCancellationRequested();

                            var jobId = jobValue.ToString();
                            if (string.IsNullOrWhiteSpace(jobId))
                                continue;

                            await database.ListRemoveAsync(_processingQueueKey, jobId, 1).ConfigureAwait(false);
                            await database.ListLeftPushAsync(_queueKey, jobId).ConfigureAwait(false);
                            recoveredCount++;
                        }
                    }
                    while (inFlight.Length > 0 && !ct.IsCancellationRequested);

                    return recoveredCount;
                },
                fallbackOperation: ct => Task.FromResult(RecoverInFlightFallback()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.RecoveryFailed(Logger, ex);
            throw;
        }
    }

    private void EnqueueFallback(string jobId)
    {
        if (Volatile.Read(ref _fallbackQueueLength) >= MaxFallbackQueueSize)
        {
            throw new InvalidOperationException($"Fallback queue is full (max: {MaxFallbackQueueSize})");
        }

        _fallbackQueue.Enqueue(jobId);
        Interlocked.Increment(ref _fallbackQueueLength);
    }

    private string? DequeueFallback()
    {
        if (_fallbackQueue.TryDequeue(out var jobId))
        {
            Interlocked.Decrement(ref _fallbackQueueLength);
            _fallbackInFlight.TryAdd(jobId, 0);
            return jobId;
        }

        return null;
    }

    private void CompleteFallback(string jobId)
    {
        _fallbackInFlight.TryRemove(jobId, out _);
    }

    private int RecoverInFlightFallback()
    {
        int recoveredCount = 0;

        foreach (var jobId in _fallbackInFlight.Keys.ToArray())
        {
            if (_fallbackInFlight.TryRemove(jobId, out _))
            {
                _fallbackQueue.Enqueue(jobId);
                Interlocked.Increment(ref _fallbackQueueLength);
                recoveredCount++;
            }
        }

        return recoveredCount;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9201, Level = LogLevel.Information,
            Message = "Job queue initialized (Key: {QueueKey}, Mode: {Mode}, Redis: {RedisConfigured})")]
        public static partial void JobQueueInitialized(ILogger logger, string queueKey, RedisFallbackMode mode, bool redisConfigured);

        [LoggerMessage(EventId = 9202, Level = LogLevel.Debug,
            Message = "Job enqueued (JobId: {JobId}, Source: {Source})")]
        public static partial void JobEnqueued(ILogger logger, string jobId, string source);

        [LoggerMessage(EventId = 9203, Level = LogLevel.Debug,
            Message = "Job dequeued (JobId: {JobId}, Source: {Source})")]
        public static partial void JobDequeued(ILogger logger, string jobId, string source);

        [LoggerMessage(EventId = 9204, Level = LogLevel.Debug,
            Message = "Job completed (JobId: {JobId}, Source: {Source})")]
        public static partial void JobCompleted(ILogger logger, string jobId, string source);

        [LoggerMessage(EventId = 9205, Level = LogLevel.Warning,
            Message = "Job enqueue failed (JobId: {JobId})")]
        public static partial void JobEnqueueFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(EventId = 9206, Level = LogLevel.Warning,
            Message = "Job dequeue failed")]
        public static partial void JobDequeueFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9207, Level = LogLevel.Warning,
            Message = "Job complete failed (JobId: {JobId})")]
        public static partial void JobCompleteFailed(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(EventId = 9208, Level = LogLevel.Warning,
            Message = "Queue length check failed")]
        public static partial void QueueLengthCheckFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 9209, Level = LogLevel.Warning,
            Message = "Recovery failed")]
        public static partial void RecoveryFailed(ILogger logger, Exception exception);
    }
}

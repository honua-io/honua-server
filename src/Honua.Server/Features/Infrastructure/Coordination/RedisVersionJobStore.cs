// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using StackExchange.Redis;

namespace Honua.Infrastructure.Coordination;

// A Redis failure on one operation falls back to the in-memory store instead of failing the job surface.
internal sealed partial class RedisVersionJobStore : IVersionJobStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const int MaximumRecoveryWrites = 3;
    private const string CompareExchangeScript = """
        local current = redis.call('GET', KEYS[1])
        if (not current and ARGV[1] == '') or current == ARGV[1] then
            redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
            return 1
        end
        return 0
        """;
    private const string KeyPrefix = "honua:version:job:";

    private readonly IDatabase? _database;
    private readonly InMemoryVersionJobStore _fallback = new();
    private readonly ConcurrentDictionary<Guid, VersionJob> _pending = new();
    private readonly ILogger<RedisVersionJobStore> _logger;

    public RedisVersionJobStore(IConnectionMultiplexer? redis, ILogger<RedisVersionJobStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // GetDatabase does not require an active connection (the multiplexer is created with
        // AbortOnConnectFail=false), so resolve it whenever Redis is configured at all. Checking
        // IsConnected here would silently and permanently pin this singleton to the non-durable
        // in-memory store when Redis is merely slow or briefly down at startup.
        _database = redis?.GetDatabase();
        if (_database is null)
        {
            Log.VersionJobStoreInMemoryFallback(_logger);
        }
    }

    public async Task SaveAsync(VersionJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (_database is null)
        {
            await _fallback.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _pending.AddOrUpdate(job.JobId, job, (_, current) => CanAdvance(current, job) ? job : current);
        try
        {
            await ReconcilePendingAsync(job.JobId, createWhenMissing: true, cancellationToken).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            Log.VersionJobStoreRedisError(_logger, job.JobId, ex);
        }
    }

    public async Task<VersionJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_database is null)
        {
            return await _fallback.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ReconcilePendingAsync(jobId, createWhenMissing: false, cancellationToken).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            Log.VersionJobStoreRedisError(_logger, jobId, ex);
            return _pending.GetValueOrDefault(jobId);
        }
    }

    private async Task<VersionJob?> ReconcilePendingAsync(Guid jobId, bool createWhenMissing, CancellationToken cancellationToken)
    {
        var key = $"{KeyPrefix}{jobId:N}";
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _database!.StringGetAsync(key).ConfigureAwait(false);
            var current = snapshot.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize((string)snapshot!, VersionJobJsonContext.Default.VersionJob);
            if (!_pending.TryGetValue(jobId, out var pending))
            {
                return current;
            }
            if (current is null && !createWhenMissing)
            {
                return pending;
            }
            if (current is not null && !CanAdvance(current, pending))
            {
                _pending.TryRemove(new KeyValuePair<Guid, VersionJob>(jobId, pending));
                return current;
            }
            if (attempt >= MaximumRecoveryWrites)
            {
                return current ?? pending;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var payload = JsonSerializer.Serialize(pending, VersionJobJsonContext.Default.VersionJob);
            var acknowledged = await _database.ScriptEvaluateAsync(CompareExchangeScript, [key],
                [snapshot.IsNullOrEmpty ? "" : snapshot, payload, (long)Retention.TotalMilliseconds]).ConfigureAwait(false);
            if ((long)acknowledged == 1)
            {
                _pending.TryRemove(new KeyValuePair<Guid, VersionJob>(jobId, pending));
                return pending;
            }
        }
    }

    private static bool CanAdvance(VersionJob current, VersionJob next)
        => HasSameIdentity(current, next) && (current.Status switch
        {
            VersionJobStatus.Pending => next.Status is VersionJobStatus.Running or VersionJobStatus.Succeeded
                or VersionJobStatus.Failed or VersionJobStatus.LockContended,
            VersionJobStatus.Running => next.Status is VersionJobStatus.Succeeded
                or VersionJobStatus.Failed or VersionJobStatus.LockContended,
            _ => false
        });

    private static bool HasSameIdentity(VersionJob current, VersionJob next)
        => current.JobId == next.JobId && current.Service == next.Service && current.VersionId == next.VersionId
            && current.Kind == next.Kind && current.Policy == next.Policy && current.CreatedAt == next.CreatedAt
            && current.ConflictDetection == next.ConflictDetection;

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7112,
            Level = LogLevel.Information,
            Message = "Redis is not configured; version jobs are stored in memory and are neither pollable from other replicas nor restart-durable.")]
        public static partial void VersionJobStoreInMemoryFallback(ILogger logger);

        [LoggerMessage(
            EventId = 7113,
            Level = LogLevel.Warning,
            Message = "Version job store Redis operation failed for job {JobId}; degrading to in-memory storage for this operation.")]
        public static partial void VersionJobStoreRedisError(ILogger logger, Guid jobId, Exception exception);
    }
}

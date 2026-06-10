// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using StackExchange.Redis;

namespace Honua.Infrastructure.Coordination;

/// <summary>
/// Redis-backed durable <see cref="IVersionJobStore"/> (#1553). Persists async reconcile/post job
/// records so the job surface is pollable across replicas and survives a restart. Mirrors the
/// execution-job and import-job stores' Redis-string-with-TTL pattern. When Redis is not configured
/// it delegates to the single-node in-memory store, which is correct for single-node and
/// development/test deployments; per-operation Redis failures degrade to the in-memory store with a
/// logged warning rather than failing the job surface outright.
/// </summary>
internal sealed partial class RedisVersionJobStore : IVersionJobStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const string KeyPrefix = "honua:version:job:";

    private readonly IDatabase? _database;
    private readonly InMemoryVersionJobStore _fallback = new();
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

    /// <inheritdoc />
    public async Task SaveAsync(VersionJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (_database is null)
        {
            await _fallback.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = JsonSerializer.Serialize(job, VersionJobJsonContext.Default.VersionJob);
        try
        {
            await _database.StringSetAsync($"{KeyPrefix}{job.JobId:N}", payload, Retention).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            // Keep the job surface available during a Redis outage: the record stays pollable on
            // this node (only) and the degradation is logged so operators can see durability is
            // reduced until Redis recovers.
            Log.VersionJobStoreRedisError(_logger, job.JobId, ex);
            await _fallback.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<VersionJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_database is null)
        {
            return await _fallback.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        RedisValue value;
        try
        {
            value = await _database.StringGetAsync($"{KeyPrefix}{jobId:N}").ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            Log.VersionJobStoreRedisError(_logger, jobId, ex);
            return await _fallback.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        if (value.IsNullOrEmpty)
        {
            // A record saved to the in-memory fallback during a Redis outage is not in Redis;
            // consult the fallback before reporting the job as unknown.
            return await _fallback.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Deserialize((string)value!, VersionJobJsonContext.Default.VersionJob);
    }

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

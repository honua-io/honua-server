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
/// execution-job and import-job stores' Redis-string-with-TTL pattern. When Redis is not connected it
/// delegates to the single-node in-memory store, which is correct for single-node and development/test
/// deployments.
/// </summary>
internal sealed class RedisVersionJobStore : IVersionJobStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const string KeyPrefix = "honua:version:job:";

    private readonly IDatabase? _database;
    private readonly InMemoryVersionJobStore _fallback = new();

    public RedisVersionJobStore(IConnectionMultiplexer? redis)
        => _database = redis?.IsConnected == true ? redis.GetDatabase() : null;

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
        await _database.StringSetAsync($"{KeyPrefix}{job.JobId:N}", payload, Retention).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VersionJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_database is null)
        {
            return await _fallback.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        var value = await _database.StringGetAsync($"{KeyPrefix}{jobId:N}").ConfigureAwait(false);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize((string)value!, VersionJobJsonContext.Default.VersionJob);
    }
}

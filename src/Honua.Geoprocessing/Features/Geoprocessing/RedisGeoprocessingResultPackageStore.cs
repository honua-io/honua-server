// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using StackExchange.Redis;

namespace Honua.Server.Features.ControlPlane;

/// <summary>
/// Redis-backed store for terminal geoprocessing result packages.
/// </summary>
internal sealed partial class RedisGeoprocessingResultPackageStore(
    IConnectionMultiplexer redis,
    ILogger<RedisGeoprocessingResultPackageStore> logger) : IGeoprocessingResultPackageStore
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<AnalysisResultPackage?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = await _database.StringGetAsync(GetKey(jobId)).ConfigureAwait(false);
        if (!payload.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize(
            payload.ToString(),
            ControlPlaneJsonContext.Default.AnalysisResultPackage);
    }

    public async Task SetAsync(
        string jobId,
        AnalysisResultPackage package,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.Serialize(
            package,
            ControlPlaneJsonContext.Default.AnalysisResultPackage);
        await _database.StringSetAsync(
                GetKey(jobId),
                payload,
                ttl ?? DefaultRetention)
            .ConfigureAwait(false);

        Log.ResultPackageStored(logger, jobId, package.ResultPackageId);
    }

    private static string GetKey(string jobId) => $"controlplane:job:gp-result:{jobId}";

    private static partial class Log
    {
        [LoggerMessage(9040, LogLevel.Debug, "Stored geoprocessing result package for job {JobId}: {ResultPackageId}")]
        public static partial void ResultPackageStored(
            ILogger logger,
            string jobId,
            string resultPackageId);
    }
}

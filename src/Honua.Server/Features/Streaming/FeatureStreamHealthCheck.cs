// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Honua.Server.Features.Streaming;

// Streaming degradation does not fail readiness, because delivery stays recoverable from the durable store.
internal sealed class FeatureStreamHealthCheck : IHealthCheck
{
    // Fraction of the concurrent-session cap above which the check reports Degraded.
    private const double SaturationRatio = 0.9;

    private readonly FeatureStreamSessionManager _sessionManager;

    public FeatureStreamHealthCheck(FeatureStreamSessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var activeSessions = _sessionManager.SessionCount;
        var maximumPartitionSessions = _sessionManager.MaximumPartitionSessionCount;
        var maxSessions = _sessionManager.MaxConcurrentSessions;
        var slowConsumerDrops = _sessionManager.SlowConsumerDrops;

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["active_sessions"] = activeSessions,
            ["maximum_partition_sessions"] = maximumPartitionSessions,
            ["max_concurrent_sessions"] = maxSessions,
            ["slow_consumer_drops"] = slowConsumerDrops,
        };

        // Session saturation: at the cap, new stream connections are rejected with 503.
        var saturationThreshold = Math.Max(1, (int)Math.Ceiling(maxSessions * SaturationRatio));
        if (maxSessions > 0 && maximumPartitionSessions >= saturationThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"A feature-stream tenant or principal is near capacity ({maximumPartitionSessions.ToString(CultureInfo.InvariantCulture)} "
                + $"of {maxSessions.ToString(CultureInfo.InvariantCulture)}); new connections for that partition will be rejected once the cap is reached.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Feature-stream pipeline is healthy.", data));
    }
}

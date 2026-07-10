// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Surfaces real-time feature-stream backpressure and cross-node delivery health through
/// the <see cref="HealthCheckService"/> roll-up so operators see streaming degradation
/// before clients silently miss events. Added with the GA promotion (#2428); modeled on
/// the alert dispatch / outbox health checks. Reports:
/// <list type="bullet">
///   <item><description>
///     <b>Degraded</b> when active sessions approach the concurrent-session cap (new
///     connections are rejected with 503 once saturated);
///   </description></item>
///   <item><description>
///     <b>Degraded</b> when the cross-node broadcast backlog is buffering because Redis
///     publish is failing, or has shed payloads on overflow (cross-node live events may be
///     missed — they remain recoverable through the durable store + cross-node poll).
///   </description></item>
/// </list>
/// Never reports Unhealthy: streaming delivery is best-effort with durable replay, so its
/// degradation must not fail readiness. Reads only in-process counters (no I/O). Runs via
/// the IHealthCheck registry, not the <c>/healthz/ready</c> probe.
/// </summary>
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
        var maxSessions = _sessionManager.MaxConcurrentSessions;
        var slowConsumerDrops = _sessionManager.SlowConsumerDrops;
        var backlog = _sessionManager.GetClusterBroadcastBacklogSnapshot();

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["active_sessions"] = activeSessions,
            ["max_concurrent_sessions"] = maxSessions,
            ["slow_consumer_drops"] = slowConsumerDrops,
            ["cluster_broadcast_configured"] = backlog.Configured,
            ["cluster_broadcast_enabled"] = backlog.Enabled,
            ["cluster_broadcast_backlog"] = backlog.BacklogDepth,
            ["cluster_broadcast_dropped"] = backlog.Dropped,
        };

        // Cross-node broadcast is buffering (Redis publish unavailable) or has shed payloads:
        // cross-node subscribers may miss live events until the durable poll catches them up.
        if (backlog.Configured && (backlog.BacklogDepth > 0 || backlog.Dropped > 0 || !backlog.Enabled))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Feature-stream cross-node broadcast is degraded (Redis publish unavailable); "
                + $"{backlog.BacklogDepth.ToString(CultureInfo.InvariantCulture)} payloads buffered, "
                + $"{backlog.Dropped.ToString(CultureInfo.InvariantCulture)} dropped since startup. "
                + "Cross-node live events are recoverable via the durable store and cross-node poll.",
                data: data));
        }

        // Session saturation: at the cap, new stream connections are rejected with 503.
        var saturationThreshold = Math.Max(1, (int)Math.Ceiling(maxSessions * SaturationRatio));
        if (maxSessions > 0 && activeSessions >= saturationThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Feature-stream sessions are near capacity ({activeSessions.ToString(CultureInfo.InvariantCulture)} "
                + $"of {maxSessions.ToString(CultureInfo.InvariantCulture)}); new connections will be rejected once the cap is reached.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Feature-stream pipeline is healthy.", data));
    }
}

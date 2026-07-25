// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Internal;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit coverage for the GA-promotion (#2428) feature-stream health check: healthy under
/// normal load, degraded when sessions approach the concurrent cap, and never Unhealthy
/// (best-effort delivery with durable replay must not fail readiness).
/// </summary>
public sealed class FeatureStreamHealthCheckTests
{
    private static FeatureStreamSessionManager CreateManager(int maxConcurrentSessions)
        => new(
            Options.Create(new FeatureStreamOptions
            {
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                MaxBufferPerConnection = 4,
                MaxConcurrentSessions = maxConcurrentSessions,
                ReplayBatchSize = 100,
            }),
            NullLogger<FeatureStreamSessionManager>.Instance,
            TestTelemetry.CreateFeatureStreamMetrics());

    private static async Task<HealthCheckResult> CheckAsync(FeatureStreamSessionManager manager)
    {
        var check = new FeatureStreamHealthCheck(manager);
        return await check.CheckHealthAsync(new HealthCheckContext());
    }

    [UnitTest]
    public async Task CheckHealth_WithNoSessions_ReportsHealthy()
    {
        using var manager = CreateManager(maxConcurrentSessions: 10);

        var result = await CheckAsync(manager);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["active_sessions"].Should().Be(0);
        result.Data["max_concurrent_sessions"].Should().Be(10);
    }

    [UnitTest]
    public async Task CheckHealth_WithSingleSessionCapacityAndNoSessions_ReportsHealthy()
    {
        using var manager = CreateManager(maxConcurrentSessions: 1);

        var result = await CheckAsync(manager);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["active_sessions"].Should().Be(0);
    }

    [UnitTest]
    public async Task CheckHealth_WhenSessionsNearCap_ReportsDegraded()
    {
        using var manager = CreateManager(maxConcurrentSessions: 4);
        // 90% of 4 == 3.6, rounded up to 4: opening 4 sessions crosses the saturation ratio.
        var sessions = Enumerable.Range(0, 4)
            .Select(_ => manager.CreateSession("WebSocket", null))
            .ToList();

        try
        {
            var result = await CheckAsync(manager);

            result.Status.Should().Be(HealthStatus.Degraded);
            result.Data["active_sessions"].Should().Be(4);
        }
        finally
        {
            // A `using` declaration doesn't apply cleanly to a dynamically-sized List<T> of
            // disposables created via LINQ; disposing each item in a finally block is the
            // correct idiom here.
            DeferredDisposal.DisposeAll(sessions);
        }
    }

    [UnitTest]
    public async Task CheckHealth_AfterSessionsClose_RecoversToHealthy()
    {
        using var manager = CreateManager(maxConcurrentSessions: 4);
        var sessions = Enumerable.Range(0, 4)
            .Select(_ => manager.CreateSession("WebSocket", null))
            .ToList();
        foreach (var session in sessions)
        {
            session.Dispose();
        }

        var result = await CheckAsync(manager);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["active_sessions"].Should().Be(0);
    }

    [UnitTest]
    public async Task CheckHealth_WithoutRedis_ReportsClusterBroadcastUnconfigured()
    {
        using var manager = CreateManager(maxConcurrentSessions: 10);

        var result = await CheckAsync(manager);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["cluster_broadcast_configured"].Should().Be(false);
        result.Data["cluster_broadcast_backlog"].Should().Be(0);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertDispatchHealthCheckTests
{
    [UnitTest]
    public async Task CheckHealth_WhenDisabled_ReportsHealthy()
    {
        var health = new FakeDispatchHealth { IsDispatcherEnabled = false };
        var sut = Create(health);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [UnitTest]
    public async Task CheckHealth_WhenEnabledButNotRunning_ReportsUnhealthy()
    {
        var health = new FakeDispatchHealth { IsDispatcherEnabled = true, IsDispatcherRunning = false };
        var sut = Create(health);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [UnitTest]
    public async Task CheckHealth_WhenDeadLetteredAtThreshold_ReportsUnhealthy()
    {
        var health = new FakeDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastBacklog = new AlertDispatchBacklog { PendingCount = 0, DeadLetteredCount = 1 },
        };
        var sut = Create(health);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [UnitTest]
    public async Task CheckHealth_WhenBacklogExceedsThreshold_ReportsDegraded()
    {
        var health = new FakeDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastBacklog = new AlertDispatchBacklog { PendingCount = 5, DeadLetteredCount = 0 },
        };
        var sut = Create(health, degradedBacklogThreshold: 5, unhealthyDeadLetterThreshold: 1);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [UnitTest]
    public async Task CheckHealth_WhenBacklogHealthy_ReportsHealthy()
    {
        var health = new FakeDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastPollAt = DateTimeOffset.UtcNow,
            LastBacklog = new AlertDispatchBacklog { PendingCount = 3, DeadLetteredCount = 0 },
        };
        var sut = Create(health, degradedBacklogThreshold: 1_000, unhealthyDeadLetterThreshold: 1);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [UnitTest]
    public void Evaluate_WhenHeartbeatStale_ReportsUnhealthy()
    {
        // Running dispatcher whose last poll aged well past the staleness threshold: hung loop.
        var now = new DateTimeOffset(2026, 07, 12, 12, 0, 0, TimeSpan.Zero);
        var health = new FakeDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastPollAt = now - TimeSpan.FromMinutes(10),
            LastBacklog = new AlertDispatchBacklog { PendingCount = 0, DeadLetteredCount = 0 },
        };
        var sut = Create(health);

        var result = sut.Evaluate(now);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("stale");
    }

    [UnitTest]
    public void Evaluate_WhenHeartbeatFresh_ReportsHealthy()
    {
        var now = new DateTimeOffset(2026, 07, 12, 12, 0, 0, TimeSpan.Zero);
        var health = new FakeDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastPollAt = now - TimeSpan.FromSeconds(3),
            LastBacklog = new AlertDispatchBacklog { PendingCount = 0, DeadLetteredCount = 0 },
        };

        Create(health).Evaluate(now).Status.Should().Be(HealthStatus.Healthy);
    }

    [UnitTest]
    public void Evaluate_WhenNeverPolledYet_IsNotStale()
    {
        // Fresh start: LastPollAt null must not be treated as stale (no false depool at boot).
        var now = new DateTimeOffset(2026, 07, 12, 12, 0, 0, TimeSpan.Zero);
        var health = new FakeDispatchHealth
        {
            IsDispatcherEnabled = true,
            IsDispatcherRunning = true,
            LastPollAt = null,
        };

        Create(health).Evaluate(now).Status.Should().Be(HealthStatus.Healthy);
    }

    private static AlertDispatchHealthCheck Create(
        FakeDispatchHealth health,
        int degradedBacklogThreshold = 1_000,
        int unhealthyDeadLetterThreshold = 1)
    {
        var options = Options.Create(new AlertOptions
        {
            Dispatch = new AlertDispatchOptions
            {
                DegradedBacklogThreshold = degradedBacklogThreshold,
                UnhealthyDeadLetterThreshold = unhealthyDeadLetterThreshold,
            },
        });

        return new AlertDispatchHealthCheck(health, options);
    }

    private sealed class FakeDispatchHealth : IAlertDispatchHealth
    {
        public bool IsDispatcherRunning { get; init; }
        public bool IsDispatcherEnabled { get; init; }
        public DateTimeOffset? LastPollAt { get; init; }
        public AlertDispatchBacklog? LastBacklog { get; init; }
        public bool IsStoragePollFailing { get; init; }

        public Task<AlertDispatchBacklog> RefreshBacklogAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(LastBacklog ?? new AlertDispatchBacklog { PendingCount = 0, DeadLetteredCount = 0 });
    }
}

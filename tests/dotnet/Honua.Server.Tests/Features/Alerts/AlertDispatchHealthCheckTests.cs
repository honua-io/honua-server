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
            LastBacklog = new AlertDispatchBacklog { PendingCount = 3, DeadLetteredCount = 0 },
        };
        var sut = Create(health, degradedBacklogThreshold: 1_000, unhealthyDeadLetterThreshold: 1);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
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
    }
}

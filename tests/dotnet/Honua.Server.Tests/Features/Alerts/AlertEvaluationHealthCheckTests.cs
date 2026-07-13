// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AlertEvaluationHealthCheckTests
{
    [UnitTest]
    public async Task CheckHealth_WhenDisabled_ReportsHealthy()
    {
        var health = new FakeEvaluationHealth { IsEvaluatorEnabled = false };
        var sut = Create(health);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [UnitTest]
    public async Task CheckHealth_WhenEnabledButNotRunning_ReportsUnhealthy()
    {
        var health = new FakeEvaluationHealth { IsEvaluatorEnabled = true, IsEvaluatorRunning = false };
        var sut = Create(health);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [UnitTest]
    public async Task CheckHealth_WhenHeartbeatFresh_ReportsHealthy()
    {
        // A live loop stamps its heartbeat every iteration (leader or not); a fresh heartbeat is healthy.
        var now = DateTimeOffset.UtcNow;
        var health = new FakeEvaluationHealth
        {
            IsEvaluatorEnabled = true,
            IsEvaluatorRunning = true,
            LastPollAt = now - TimeSpan.FromSeconds(1),
        };
        var sut = Create(health, timeProvider: new FixedTimeProvider(now));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [UnitTest]
    public async Task CheckHealth_WhenHeartbeatStale_ReportsUnhealthy()
    {
        // #2810: a loop hung inside a pass keeps IsEvaluatorRunning true while LastPollAt ages;
        // the check must compare the heartbeat to now and fault rather than report Healthy.
        var now = DateTimeOffset.UtcNow;
        var health = new FakeEvaluationHealth
        {
            IsEvaluatorEnabled = true,
            IsEvaluatorRunning = true,
            LastPollAt = now - TimeSpan.FromMinutes(10),
        };
        var sut = Create(health, timeProvider: new FixedTimeProvider(now));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("heartbeat is stale");
    }

    private static AlertEvaluationHealthCheck Create(
        FakeEvaluationHealth health,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new AlertOptions());
        return new AlertEvaluationHealthCheck(health, options, timeProvider ?? TimeProvider.System);
    }

    private sealed class FakeEvaluationHealth : IAlertEvaluationHealth
    {
        public bool IsEvaluatorRunning { get; init; }
        public bool IsEvaluatorEnabled { get; init; }
        public bool IsLeader { get; init; }
        public DateTimeOffset? LastPollAt { get; init; }
        public DateTimeOffset? RunningSince { get; init; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

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
    private static readonly DateTimeOffset Now = new(2026, 07, 12, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void Evaluate_WhenDisabled_ReportsHealthy()
    {
        var sut = Create(new FakeEvaluationHealth { IsEvaluatorEnabled = false });

        sut.Evaluate(Now).Status.Should().Be(HealthStatus.Healthy);
    }

    [UnitTest]
    public void Evaluate_WhenEnabledButNotRunning_ReportsUnhealthy()
    {
        var sut = Create(new FakeEvaluationHealth { IsEvaluatorEnabled = true, IsEvaluatorRunning = false });

        sut.Evaluate(Now).Status.Should().Be(HealthStatus.Unhealthy);
    }

    [UnitTest]
    public void Evaluate_WhenLeadershipAcquisitionFailingBeyondThreshold_ReportsNoLeaderUnhealthy()
    {
        var health = new FakeEvaluationHealth
        {
            IsEvaluatorEnabled = true,
            IsEvaluatorRunning = true,
            IsLeader = false,
            // Failing for 5 minutes — beyond the 2-minute no-leader threshold.
            LeaderAcquisitionFailingSince = Now - TimeSpan.FromMinutes(5),
        };
        var sut = Create(health);

        var result = sut.Evaluate(Now);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("no leader");
    }

    [UnitTest]
    public void Evaluate_WhenHealthyFollowerWithNoRecentAcquisitionFault_ReportsHealthy()
    {
        // A follower cleanly conceded leadership (no acquisition fault) — never a no-leader stall.
        var health = new FakeEvaluationHealth
        {
            IsEvaluatorEnabled = true,
            IsEvaluatorRunning = true,
            IsLeader = false,
            LeaderAcquisitionFailingSince = null,
            LastHeartbeatAt = Now,
        };

        Create(health).Evaluate(Now).Status.Should().Be(HealthStatus.Healthy);
    }

    [UnitTest]
    public void Evaluate_WhenLeaderHeartbeatStale_ReportsHungLeaderUnhealthy()
    {
        var health = new FakeEvaluationHealth
        {
            IsEvaluatorEnabled = true,
            IsEvaluatorRunning = true,
            IsLeader = true,
            // Last productive pass 5 minutes ago — beyond the 2-minute staleness threshold.
            LastLeaderPassAt = Now - TimeSpan.FromMinutes(5),
            LastHeartbeatAt = Now - TimeSpan.FromMinutes(5),
        };
        var sut = Create(health);

        var result = sut.Evaluate(Now);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("hung");
    }

    [UnitTest]
    public void Evaluate_WhenLeaderHeartbeatFresh_ReportsHealthy()
    {
        var health = new FakeEvaluationHealth
        {
            IsEvaluatorEnabled = true,
            IsEvaluatorRunning = true,
            IsLeader = true,
            LastLeaderPassAt = Now - TimeSpan.FromSeconds(3),
            LastHeartbeatAt = Now,
        };

        Create(health).Evaluate(Now).Status.Should().Be(HealthStatus.Healthy);
    }

    private static AlertEvaluationHealthCheck Create(FakeEvaluationHealth health)
    {
        var options = Options.Create(new AlertOptions
        {
            Evaluation = new AlertEvaluationOptions
            {
                HeartbeatStalenessThreshold = TimeSpan.FromMinutes(2),
                NoLeaderThreshold = TimeSpan.FromMinutes(2),
            },
        });

        return new AlertEvaluationHealthCheck(health, options);
    }

    private sealed class FakeEvaluationHealth : IAlertEvaluationHealth
    {
        public bool IsEvaluatorEnabled { get; init; }
        public bool IsEvaluatorRunning { get; init; }
        public bool IsLeader { get; init; }
        public DateTimeOffset? LastHeartbeatAt { get; init; }
        public DateTimeOffset? LastLeaderPassAt { get; init; }
        public DateTimeOffset? LeaderAcquisitionFailingSince { get; init; }
    }
}

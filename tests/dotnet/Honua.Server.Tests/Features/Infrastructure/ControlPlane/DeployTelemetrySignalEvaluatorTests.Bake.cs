// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class DeployTelemetrySignalEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_DuringBakeHealthFails_RecommendsRollbackWithoutGoldenQuery()
    {
        var (decision, probe, queries) = await EvaluateBakeAsync(
            new DeployHealthProbeResult { Attempts = 3, Failures = 3 },
            new DeployGoldenQueryResult { Matched = false, Detail = "body expectation" });

        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        probe.Invocations.Should().Be(1);
        probe.GoldenInvocations.Should().Be(0);
        queries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_DuringBakeGoldenQueryMismatches_RecommendsRollback()
    {
        var (decision, probe, queries) = await EvaluateBakeAsync(
            new DeployHealthProbeResult { Attempts = 3, Failures = 0 },
            new DeployGoldenQueryResult { Matched = false, Detail = "body expectation" });

        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        probe.Invocations.Should().Be(1);
        probe.GoldenInvocations.Should().Be(1);
        queries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_DuringBakeHealthy_ProbesBeforeHoldingPromotion()
    {
        var (decision, probe, queries) = await EvaluateBakeAsync(
            new DeployHealthProbeResult { Attempts = 3, Failures = 0 },
            new DeployGoldenQueryResult { Matched = true, Detail = "body expectation" });

        decision!.RollbackRecommended.Should().BeFalse();
        decision.WaitForMoreTelemetry.Should().BeTrue();
        probe.Invocations.Should().Be(1);
        probe.GoldenInvocations.Should().Be(1);
        queries.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAsync_DuringBakeFirstBreach_WaitsForDebounce()
    {
        var (decision, probe) = await EvaluateBakeBreachAsync("0");

        decision!.RollbackRecommended.Should().BeFalse();
        decision.WaitForMoreTelemetry.Should().BeTrue();
        decision.UpdatedDeployParameters!["telemetry.rollback.breach_streak"].Should().Be("1");
        probe.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task EvaluateAsync_DuringBakeRepeatedBreach_RecommendsRollback()
    {
        var (decision, probe) = await EvaluateBakeBreachAsync("1");

        decision!.RollbackRecommended.Should().BeTrue();
        decision.WaitForMoreTelemetry.Should().BeFalse();
        decision.UpdatedDeployParameters!["telemetry.rollback.breach_streak"].Should().Be("0");
        probe.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task EvaluateAsync_DuringBake_HealthyProbesResetBreachStreakWithoutPromoting()
    {
        var probe = new FakeHealthProbe(
            new DeployHealthProbeResult { Attempts = 3, Failures = 0 },
            new DeployGoldenQueryResult { Matched = true });
        var evaluator = CreateHealthProbeEvaluator(probe);

        var decision = await evaluator.EvaluateAsync(WithBreachStreak(CreateBakeOperation(), "1"));

        decision!.RollbackRecommended.Should().BeFalse();
        decision.WaitForMoreTelemetry.Should().BeTrue();
        decision.UpdatedDeployParameters!["telemetry.rollback.breach_streak"].Should().Be("0");
        probe.Invocations.Should().Be(1);
        probe.GoldenInvocations.Should().Be(1);
    }

    private static async Task<(DeployTelemetryDecision? Decision, FakeHealthProbe Probe, ConcurrentQueue<string> Queries)>
        EvaluateBakeAsync(DeployHealthProbeResult health, DeployGoldenQueryResult golden)
    {
        var queries = new ConcurrentQueue<string>();
        var probe = new FakeHealthProbe(health, golden);
        var evaluator = CreateEvaluator(queries, healthProbe: probe);
        var decision = await evaluator.EvaluateAsync(CreateBakeOperation());
        return (decision, probe, queries);
    }

    private static async Task<(DeployTelemetryDecision? Decision, FakeHealthProbe Probe)> EvaluateBakeBreachAsync(
        string priorStreak)
    {
        var probe = new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 3 });
        var evaluator = CreateHealthProbeEvaluator(probe);
        var decision = await evaluator.EvaluateAsync(WithBreachStreak(CreateBakeOperation(), priorStreak));
        return (decision, probe);
    }

    private static WorkflowOperationRecord WithBreachStreak(WorkflowOperationRecord operation, string priorStreak)
    {
        var parameters = new Dictionary<string, string>(operation.Deploy!.Parameters)
        {
            ["telemetry.rollback.consecutive_breaches"] = "2",
            ["telemetry.rollback.breach_streak"] = priorStreak
        };
        return operation with { Deploy = operation.Deploy with { Parameters = parameters } };
    }

    private static WorkflowOperationRecord CreateBakeOperation()
    {
        var operation = CreateOperation(DeployTargetKind.AwsEcs, new Dictionary<string, string>
        {
            ["telemetry.connection"] = "prod-prom",
            ["telemetry.healthz.url"] = "https://example.com/healthz/ready",
            ["telemetry.golden_query.url"] = "https://example.com/query",
            ["telemetry.golden_query.expected_contains"] = "features",
            ["telemetry.warmup_seconds"] = "300"
        }, createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        return operation with { Deploy = operation.Deploy! with { TrafficExposedAt = DateTimeOffset.UtcNow } };
    }
}

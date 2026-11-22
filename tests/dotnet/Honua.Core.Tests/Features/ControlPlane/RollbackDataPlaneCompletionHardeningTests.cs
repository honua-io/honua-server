// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.ControlPlane;

[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class RollbackDataPlaneCompletionHardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void ReadFunctionalExpectation_PrefersRollbackParametersOverGoldenQueryParameters()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RollbackDataPlaneCompletion.FunctionalQueryUrlParameterKey] = "https://service.example/query",
            [RollbackDataPlaneCompletion.FunctionalQueryExpectedContainsParameterKey] = "candidate-marker",
            [RollbackDataPlaneCompletion.FunctionalQueryForbiddenContainsParameterKey] = "old-forbidden",
            [RollbackDataPlaneCompletion.FunctionalQueryExpectedSha256ParameterKey] = "candidate-sha",
            [RollbackDataPlaneCompletion.LocalFunctionalQueryExpectedContainsParameterKey] = "prior-marker",
            [RollbackDataPlaneCompletion.LocalFunctionalQueryForbiddenContainsParameterKey] = "candidate-forbidden",
            [RollbackDataPlaneCompletion.LocalFunctionalQueryExpectedSha256ParameterKey] = "prior-sha",
        };

        var expectation = RollbackDataPlaneCompletion.ReadFunctionalExpectation(parameters);

        expectation.ExpectedContains.Should().Be("prior-marker");
        expectation.ForbiddenContains.Should().Be("candidate-forbidden");
        expectation.ExpectedSha256.Should().Be("prior-sha");
    }

    [UnitTest]
    public async Task ProbeFunctionalQueryAsync_WhenNoUrlConfigured_ReturnsNotConfigured()
    {
        var verdict = await RollbackDataPlaneCompletion.ProbeFunctionalQueryAsync(
            probe: null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            CancellationToken.None);

        verdict.Should().Be(RollbackFunctionalQueryVerdict.NotConfigured);
    }

    [UnitTest]
    public void Evaluate_WhenFunctionalQueryNotConfiguredAndIdentityAndReadinessProven_CompletesRollback()
    {
        var decision = RollbackDataPlaneCompletion.Evaluate(
            new RollbackDataPlaneEvidence
            {
                RoutingConverged = true,
                PriorRevisionIdentityProven = true,
                ServingRevision = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua:41",
                HealthyEndpointCount = 2,
                RegisteredEndpointCount = 2,
                FunctionalQuery = RollbackFunctionalQueryVerdict.NotConfigured,
                Window = TimeSpan.FromMinutes(5),
            },
            Now);

        decision.Disposition.Should().Be(RollbackDataPlaneDisposition.RolledBack);
        decision.ReasonCode.Should().Be(RollbackDataPlaneCompletion.ReasonRolledBack);
        RollbackDataPlaneCompletion.ToStatus(decision.Disposition).Should().Be(WorkflowOperationStatus.RolledBack);
    }

    [UnitTest]
    public void Evaluate_WhenFunctionalQueryNotProven_StillAwaitsDataPlane()
    {
        var decision = RollbackDataPlaneCompletion.Evaluate(
            new RollbackDataPlaneEvidence
            {
                RoutingConverged = true,
                PriorRevisionIdentityProven = true,
                ServingRevision = "arn:aws:ecs:us-east-1:123456789012:task-definition/honua:41",
                HealthyEndpointCount = 2,
                RegisteredEndpointCount = 2,
                FunctionalQuery = RollbackFunctionalQueryVerdict.NotProven,
                Window = TimeSpan.FromMinutes(5),
            },
            Now);

        decision.Disposition.Should().Be(RollbackDataPlaneDisposition.AwaitingDataPlane);
    }
}

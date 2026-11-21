// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class DeployWorkflowServiceTests
{
    [UnitTheory]
    [InlineData("aws.ecs.cluster")]
    [InlineData("aws.ecs.canary_service")]
    [InlineData("aws.region")]
    [InlineData("aws.ecs.migration_task_definition")]
    [InlineData("lambda.function_name")]
    [InlineData("kubernetes.namespace")]
    [InlineData("compose.yaml")]
    [InlineData("target.resource_id")]
    [InlineData("AWS.ecs.cluster")]
    public async Task PlanAsync_OverrideChangesTargetInfrastructure_BlocksSubmission(string key)
    {
        var service = CreateService(new TestWorkflowOperationStore(), new ImmediateDeployBackend());

        var plan = await service.PlanAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            parameterOverrides: new Dictionary<string, string> { [key] = "elsewhere" });

        plan!.Plan.IsReadyToSubmit.Should().BeFalse();
        plan.Plan.BlockingReasons.Should().Contain(reason =>
            reason.StartsWith(DeployWorkflowService.TargetIdentityOverrideMessage, StringComparison.Ordinal) &&
            reason.Contains(key, StringComparison.Ordinal));
    }

    [UnitTheory]
    [InlineData("deployment.canary_weight_percentage")]
    [InlineData("aws.ecs.canary_weight_percentage")]
    [InlineData("kubernetes.argo.canary_weight_percentage")]
    [InlineData("aws.ecs.steady_attempts")]
    [InlineData("aws.ecs.steady_delay_ms")]
    public async Task PlanAsync_RolloutTuningOverride_IsNotTreatedAsInfrastructure(string key)
    {
        var service = CreateService(new TestWorkflowOperationStore(), new ImmediateDeployBackend());

        var plan = await service.PlanAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            parameterOverrides: new Dictionary<string, string> { [key] = "10" });

        plan!.Plan.BlockingReasons.Should().NotContain(reason =>
            reason.StartsWith(DeployWorkflowService.TargetIdentityOverrideMessage, StringComparison.Ordinal));
    }
}

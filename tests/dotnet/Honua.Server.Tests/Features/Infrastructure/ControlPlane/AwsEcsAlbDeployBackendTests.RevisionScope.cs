// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class AwsEcsAlbDeployBackendTests
{
    private const string OtherFamilyTaskDefArn = "arn:aws:ecs:us-east-1:123456789012:task-definition/batch-worker:7";
    private const string OtherAccountTaskDefArn = "arn:aws:ecs:us-east-1:210987654321:task-definition/honua-app:42";
    private const string OtherRegionTaskDefArn = "arn:aws:ecs:eu-west-1:123456789012:task-definition/honua-app:42";

    [UnitTheory]
    [InlineData(OtherFamilyTaskDefArn)]
    [InlineData(OtherAccountTaskDefArn)]
    [InlineData(OtherRegionTaskDefArn)]
    public async Task StartAsync_RevisionOutsideServiceFamily_FailsBeforeAnyChange(string desiredRevision)
    {
        var albClient = new StubAwsAlbClient();
        var ecsClient = new StubAwsEcsClient();
        var backend = CreateBackend(albClient, ecsClient);

        var result = await backend.StartAsync(CreateOperation(desiredRevision: desiredRevision));

        result.Status.Should().Be(WorkflowOperationStatus.Failed);
        result.Message.Should().Be(AwsEcsAlbDeployBackend.RevisionOutsideServiceFamilyMessage);
        ecsClient.LastUpdateTaskDefinitionArn.Should().BeNull();
        albClient.LastWeights.Should().BeNull();
    }

    [UnitTest]
    public async Task StartAsync_EarlierRevisionOfServiceFamily_IsSubmitted()
    {
        var ecsClient = new StubAwsEcsClient();
        var backend = CreateBackend(ecsClient: ecsClient);

        var result = await backend.StartAsync(CreateOperation(desiredRevision: PreviousTaskDefArn));

        result.Status.Should().Be(WorkflowOperationStatus.Submitted);
        ecsClient.LastUpdateTaskDefinitionArn.Should().Be(PreviousTaskDefArn);
    }

    [UnitTest]
    public async Task PromoteAsync_RevisionOutsideServiceFamily_FailsBeforeAnyChange()
    {
        var albClient = new StubAwsAlbClient();
        var ecsClient = new StubAwsEcsClient();
        var backend = CreateBackend(albClient, ecsClient);

        var observation = await backend.PromoteAsync(
            CreateOperation(desiredRevision: OtherFamilyTaskDefArn, currentRevision: PreviousTaskDefArn, parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Failed);
        observation.Message.Should().Be(AwsEcsAlbDeployBackend.RevisionOutsideServiceFamilyMessage);
        ecsClient.LastUpdateTaskDefinitionArn.Should().BeNull();
        albClient.LastWeights.Should().BeNull();
    }

    [UnitTest]
    public async Task PromoteAsync_EarlierRevisionOfServiceFamily_Succeeds()
    {
        var ecsClient = new StubAwsEcsClient();
        var backend = CreateBackend(ecsClient: ecsClient);

        var observation = await backend.PromoteAsync(
            CreateOperation(desiredRevision: PreviousTaskDefArn, currentRevision: TaskDefArn, parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        ecsClient.LastUpdateTaskDefinitionArn.Should().Be(PreviousTaskDefArn);
    }

    [UnitTest]
    public async Task PromoteAsync_TaskDefinitionRollout_UpdatesServiceToDesiredRevisionNotCurrentRevision()
    {
        var ecsClient = new StubAwsEcsClient();
        var backend = CreateBackend(ecsClient: ecsClient);

        var observation = await backend.PromoteAsync(
            CreateOperation(desiredRevision: TaskDefArn, currentRevision: PreviousTaskDefArn, parameters: CanaryParameters()));

        observation.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        ecsClient.LastUpdateTaskDefinitionArn.Should().Be(TaskDefArn);
    }

    [UnitTheory]
    [InlineData("honua-app:42")]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:task-definition/honua-app")]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:task-definition/honua-app:latest")]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:service/honua-prod-ecs/honua-app")]
    public async Task PlanAsync_RevisionNotAFullTaskDefinitionArn_HasBlockingReason(string desiredRevision)
    {
        var backend = CreateBackend();

        var plan = await backend.PlanAsync(CreateSpec(desiredRevision: desiredRevision));

        plan.IsReadyToSubmit.Should().BeFalse();
        plan.BlockingReasons.Should().Contain(AwsEcsAlbDeployBackend.RevisionFormatMessage);
    }

    [UnitTest]
    public void AreSameFamily_EarlierRevisionOfSameFamily_IsTrue()
        => EcsTaskDefinitionRevision.AreSameFamily(PreviousTaskDefArn, TaskDefArn).Should().BeTrue();

    [UnitTheory]
    [InlineData(OtherFamilyTaskDefArn, TaskDefArn)]
    [InlineData(OtherAccountTaskDefArn, TaskDefArn)]
    [InlineData(OtherRegionTaskDefArn, TaskDefArn)]
    [InlineData(TaskDefArn, null)]
    [InlineData("arn:aws:ecs:us-east-1:123456789012:task-definition/honua-app-2:1", TaskDefArn)]
    public void AreSameFamily_DifferentFamilyAccountOrRegion_IsFalse(string candidate, string? reference)
        => EcsTaskDefinitionRevision.AreSameFamily(candidate, reference).Should().BeFalse();
}

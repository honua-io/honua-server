// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed partial class DeploymentSettledCdnConsumerTests(DatabaseFixtureAdapter fixture)
{
    [IntegrationTest]
    public Task ConcurrentHostsAndDuplicateCompletion_ProduceOnePurge()
        => InSchemaAsync(async database =>
        {
            var ecs = Ecs(Settled());
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            // A host that finds the service locked returns the event to the queue; its redelivery is a no-op.
            var outcomes = await Task.WhenAll(HandleOrRequeueAsync(Host(database, ecs, cdn)),
                HandleOrRequeueAsync(Host(database, ecs, cdn)));
            outcomes.Should().Contain(true);
            await Host(database, ecs, cdn).HandleAsync(Event(), default);
            await Host(database, ecs, cdn).HandleAsync(Event("duplicate"), default);
            (await CountAsync(database)).Should().Be(1);
            await cdn.Received(1).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
            await ecs.Received(1).DescribeServiceAsync(Cluster, "server", "us-west-2", Arg.Any<CancellationToken>());
        });

    [IntegrationTest]
    public Task FailedPurge_RemainsUnrecordedAndRetriesOnAnotherHost()
        => InSchemaAsync(async database =>
        {
            var ecs = Ecs(Settled());
            var cdn = Cdn(CdnInvalidationOutcome.Failed, CdnInvalidationOutcome.Submitted);
            var first = () => Host(database, ecs, cdn).HandleAsync(Event(), default);
            await first.Should().ThrowAsync<InvalidOperationException>();
            (await CountAsync(database)).Should().Be(0);
            await Host(database, ecs, cdn).HandleAsync(Event(), default);
            (await CountAsync(database)).Should().Be(1);
            await cdn.Received(2).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        });

    [IntegrationTest]
    public Task CompletionWithOldTasksStillRunning_WaitsForDrain()
        => InSchemaAsync(async database =>
        {
            var ready = Settled();
            var ecs = Ecs(ready with
            {
                Deployments = [ready.Deployments[0],
                new AwsEcsDeploymentState { Status = "ACTIVE", DeploymentId = "old", RunningCount = 1 }]
            });
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            var handle = () => Host(database, ecs, cdn).HandleAsync(Event(), default);
            await handle.Should().ThrowAsync<InvalidOperationException>().WithMessage("*drain*");
            (await CountAsync(database)).Should().Be(0);
            await cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
            ecs.DescribeServiceAsync(default!, default!, default, default).ReturnsForAnyArgs(ready);
            await Host(database, ecs, cdn).HandleAsync(Event(), default);
            (await CountAsync(database)).Should().Be(1);
        });

    [IntegrationTest]
    public Task StaleAndUnrelatedEvents_DoNotPurgeOrRecord()
        => InSchemaAsync(async database =>
        {
            var ready = Settled();
            var ecs = Ecs(ready with
            {
                Deployments = [ready.Deployments[0],
                new AwsEcsDeploymentState { DeploymentId = "old", Status = "ACTIVE" }]
            });
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            var host = Host(database, ecs, cdn);
            await host.HandleAsync(Event(deployment: "old"), default);
            await host.HandleAsync(Event(service: "other"), default);
            await host.HandleAsync(Event(eventName: "SERVICE_DEPLOYMENT_FAILED"), default);
            (await CountAsync(database)).Should().Be(0);
            await cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
            await ecs.Received(1).DescribeServiceAsync(Cluster, "server", "us-west-2", Arg.Any<CancellationToken>());
        });

    [IntegrationTest]
    public Task OtherRegionClusterOrService_DoNotContactEcs()
        => InSchemaAsync(async database =>
        {
            var ecs = Ecs(Settled());
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            await Host(database, ecs, cdn, target => target.Parameters["aws.region"] = "us-east-1")
                .HandleAsync(Event(), default);
            await Host(database, ecs, cdn, target => target.Parameters["aws.ecs.cluster"] = "other")
                .HandleAsync(Event(), default);
            await Host(database, ecs, cdn, target => target.Parameters["aws.ecs.canary_service"] = "canary")
                .HandleAsync(Event(), default);
            await Host(database, ecs, cdn, target => target.ParameterEntries.Add(new() { Key = "aws.ecs.cluster", Value = "other" }))
                .HandleAsync(Event(), default);
            (await CountAsync(database)).Should().Be(0);
            await ecs.DidNotReceiveWithAnyArgs().DescribeServiceAsync(default!, default!, default, default);
            await cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
        });

    [IntegrationTheory]
    [InlineData("blank-canary")]
    [InlineData("trimmed-cluster")]
    [InlineData("entry-overrides-parameter")]
    [InlineData("no-region")]
    public Task TargetParameters_AreReadAsTheDeployBackendReadsThem(string shape)
        => InSchemaAsync(async database =>
        {
            Action<DeployTargetOptions> configure = shape switch
            {
                "blank-canary" => target => target.Parameters["aws.ecs.canary_service"] = " ",
                "trimmed-cluster" => target => target.Parameters["aws.ecs.cluster"] = $" {Cluster} ",
                "entry-overrides-parameter" => target =>
                {
                    target.Parameters["aws.ecs.cluster"] = "staging";
                    target.ParameterEntries.Add(new() { Key = "aws.ecs.cluster", Value = Cluster });
                }
                ,
                _ => target => target.Parameters.Remove("aws.region"),
            };
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            await Host(database, Ecs(Settled()), cdn, configure).HandleAsync(Event(), default);
            (await CountAsync(database)).Should().Be(1);
            await cdn.Received(1).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        });

    [IntegrationTest]
    public Task LateEventForDeploymentEcsNoLongerLists_IsCoveredOnceTheCurrentPrimaryPurged()
        => InSchemaAsync(async database =>
        {
            var ready = Settled();
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            var late = () => Host(database, Ecs(ready), cdn).HandleAsync(Event("late", deployment: "ecs-svc/older"), default);
            await late.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not yet visible*");
            await Host(database, Ecs(ready), cdn).HandleAsync(Event(), default);
            await late();
            (await CountAsync(database)).Should().Be(1);
            await cdn.Received(1).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        });

    [IntegrationTest]
    public Task IncompleteRolloutOrMissingTaskDefinition_RemainsRetryable()
        => InSchemaAsync(async database =>
        {
            var ready = Settled();
            var states = new[] { ready with { PendingCount = 1 }, ready with { RunningCount = 1 },
                ready with { TaskDefinitionArn = "wrong" }, ready with { Status = "DRAINING" },
                ready with { Deployments = [] },
                ready with { Deployments = [ready.Deployments[0] with { DeploymentId = "not-yet-visible" }] },
                ready with { Deployments = [ready.Deployments[0] with { RolloutState = "IN_PROGRESS" }] },
                ready with { TaskDefinitionArn = " ", Deployments = [ready.Deployments[0] with { TaskDefinitionArn = " " }] },
                ready with { TaskDefinitionArn = null, Deployments = [ready.Deployments[0] with { TaskDefinitionArn = null }] } };
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            foreach (var state in states)
            {
                var handle = () => Host(database, Ecs(state), cdn).HandleAsync(Event(), default);
                await handle.Should().ThrowAsync<InvalidOperationException>();
            }

            (await CountAsync(database)).Should().Be(0);
            await cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
        });

    private static async Task<bool> HandleOrRequeueAsync(DeploymentSettledCdnConsumer host)
    {
        try
        {
            await host.HandleAsync(Event(), default);
            return true;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Another task", StringComparison.Ordinal))
        {
            return false;
        }
    }
}
#endif

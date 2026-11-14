// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Collection("Database")]
public sealed partial class DeploymentSettledCdnConsumerTests(DatabaseFixtureAdapter fixture)
{
    [IntegrationTest]
    public Task ConcurrentHostsAndDuplicateCompletion_ProduceOnePurge()
        => InSchemaAsync(async database =>
        {
            var ecs = Ecs(Settled());
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            await Task.WhenAll(Host(database, ecs, cdn).HandleAsync(Event(), default),
                Host(database, ecs, cdn).HandleAsync(Event(), default));
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
            var ecs = Ecs(ready with { Deployments = [ready.Deployments[0],
                new AwsEcsDeploymentState { Status = "ACTIVE", DeploymentId = "old", RunningCount = 1 }] });
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
            var ecs = Ecs(ready with { Deployments = [ready.Deployments[0],
                new AwsEcsDeploymentState { DeploymentId = "old", Status = "ACTIVE" }] });
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
    public Task UnconfiguredRegionClusterAndBlankCanary_DoNotContactEcs()
        => InSchemaAsync(async database =>
        {
            var ecs = Ecs(Settled());
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            await Host(database, ecs, cdn, target => target.Parameters["aws.region"] = "us-east-1")
                .HandleAsync(Event(), default);
            await Host(database, ecs, cdn, target => target.Parameters["aws.ecs.cluster"] = "other")
                .HandleAsync(Event(), default);
            await Host(database, ecs, cdn, target => target.Parameters["aws.ecs.canary_service"] = " ")
                .HandleAsync(Event(), default);
            (await CountAsync(database)).Should().Be(0);
            await ecs.DidNotReceiveWithAnyArgs().DescribeServiceAsync(default!, default!, default, default);
            await cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
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
                ready with { Deployments = [ready.Deployments[0] with { RolloutState = "IN_PROGRESS" }] } };
            var cdn = Cdn(CdnInvalidationOutcome.Submitted);
            foreach (var state in states)
            {
                var handle = () => Host(database, Ecs(state), cdn).HandleAsync(Event(), default);
                await handle.Should().ThrowAsync<InvalidOperationException>();
            }

            (await CountAsync(database)).Should().Be(0);
            await cdn.DidNotReceiveWithAnyArgs().InvalidateAsync(default!, default);
        });
}
#endif

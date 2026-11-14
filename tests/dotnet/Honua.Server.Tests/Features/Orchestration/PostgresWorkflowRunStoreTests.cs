// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Orchestration;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresWorkflowRunStoreTests
{
    [IntegrationTest]
    public async Task TryCreateAsync_Twice_SecondIsRefused()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<IWorkflowRunStore>();
        var run = Run();

        (await store.TryCreateAsync(run)).Should().BeTrue();
        (await store.TryCreateAsync(run with { ErrorMessage = "replacement" })).Should().BeFalse();
        (await store.GetAsync(run.RunId)).Should().BeEquivalentTo(run);
    }

    [IntegrationTest]
    public async Task GetAsync_AfterWritingHostStops_ReadsTheDurableRun()
    {
        var run = Run();
        await using (var writer = CreateHost())
        {
            await writer.InitializeAsync();
            (await writer.GetService<IWorkflowRunStore>().TryCreateAsync(run)).Should().BeTrue();
        }

        await using var reader = CreateHost();
        await reader.InitializeAsync();
        (await reader.GetService<IWorkflowRunStore>().GetAsync(run.RunId)).Should().BeEquivalentTo(run);
    }

    [IntegrationTest]
    public async Task ListActiveAsync_CancelledRunWithRunningChild_RemainsUntilChildFinishes()
    {
        await using var host = CreateHost();
        await host.InitializeAsync();
        var store = host.GetService<IWorkflowRunStore>();
        var run = Run() with { Status = WorkflowRunStatus.Cancelled };
        await store.SetAsync(run);

        (await store.ListActiveAsync()).Should().Contain(candidate => candidate.RunId == run.RunId);
        var finished = run with
        {
            StepStates = [run.StepStates[0] with { Status = WorkflowStepStatus.Cancelled }]
        };
        await store.SetAsync(finished);

        (await store.ListActiveAsync()).Should().NotContain(candidate => candidate.RunId == run.RunId);
        (await store.ListByWorkflowAsync(run.WorkflowId)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(finished);
    }

    [IntegrationTest]
    public async Task TryAcquireLeaseAsync_CompetingNodes_EnforcesOwnerOnRenewAndRelease()
    {
        await using var first = CreateHost();
        await using var second = CreateHost();
        await first.InitializeAsync();
        await second.InitializeAsync();
        var owner = first.GetService<IWorkflowRunStore>();
        var competitor = second.GetService<IWorkflowRunStore>();
        var runId = Guid.NewGuid().ToString("N");
        var duration = TimeSpan.FromMinutes(1);

        (await owner.TryAcquireLeaseAsync(runId, "owner", duration)).Should().BeTrue();
        (await competitor.TryAcquireLeaseAsync(runId, "other", duration)).Should().BeFalse();
        (await competitor.RenewLeaseAsync(runId, "other", duration)).Should().BeFalse();
        await competitor.ReleaseLeaseAsync(runId, "other");
        (await owner.RenewLeaseAsync(runId, "owner", duration)).Should().BeTrue();
        await owner.ReleaseLeaseAsync(runId, "owner");
        (await competitor.TryAcquireLeaseAsync(runId, "other", duration)).Should().BeTrue();
        await competitor.ReleaseLeaseAsync(runId, "other");
    }

    private static WorkflowRun Run() => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        WorkflowId = Guid.NewGuid().ToString("N"),
        Status = WorkflowRunStatus.Running,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        StepStates = [new WorkflowStepState
        {
            StepId = "extract",
            PlanId = "plan",
            Status = WorkflowStepStatus.Running
        }]
    };

    private static WebAppFixture CreateHost() => new WebAppFixture()
        .WithProductionJobSubstrate()
        .ConfigureWebHost(builder => builder.UseSetting("ConnectionStrings:redis", string.Empty));
}

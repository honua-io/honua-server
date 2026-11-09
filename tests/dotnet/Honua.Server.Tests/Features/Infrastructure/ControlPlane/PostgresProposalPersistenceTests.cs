// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class PostgresProposalPersistenceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("ConnectionStrings:redis", "");
        builder.UseSetting("Licensing:DevGrantEdition", "Pro");
    });

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();
    private IOperationProposalStore Store => _fixture.GetService<IOperationProposalStore>();

    [IntegrationTest]
    public async Task TryCreateAsync_WithoutRedis_PreservesApprovalAcrossHostRestart()
    {
        var proposal = Proposal() with
        {
            Status = OperationProposalStatus.Succeeded,
            ResolvedBy = "reviewer",
            ResolutionReason = "Approved maintenance",
            ResolvedAt = DateTimeOffset.UtcNow,
            SealedPlanHash = "sealed-plan",
            TenantId = "tenant-a",
            RecognizedScopes = ["admin:write"],
            Plan = new OperationProposalPlan
            {
                Summary = "Maintenance",
                ExecutionPayload = "{\"target\":\"sample\"}",
                Diff = ["change"],
                RiskLevel = ProposalRiskLevel.High
            }
        };
        Assert.True(await Store.TryCreateAsync(proposal));
        Assert.False(await Store.TryCreateAsync(proposal));

        await _fixture.RestartHostAsync();

        var loaded = Assert.IsType<OperationProposal>(await Store.GetAsync(proposal.ProposalId));
        Assert.Equal(proposal.ResolvedBy, loaded.ResolvedBy);
        Assert.Equal(proposal.ResolutionReason, loaded.ResolutionReason);
        Assert.Equal(proposal.Plan.ExecutionPayload, loaded.Plan.ExecutionPayload);
        Assert.Equal(proposal.SealedPlanHash, loaded.SealedPlanHash);
        Assert.Equal(proposal.TenantId, loaded.TenantId);
        Assert.Equal(proposal.RecognizedScopes, loaded.RecognizedScopes);
        Assert.Equal(0, loaded.Version);
        Assert.DoesNotContain(await Store.ListActiveAsync(), item => item.ProposalId == proposal.ProposalId);
    }

    [IntegrationTest]
    public async Task TrySetAsync_ConcurrentApprovals_ExactlyOneWinsAndTerminalLeavesActiveList()
    {
        var proposal = Proposal();
        Assert.True(await Store.TryCreateAsync(proposal));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = Enumerable.Range(0, 8).Select(async index =>
        {
            await start.Task;
            return await Store.TrySetAsync(proposal with
            {
                Status = OperationProposalStatus.Succeeded,
                ResolvedBy = $"reviewer-{index}"
            });
        }).ToArray();
        start.SetResult();

        Assert.Single((await Task.WhenAll(writes)).Where(won => won));
        var loaded = Assert.IsType<OperationProposal>(await Store.GetAsync(proposal.ProposalId));
        Assert.Equal(1, loaded.Version);
        Assert.Equal(OperationProposalStatus.Succeeded, loaded.Status);
        Assert.False(await Store.TrySetAsync(proposal));
        Assert.DoesNotContain(await Store.ListActiveAsync(), item => item.ProposalId == proposal.ProposalId);
        Assert.False(await Store.TrySetAsync(Proposal()));
    }

    [IntegrationTest]
    public async Task ListActiveAsync_KindFilterAndTransitions_ReflectPersistedState()
    {
        var first = Proposal();
        var second = Proposal() with { Kind = OperationClass.MetadataRelease };
        Assert.True(await Store.TryCreateAsync(first));
        Assert.True(await Store.TryCreateAsync(second));
        Assert.Contains(await Store.ListActiveAsync(first.Kind), item => item.ProposalId == first.ProposalId);
        Assert.DoesNotContain(await Store.ListActiveAsync(first.Kind), item => item.ProposalId == second.ProposalId);

        Assert.True(await Store.TrySetAsync(first with { Status = OperationProposalStatus.Executing }));
        Assert.Contains(await Store.ListActiveAsync(), item => item.ProposalId == first.ProposalId);
        Assert.True(await Store.TrySetAsync(first with { Version = 1, Status = OperationProposalStatus.Rejected }));
        Assert.DoesNotContain(await Store.ListActiveAsync(), item => item.ProposalId == first.ProposalId);
        Assert.Contains(await Store.ListActiveAsync(), item => item.ProposalId == second.ProposalId);
    }

    [IntegrationTest]
    public async Task LeaseAsync_HostRestartAndWrongOwner_PreserveExclusiveOwnership()
    {
        var id = Proposal().ProposalId;
        Assert.True(await Store.TryAcquireLeaseAsync(id, "owner", TimeSpan.FromMinutes(1)));
        await _fixture.RestartHostAsync();
        Assert.False(await Store.TryAcquireLeaseAsync(id, "other", TimeSpan.FromMinutes(1)));
        Assert.False(await Store.RenewLeaseAsync(id, "other", TimeSpan.FromMinutes(1)));
        await Store.ReleaseLeaseAsync(id, "other");
        Assert.False(await Store.TryAcquireLeaseAsync(id, "other", TimeSpan.FromMinutes(1)));
        Assert.True(await Store.RenewLeaseAsync(id, "owner", TimeSpan.FromMinutes(1)));
        await Store.ReleaseLeaseAsync(id, "owner");
        Assert.True(await Store.TryAcquireLeaseAsync(id, "other", TimeSpan.FromMinutes(1)));
        await Store.ReleaseLeaseAsync(id, "other");
    }

    [IntegrationTest]
    public async Task StoreAsync_CancelledRequest_DoesNotWrite()
    {
        var proposal = Proposal();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.TryCreateAsync(proposal, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.GetAsync(proposal.ProposalId, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.ListActiveAsync(cancellationToken: cancellation.Token));
        Assert.Null(await Store.GetAsync(proposal.ProposalId));
    }

    private static OperationProposal Proposal() => new()
    {
        ProposalId = Guid.NewGuid().ToString("N"),
        Kind = OperationClass.AdminConfigChange,
        Status = OperationProposalStatus.AwaitingApproval,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        RequestedBy = "requester"
    };
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class PlannedProposalReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public async Task SweepOnceAsync_StalePlannedProposal_LeasesAuditsAndCompensates()
    {
        var store = Substitute.For<IOperationProposalStore>();
        var audit = Substitute.For<IAuditLog>();
        var proposal = Proposal(Now - PlannedProposalReconciler.StaleAge - TimeSpan.FromSeconds(1));
        store.TryAcquireLeaseAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        store.ListActiveAsync(null, Arg.Any<CancellationToken>())
            .Returns([proposal]);
        store.GetAsync(proposal.ProposalId, Arg.Any<CancellationToken>())
            .Returns(proposal);
        store.TrySetAsync(
                Arg.Any<OperationProposal>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        audit.RecordAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>())
            .Returns("audit-janitor-1");
        var reconciler = new PlannedProposalReconciler(
            store,
            audit,
            new FixedTimeProvider(Now),
            NullLogger<PlannedProposalReconciler>.Instance);

        await reconciler.SweepOnceAsync();

        await store.Received(1).TryAcquireLeaseAsync(
            "operation-proposal-planned-janitor",
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync(
            Arg.Is<AuditEvent>(entry =>
                entry.Action == "operation.proposal.planned-timeout" &&
                entry.ResourceId == proposal.ProposalId),
            Arg.Any<CancellationToken>());
        await store.Received(1).TrySetAsync(
            Arg.Is<OperationProposal>(updated =>
                updated.Status == OperationProposalStatus.Failed &&
                updated.Audit.AuditId == "audit-janitor-1" &&
                updated.ResolvedAt == Now),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
        await store.Received(1).ReleaseLeaseAsync(
            "operation-proposal-planned-janitor",
            Arg.Any<string>(),
            CancellationToken.None);
    }

    [UnitTest]
    public async Task SweepOnceAsync_LeaseUnavailable_PerformsNoCompensation()
    {
        var store = Substitute.For<IOperationProposalStore>();
        var audit = Substitute.For<IAuditLog>();
        store.TryAcquireLeaseAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        var reconciler = new PlannedProposalReconciler(
            store,
            audit,
            new FixedTimeProvider(Now),
            NullLogger<PlannedProposalReconciler>.Instance);

        await reconciler.SweepOnceAsync();

        await store.DidNotReceive().ListActiveAsync(
            Arg.Any<OperationClass?>(),
            Arg.Any<CancellationToken>());
        await audit.DidNotReceive().RecordAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    private static OperationProposal Proposal(DateTimeOffset updatedAt) => new()
    {
        ProposalId = "proposal-stale-1",
        Kind = OperationClass.Deploy,
        Status = OperationProposalStatus.Planned,
        Audit = new OperationAuditInfo
        {
            OperationInstanceId = "opinst-stale-1",
            CorrelationId = "corr-stale-1",
        },
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt,
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

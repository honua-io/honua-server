// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Observability.Domain;
using Honua.Core.Features.Observability.Services;

namespace Honua.Core.Tests.Features.Observability;

public sealed class InMemoryOpsAutonomyProposalAccountingTests
{
    private const string Rule = "alert-dispatch-backlog";

    [Fact]
    public async Task RecordProposalResolution_ConcurrentApprovalRetries_IncrementsExactlyOnce()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            store.RecordProposalResolutionAsync(
                Rule,
                "proposal-1",
                OpsAutonomyProposalResolution.Approved)));

        var snapshot = (await store.ListPoliciesAsync()).Should().ContainSingle().Subject;
        snapshot.IsPersisted.Should().BeFalse();
        snapshot.TrackRecord.ProposalsApproved.Should().Be(1);
        snapshot.TrackRecord.ProposalsRejected.Should().Be(0);
    }

    [Fact]
    public async Task RecordProposalResolution_ConflictingReplay_PreservesOriginalCounter()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();

        await store.RecordProposalResolutionAsync(
            Rule,
            "proposal-1",
            OpsAutonomyProposalResolution.Approved);
        await store.RecordProposalResolutionAsync(
            Rule,
            "proposal-1",
            OpsAutonomyProposalResolution.Rejected);
        await store.RecordProposalResolutionAsync(
            Rule,
            "proposal-2",
            OpsAutonomyProposalResolution.Rejected);

        var track = (await store.ListPoliciesAsync()).Should().ContainSingle().Subject.TrackRecord;
        track.ProposalsApproved.Should().Be(1);
        track.ProposalsRejected.Should().Be(1);
    }
}

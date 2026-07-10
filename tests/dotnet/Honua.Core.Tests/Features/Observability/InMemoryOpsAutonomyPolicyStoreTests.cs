// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Observability.Domain;
using Honua.Core.Features.Observability.Services;

namespace Honua.Core.Tests.Features.Observability;

public sealed class InMemoryOpsAutonomyPolicyStoreTests
{
    private const string Rule = "alert-dispatch-backlog";

    [Fact]
    public async Task RecordAutoActionOutcome_IndeterminateAndCanceled_CountAsFailedNotAutoApplied()
    {
        var store = new InMemoryOpsAutonomyPolicyStore();
        await store.SetPolicyAsync(
            new OpsAutonomyPolicy
            {
                Rule = Rule,
                Mode = OpsAutonomyMode.AutoApply,
                MaxAutoActionsPerWindow = 2,
                Window = TimeSpan.FromHours(1),
            },
            changedBy: "test");
        var indeterminate = await store.TryReserveAutoActionAsync(Reservation("finding-indeterminate"));
        var canceled = await store.TryReserveAutoActionAsync(Reservation("finding-canceled"));

        await store.RecordAutoActionOutcomeAsync(
            indeterminate.ReservationId!,
            OpsAutonomyActionOutcome.Indeterminate,
            operationId: "op-indeterminate");
        await store.RecordAutoActionOutcomeAsync(
            canceled.ReservationId!,
            OpsAutonomyActionOutcome.Canceled,
            operationId: "op-canceled");

        var snapshot = (await store.ListPoliciesAsync()).Should().ContainSingle().Subject;
        snapshot.TrackRecord.AutoApplied.Should().Be(0);
        snapshot.TrackRecord.RolledBack.Should().Be(0);
        snapshot.TrackRecord.Failed.Should().Be(2,
            "neither unknown convergence nor cancellation may improve the autonomy success record");
    }

    private static OpsAutonomyReservationRequest Reservation(string findingId)
        => new()
        {
            Rule = Rule,
            FindingId = findingId,
            OperationClass = OperationClass.AdminConfigChange,
            ActionDiscriminator = "alerts.redrive_dead_letters",
            MaxAutoActionsPerWindow = 2,
            Window = TimeSpan.FromHours(1),
        };
}

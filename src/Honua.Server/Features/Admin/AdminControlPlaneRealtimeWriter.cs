// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

// Append last, on the state writer's transaction, so a client never loses a committed change.
internal sealed class AdminControlPlaneRealtimeWriter(IRealtimeTransactionWriter log) : IControlPlaneRealtimeWriter
{
    public void NotifyCommitted() => log.NotifyCommitted();

    public async Task AppendProposalAsync(OperationProposal proposal, ControlPlaneWriteContext context)
    {
        if (proposal.Status is OperationProposalStatus.Planned or OperationProposalStatus.Executing)
        {
            return;
        }

        var kind = proposal.Status == OperationProposalStatus.AwaitingApproval
            ? AdminRealtimeContract.ProposalPendingEventName : AdminRealtimeContract.ProposalResolvedEventName;
        var payload = new ProposalRealtimeEvent(proposal.ProposalId, proposal.Kind.ToString(),
            proposal.Status.ToString(), proposal.RequestedBy, proposal.Plan.RiskLevel.ToString(), proposal.UpdatedAt);
        await log.AppendAsync(RealtimeFamily.Admin, [new RealtimeTransactionEntry(AdminRealtimeContract.ProposalsTopic, kind,
            JsonSerializer.SerializeToElement(payload, AdminRealtimeJsonContext.Default.ProposalRealtimeEvent))],
            context.Transaction, context.CancellationToken).ConfigureAwait(false);
    }

    public async Task AppendTransitionAsync(WorkflowOperationTransition transition, ControlPlaneWriteContext context)
    {
        var operateEvent = OperateEventResponseMapper.Map(ReleaseTimelineEventFactory.Create(transition));
        await log.AppendAsync(RealtimeFamily.Admin,
        [
            new RealtimeTransactionEntry(AdminRealtimeContract.DeployOperationsTopic, AdminRealtimeContract.DeployOperationEventName,
                JsonSerializer.SerializeToElement(MapDeployEvent(transition), AdminRealtimeGroupsJsonContext.Default.DeployOperationRealtimeEvent)),
            new RealtimeTransactionEntry(AdminRealtimeContract.OperateEventsTopic, AdminRealtimeContract.OperateEventEventName,
                JsonSerializer.SerializeToElement(operateEvent, AdminRealtimeGroupsJsonContext.Default.OperateEventResponse))
        ], context.Transaction, context.CancellationToken).ConfigureAwait(false);
    }

    private static DeployOperationRealtimeEvent MapDeployEvent(WorkflowOperationTransition transition)
    {
        var occurredTicks = transition.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture);
        return new DeployOperationRealtimeEvent(
            EventId: $"deploy:{transition.OperationId}:{transition.Kind}:{occurredTicks}",
            OperationId: transition.OperationId,
            TransitionKind: transition.Kind.ToString(),
            Status: transition.Operation.Status.ToString(),
            Phase: transition.Operation.CurrentPhase,
            TargetId: transition.TargetId,
            ReleaseId: transition.ReleaseId,
            CorrelationId: transition.CorrelationId,
            OperationInstanceId: transition.Operation.Audit.OperationInstanceId,
            AuditId: transition.Operation.Audit.AuditId,
            ProposalId: transition.Operation.Audit.ProposalId,
            OccurredAt: transition.OccurredAt);
    }
}

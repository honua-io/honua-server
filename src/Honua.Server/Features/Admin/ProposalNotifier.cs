// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.AspNetCore.SignalR;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Publishes proposal lifecycle events to the <see cref="AdminHub"/> proposals
/// group so the console reacts live. Emitted from the gateway/store boundary, not
/// per-endpoint (#1695).
/// </summary>
internal sealed partial class ProposalNotifier(
    IHubContext<AdminHub> hubContext,
    ILogger<ProposalNotifier> logger) : IProposalNotifier
{
    public async Task NotifyPendingAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await SendAsync(
                AdminRealtimeContract.ProposalPendingEventName,
                proposal,
                cancellationToken)
            .ConfigureAwait(false);
        Log.ProposalPending(logger, proposal.ProposalId, proposal.Kind.ToString());
    }

    public async Task NotifyResolvedAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await SendAsync(
                AdminRealtimeContract.ProposalResolvedEventName,
                proposal,
                cancellationToken)
            .ConfigureAwait(false);
        Log.ProposalResolved(logger, proposal.ProposalId, proposal.Status.ToString());
    }

    private Task SendAsync(string eventName, OperationProposal proposal, CancellationToken cancellationToken)
    {
        var payload = new ProposalRealtimeEvent(
            proposal.ProposalId,
            proposal.Kind.ToString(),
            proposal.Status.ToString(),
            proposal.RequestedBy,
            proposal.Plan.RiskLevel.ToString(),
            DateTimeOffset.UtcNow);

        return hubContext.Clients
            .Group(AdminRealtimeContract.ProposalsGroup)
            .SendAsync(eventName, payload, cancellationToken);
    }

    private static partial class Log
    {
        [LoggerMessage(9300, LogLevel.Information, "Emitted ProposalPending for {ProposalId} ({Kind})")]
        public static partial void ProposalPending(ILogger logger, string proposalId, string kind);

        [LoggerMessage(9301, LogLevel.Information, "Emitted ProposalResolved for {ProposalId} (status {Status})")]
        public static partial void ProposalResolved(ILogger logger, string proposalId, string status);
    }
}

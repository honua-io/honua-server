// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin;

internal sealed partial class ProposalNotifier(
    ILogger<ProposalNotifier> logger) : IProposalNotifier
{
    public Task NotifyPendingAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Log.ProposalPending(logger, proposal.ProposalId, proposal.Kind.ToString());
        return Task.CompletedTask;
    }

    public Task NotifyResolvedAsync(OperationProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Log.ProposalResolved(logger, proposal.ProposalId, proposal.Status.ToString());
        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(9300, LogLevel.Information, "Recorded ProposalPending for {ProposalId} ({Kind})")]
        public static partial void ProposalPending(ILogger logger, string proposalId, string kind);

        [LoggerMessage(9301, LogLevel.Information, "Recorded ProposalResolved for {ProposalId} (status {Status})")]
        public static partial void ProposalResolved(ILogger logger, string proposalId, string status);
    }
}

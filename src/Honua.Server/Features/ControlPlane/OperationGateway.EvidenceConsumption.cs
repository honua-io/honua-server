// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane;

internal sealed partial class OperationGateway
{
    private enum ProposalCreation
    {
        Created,
        IdConflict,
        EvidenceAlreadyUsed,
    }

    // An idempotent retry of the same proposal id conflicts on the id first, so it reports IdConflict
    // and folds onto the winner; only a different proposal reusing signed evidence is refused.
    private async Task<ProposalCreation> TryCreateProposalAsync(
        OperationProposal proposal,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _proposalStore.TryCreateAsync(proposal, cancellationToken: cancellationToken).ConfigureAwait(false)
                ? ProposalCreation.Created
                : ProposalCreation.IdConflict;
        }
        catch (OperationProposalEvidenceConsumedException)
        {
            return ProposalCreation.EvidenceAlreadyUsed;
        }
    }

    private static OperationGatewayResult EvidenceAlreadyUsedResult(GuardrailDecision decision)
        => new()
        {
            Outcome = OperationGatewayOutcome.Blocked,
            Decision = decision,
            Message = OperationProposalEvidenceConsumedException.AlreadyUsedMessage,
        };
}

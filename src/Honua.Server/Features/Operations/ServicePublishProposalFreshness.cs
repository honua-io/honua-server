// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Operations;

// Says whether a service.publish proposal can still succeed against the catalog it pinned.
internal static class ServicePublishProposalFreshness
{
    // A replay that failed on its stale pin needs a fresh proposal, the same as one found stale before approval.
    public static bool FailedOnStalePin(OperationProposal proposal, string? status, string? resolutionReason)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return IsServicePublish(proposal)
            && string.Equals(status, nameof(OperationProposalStatus.Failed), StringComparison.Ordinal)
            && string.Equals(resolutionReason, LayerPublicationBaseChanged.ProposalReason, StringComparison.Ordinal);
    }

    private static bool IsServicePublish(OperationProposal proposal)
        => string.Equals(proposal.OperationId, ServicePublishOperation.OperationId, StringComparison.Ordinal);
}

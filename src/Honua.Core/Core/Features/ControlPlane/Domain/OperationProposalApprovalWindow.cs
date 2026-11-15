// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.ControlPlane.Domain;

public static class OperationProposalApprovalWindow
{
    public static DateTimeOffset GetDeadline(OperationProposal proposal)
        => proposal.ApprovalExpiresAt ?? proposal.CreatedAt.AddHours(
            (proposal.AutonomyMetadata?.Severity is { } severity
                ? severity == OpsFindingSeverity.Critical
                : proposal.Plan.RiskLevel == ProposalRiskLevel.High) ? 72 : 24);

    public static bool IsElapsed(OperationProposal proposal, DateTimeOffset now)
        => now >= GetDeadline(proposal);
}

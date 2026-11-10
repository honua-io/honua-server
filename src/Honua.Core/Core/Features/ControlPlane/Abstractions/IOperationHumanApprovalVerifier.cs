// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public interface IOperationHumanApprovalVerifier
{
    Task<OperationHumanApprovalEvidence> VerifyAsync(
        OperationProposal proposal,
        OperationProposalApprovalContext approval,
        CancellationToken cancellationToken = default);
}

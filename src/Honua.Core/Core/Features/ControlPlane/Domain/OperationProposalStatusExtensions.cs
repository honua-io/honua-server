// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public static class OperationProposalStatusExtensions
{
    public static bool IsTerminal(this OperationProposalStatus status)
        => status is OperationProposalStatus.Succeeded
            or OperationProposalStatus.Failed
            or OperationProposalStatus.Rejected
            or OperationProposalStatus.RolledBack
            or OperationProposalStatus.Cancelled;
}

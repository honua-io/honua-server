// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

// This entry can propose an operator-gated operation and can never actuate it.
public interface IOperationProposalSubmitter
{
    // Replay proofs and dry runs are refused here; an approved replay uses the invoker.
    Task<OperationHandle> ProposeAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default);
}

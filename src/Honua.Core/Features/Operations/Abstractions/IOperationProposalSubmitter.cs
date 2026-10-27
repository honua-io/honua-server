// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>Trusted adapter entry point that can propose an operator-gated operation but can never actuate it.</summary>
public interface IOperationProposalSubmitter
{
    /// <summary>Accepts and validates a non-dry-run request, preserving policy refusals and requiring durable separate approval.</summary>
    /// <remarks>Context must come from authenticated server authority. Replay proofs and dry runs are refused; approved replay uses IOperationInvoker.</remarks>
    Task<OperationHandle> ProposeAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default);
}

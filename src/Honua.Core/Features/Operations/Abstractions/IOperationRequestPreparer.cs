// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>Optionally captures stable execution targets before validation, policy and approval.</summary>
public interface IOperationRequestPreparer
{
    /// <summary>
    /// Returns a copied request with its resolved target pinned. Approved replay must
    /// retain and verify its sealed target rather than resolving a new authority.
    /// </summary>
    Task<OperationRequest> PrepareAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default);
}

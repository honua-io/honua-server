// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

public interface IOperationRequestPreparer
{
    // Approved replay must keep and verify its sealed target rather than resolving a new authority.
    Task<OperationRequest> PrepareAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default);
}

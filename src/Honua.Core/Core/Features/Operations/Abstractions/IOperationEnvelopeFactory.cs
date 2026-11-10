// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

public interface IOperationEnvelopeFactory
{
    Task<OperationHandle> CreateAcceptedAsync(
        string operationId,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default);

    // A cached payload is evidence, and its identities are never reused for the new invocation.
    Task<OperationHandle> CompleteCacheHitAsync(
        string operationId,
        OperationPolicyContext context,
        string sourceOperationInstanceId,
        string? sourceAuditId,
        CancellationToken cancellationToken = default);
}

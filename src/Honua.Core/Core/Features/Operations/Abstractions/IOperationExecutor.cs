// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

public interface IOperationExecutor
{
    string OperationId { get; }

    Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default);

    Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default);

    Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default);
}

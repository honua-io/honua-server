// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Abstractions;

public interface IOperationInvoker
{
    Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default);

    Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default);
}

public sealed class OperationNotFoundException : Exception
{
    public OperationNotFoundException(string operationId)
        : base($"Operation '{operationId}' is not registered.")
        => OperationId = operationId;

    public string OperationId { get; }
}

public sealed class OperationUnavailableException : Exception
{
    public OperationUnavailableException(string operationId, string reason)
        : base(reason)
        => OperationId = operationId;

    public string OperationId { get; }
}

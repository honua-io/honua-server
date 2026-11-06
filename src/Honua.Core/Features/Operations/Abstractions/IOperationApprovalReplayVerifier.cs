// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Abstractions;

// The supplied hash is only a lookup claim. Verification re-reads the durable proposal and recomputes the sealed-plan hash before actuation.
public interface IOperationApprovalReplayVerifier
{
    Task<bool> VerifyAsync(
        string proposalId,
        string operationInstanceId,
        string planHash,
        CancellationToken cancellationToken = default);
}

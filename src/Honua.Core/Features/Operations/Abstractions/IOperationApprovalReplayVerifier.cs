// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Re-reads the durable proposal authority before an approved replay may reach an actuator.
/// </summary>
public interface IOperationApprovalReplayVerifier
{
    /// <summary>
    /// Verifies that the claimed approval is executing for the original operation instance and
    /// that its supplied plan hash still matches the sealed plan in durable storage.
    /// </summary>
    Task<bool> VerifyAsync(
        string proposalId,
        string operationInstanceId,
        string planHash,
        CancellationToken cancellationToken = default);
}

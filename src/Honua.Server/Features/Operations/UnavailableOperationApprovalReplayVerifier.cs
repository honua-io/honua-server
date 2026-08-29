// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Fail-closed replay verifier for hosts composed without a durable proposal store.
/// Approval replay is unforgeable only by re-reading the durable authority; with no
/// store there is no authority, so every verification refuses. Registered in place of
/// <see cref="OperationApprovalReplayVerifier"/> so degraded hosts refuse at USE time
/// instead of failing DI validation at boot (third member of the family behind the
/// 2026-08-29 trunk reds; siblings were gated in #3614 and #3617).
/// </summary>
internal sealed class UnavailableOperationApprovalReplayVerifier : IOperationApprovalReplayVerifier
{
    public Task<bool> VerifyAsync(
        string proposalId,
        string operationInstanceId,
        string planHash,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

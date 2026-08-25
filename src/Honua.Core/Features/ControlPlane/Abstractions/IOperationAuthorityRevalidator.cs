// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Revalidates retained proposer authority against current identity, grant, scope,
/// tenant/resource binding, and edition policy immediately before approved replay.
/// </summary>
public interface IOperationAuthorityRevalidator
{
    /// <summary>Returns a fail-closed current-authority decision.</summary>
    Task<OperationAuthorityRevalidationResult> RevalidateAsync(
        OperationProposal proposal,
        CancellationToken cancellationToken = default);
}

/// <summary>Current-authority replay decision.</summary>
public readonly record struct OperationAuthorityRevalidationResult(bool IsAllowed, string? Reason)
{
    public static OperationAuthorityRevalidationResult Allowed() => new(true, null);

    public static OperationAuthorityRevalidationResult Denied(string reason) => new(false, reason);
}

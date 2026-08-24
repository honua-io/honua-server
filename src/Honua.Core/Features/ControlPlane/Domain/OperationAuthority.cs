// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// The authenticated authority snapshot captured for a mutating operation.
/// This is proposer authority: an approver can resolve a proposal, but never
/// replaces this snapshot during execution.
/// </summary>
public sealed record OperationAuthorityContext
{
    /// <summary>Canonical token issuer or API-key provider.</summary>
    public required string Issuer { get; init; }

    /// <summary>Canonical authenticated actor identifier.</summary>
    public required string Actor { get; init; }

    /// <summary>Authentication scheme used to establish the actor.</summary>
    public required string Scheme { get; init; }

    /// <summary>Tenant selected after authentication and authorization.</summary>
    public required string EffectiveTenant { get; init; }

    /// <summary>OAuth/API-key permissions present on the authenticated request.</summary>
    public IReadOnlyList<string> OAuthScopes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The maximum permission set available to this operation. It must be a subset
    /// of <see cref="OAuthScopes"/> so replay can only narrow authority.
    /// </summary>
    public IReadOnlyList<string> ScopeCeiling { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Validates the bounded, non-secret authority lineage before it is persisted.
    /// </summary>
    public bool TryValidate(out string? error)
    {
        if (!IsBounded(Issuer, 512) || !IsBounded(Actor, 256) ||
            !IsBounded(Scheme, 64) || !IsBounded(EffectiveTenant, 256))
        {
            error = "Operation authority identifiers are missing or exceed their bounds.";
            return false;
        }

        if (OAuthScopes.Count > 128 || ScopeCeiling.Count > 128 ||
            OAuthScopes.Any(scope => !IsBounded(scope, 256)) ||
            ScopeCeiling.Any(scope => !IsBounded(scope, 256)))
        {
            error = "Operation authority scopes are missing or exceed their bounds.";
            return false;
        }

        var granted = OAuthScopes.ToHashSet(StringComparer.Ordinal);
        if (ScopeCeiling.Any(scope => !granted.Contains(scope)))
        {
            error = "Operation scope ceiling exceeds the authenticated scope set.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsBounded(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;
}

/// <summary>
/// Durable approval decision metadata. The proposer authority is intentionally
/// not replaced by this record when an approved proposal is replayed.
/// </summary>
public sealed record OperationApprovalRecord
{
    /// <summary>Principal that approved or rejected the proposal.</summary>
    public required string Approver { get; init; }

    /// <summary>Whether this record represents approval rather than rejection.</summary>
    public required bool Approved { get; init; }

    /// <summary>When the decision was durably recorded.</summary>
    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>Whether execution retained the original proposer authority.</summary>
    public bool ProposerAuthorityRetained { get; init; } = true;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin.Deploy;

// A supplied recovery fence is enforced before the rollback is submitted, and an unsatisfied term refuses the call rather than widening the grant.
internal static class RecoveryGrantFence
{
    // An unrecognised rollback property is a refusal because the caller's fence and the server's fence would otherwise disagree.
    internal const string UnknownPropertyCode = "recovery_fence_unknown_property";

    internal const string ActorMismatchCode = "recovery_fence_actor_mismatch";

    internal const string TenantMismatchCode = "recovery_fence_tenant_mismatch";

    internal const string ExpiredCode = "recovery_fence_expired";

    internal const string TargetMismatchCode = "recovery_fence_target_mismatch";

    internal const string ProtectionWindowAbsentCode = "recovery_fence_protection_window_absent";

    internal const string ProtectionPhaseUnrecognizedCode = "recovery_fence_protection_phase_unrecognized";

    internal const string ProtectionPhaseMismatchCode = "recovery_fence_protection_phase_mismatch";

    internal const string CandidateRevisionMismatchCode = "recovery_fence_candidate_revision_mismatch";

    internal const string PreviousRevisionMismatchCode = "recovery_fence_previous_revision_mismatch";

    internal const string GrantMismatchCode = "recovery_fence_grant_mismatch";

    internal const string PolicyDigestMismatchCode = "recovery_fence_policy_digest_mismatch";

    internal const string CompensationNotPermittedCode = "recovery_fence_compensation_not_permitted";

    public static RecoveryFenceRefusal? Evaluate(
        WorkflowOperationRecord operation,
        RollbackDeployOperationRequest? request,
        string? authenticatedActor,
        string? callerTenantId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var protection = operation.Deploy?.Protection;

        // Identity binding is enforced whether or not the caller supplied a fence: once an activation
        // has sealed a grant, only that actor/tenant may actuate its compensation. An unfenced caller
        // cannot opt out of the binding by staying silent, and a platform role is no exemption
        // (honua-server#4987): PlatformDeployAuthority requires that role of every tenant-bound deploy
        // caller, so exempting it let another tenant's administrator actuate this tenant's grant.
        if (protection != null)
        {
            if (!string.IsNullOrWhiteSpace(protection.Actor) &&
                !ValuesMatch(protection.Actor, authenticatedActor))
            {
                return new RecoveryFenceRefusal(
                    StatusCodes.Status403Forbidden,
                    ActorMismatchCode,
                    "The recovery grant for this deployment is bound to the principal that requested the " +
                    "protected activation; the authenticated principal is not that actor.");
            }

            // The tenant binding is compared in both directions: a grant sealed by a tenantless principal
            // does not become actuatable by a tenant-bound caller that happens to share its actor name.
            // With tenant resolution disabled no caller tenant is ever resolved, so this never refuses there.
            if (!TenantBindingMatches(protection.TenantId, callerTenantId))
            {
                return new RecoveryFenceRefusal(
                    StatusCodes.Status403Forbidden,
                    TenantMismatchCode,
                    "The recovery grant for this deployment is bound to the tenant that requested the " +
                    "protected activation; the authenticated principal is bound to a different tenant.");
            }
        }

        if (request == null || !request.HasFence)
        {
            return null;
        }

        if (request.NotAfter is { } notAfter && now > notAfter)
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status412PreconditionFailed,
                ExpiredCode,
                $"The declared recovery grant expired at {notAfter.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}; " +
                "an expired grant preauthorizes nothing.");
        }

        // A declared actor or tenant is bound to the sealed grant as well as to the caller
        // (honua-server#4987). Checking only the caller let a body quote another principal's grantId,
        // revisions and digest under the caller's own identity.
        if (!string.IsNullOrWhiteSpace(request.Actor) && !ValuesMatch(request.Actor, authenticatedActor))
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status403Forbidden,
                ActorMismatchCode,
                "The declared recovery actor is not the authenticated principal. The actor is read from the " +
                "validated identity and cannot be asserted by the request body.");
        }

        if (protection != null && !string.IsNullOrWhiteSpace(request.Actor) && !ValuesMatch(request.Actor, protection.Actor))
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status403Forbidden,
                ActorMismatchCode,
                "The declared recovery actor is not the principal this grant was sealed for; a grant authorizes " +
                "only the principal that requested the protected activation.");
        }

        if (!string.IsNullOrWhiteSpace(request.TenantId) && !ValuesMatch(request.TenantId, callerTenantId))
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status403Forbidden,
                TenantMismatchCode,
                "The declared recovery tenant is not the authenticated principal's tenant. The tenant is read " +
                "from the validated identity and cannot be asserted by the request body.");
        }

        if (protection != null && !string.IsNullOrWhiteSpace(request.TenantId) && !ValuesMatch(request.TenantId, protection.TenantId))
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status403Forbidden,
                TenantMismatchCode,
                "The declared recovery tenant is not the tenant this grant was sealed for; a grant authorizes " +
                "only its sealed tenant binding.");
        }

        if (!string.IsNullOrWhiteSpace(request.TargetId) &&
            !ValuesMatch(request.TargetId, operation.Deploy?.TargetId))
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status409Conflict,
                TargetMismatchCode,
                $"The declared recovery target '{request.TargetId}' is not the target this operation deploys to.");
        }

        if (request.HasProtectionBoundFence && protection == null)
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status409Conflict,
                ProtectionWindowAbsentCode,
                "The rollback declares recovery-grant terms, but this operation has no protection window: " +
                "no candidate was ever exposed, so there is no sealed grant to satisfy them.");
        }

        if (protection != null)
        {
            var refusal = EvaluateProtectionTerms(request, protection);
            if (refusal != null)
            {
                return refusal;
            }
        }

        return null;
    }

    private static RecoveryFenceRefusal? EvaluateProtectionTerms(
        RollbackDeployOperationRequest request,
        DeployProtectionState protection)
    {
        if (!string.IsNullOrWhiteSpace(request.ExpectedProtectionPhase))
        {
            if (!TryParsePhase(request.ExpectedProtectionPhase, out var expectedPhase))
            {
                return new RecoveryFenceRefusal(
                    StatusCodes.Status400BadRequest,
                    ProtectionPhaseUnrecognizedCode,
                    $"'{request.ExpectedProtectionPhase}' is not a recognized protection phase. Expected one of " +
                    "observing, protected, recovering, expired, unavailable.");
            }

            if (expectedPhase != protection.Phase)
            {
                return new RecoveryFenceRefusal(
                    StatusCodes.Status409Conflict,
                    ProtectionPhaseMismatchCode,
                    $"The declared protection phase '{request.ExpectedProtectionPhase}' is not the phase this " +
                    "operation is in; the recovery would be acting on a window that has already moved on.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedCandidateRevision) &&
            !ValuesMatch(request.ExpectedCandidateRevision, protection.CandidateRevision))
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status409Conflict,
                CandidateRevisionMismatchCode,
                $"The declared candidate revision '{request.ExpectedCandidateRevision}' is not the revision " +
                "currently activated for this target; a newer approved intent would be overwritten.");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedPreviousRevision) &&
            !ValuesMatch(request.ExpectedPreviousRevision, protection.PreviousRevision))
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status409Conflict,
                PreviousRevisionMismatchCode,
                $"The declared previous revision '{request.ExpectedPreviousRevision}' is not the revision this " +
                "compensation restores.");
        }

        if (!string.IsNullOrWhiteSpace(request.GrantId) && !ValuesMatch(request.GrantId, protection.GrantId))
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status409Conflict,
                GrantMismatchCode,
                "The declared recovery grant is not the grant this activation sealed.");
        }

        if (!string.IsNullOrWhiteSpace(request.PolicyDigest) && !ValuesMatch(request.PolicyDigest, protection.PolicyDigest))
        {
            return new RecoveryFenceRefusal(
                StatusCodes.Status409Conflict,
                PolicyDigestMismatchCode,
                "The declared safety-policy digest is not the digest this activation was approved under; the " +
                "recovery would replay a policy that was never approved.");
        }

        if (!string.IsNullOrWhiteSpace(request.Compensation) &&
            !ValuesMatch(request.Compensation, protection.PermittedCompensation))
        {
            var permitted = string.IsNullOrWhiteSpace(protection.PermittedCompensation)
                ? "none"
                : protection.PermittedCompensation;
            return new RecoveryFenceRefusal(
                StatusCodes.Status403Forbidden,
                CompensationNotPermittedCode,
                $"This activation preauthorized the compensation '{permitted}'. '{request.Compensation}' is not " +
                "that compensation and is therefore broader than what was approved.");
        }

        return null;
    }

    private static bool TenantBindingMatches(string? sealedTenantId, string? callerTenantId)
        => string.IsNullOrWhiteSpace(sealedTenantId)
            ? string.IsNullOrWhiteSpace(callerTenantId)
            : ValuesMatch(sealedTenantId, callerTenantId);

    private static bool TryParsePhase(string value, out DeployProtectionPhase phase)
        => Enum.TryParse(value.Trim(), ignoreCase: true, out phase) && Enum.IsDefined(phase);

    // Identifiers on this surface (revisions, digests, grant ids, tenant and actor names) are compared
    // case-insensitively after trimming: a difference in case or padding is a client formatting artifact,
    // not a different grant, and treating it as one would produce refusals an operator cannot act on.
    private static bool ValuesMatch(string? declared, string? actual)
        => !string.IsNullOrWhiteSpace(actual) &&
            string.Equals(declared?.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
}

internal sealed record RecoveryFenceRefusal(int StatusCode, string Code, string Detail);

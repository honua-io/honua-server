// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal enum AuthorizationDenialReason
{
    InsufficientGrant,

    InsufficientScope,

    StalePrincipalMembership,
}

internal sealed class GeoprocessingAuthorizationException : Exception
{
    public bool RequiresAuthentication { get; }

    public AuthorizationDenialReason DenialReason { get; }

    public OperatorResourceType? ResourceType { get; }

    public OperatorOperation? Operation { get; }

    public string? PolicyCode { get; }

    public GeoprocessingAuthorizationException(bool requiresAuthentication)
        : base(requiresAuthentication
            ? "Authentication is required for this operation."
            : "You do not have permission to perform this operation.")
    {
        RequiresAuthentication = requiresAuthentication;
        DenialReason = AuthorizationDenialReason.InsufficientGrant;
    }

    public GeoprocessingAuthorizationException(
        bool requiresAuthentication,
        string message,
        OperatorResourceType? resourceType = null,
        OperatorOperation? operation = null,
        AuthorizationDenialReason denialReason = AuthorizationDenialReason.InsufficientGrant,
        string? policyCode = null)
        : base(message)
    {
        RequiresAuthentication = requiresAuthentication;
        ResourceType = resourceType;
        Operation = operation;
        DenialReason = denialReason;
        PolicyCode = policyCode;
    }
}

internal sealed class GeoprocessingApprovalRequiredException : Exception
{
    public string PolicyRef { get; }

    public string? ProposalId { get; }

    public GeoprocessingApprovalRequiredException(string policyRef, string? detail = null, string? proposalId = null)
        : base($"This operation requires approval (policy: {policyRef}). " +
               (proposalId != null
                   ? $"A proposal was created and is awaiting approval (proposalId: {proposalId})."
                   : detail ?? "Use ValidatePlan to check approval requirements before submission."))
    {
        PolicyRef = policyRef;
        ProposalId = proposalId;
    }
}

internal sealed class GeoprocessingNotFoundException : Exception
{
    public GeoprocessingNotFoundException(string message) : base(message) { }
}

internal class GeoprocessingPreconditionFailedException : Exception
{
    public GeoprocessingPreconditionFailedException(string message) : base(message) { }
}

internal sealed class GeoprocessingCancellationUnsupportedException : GeoprocessingPreconditionFailedException
{
    public GeoprocessingCancellationUnsupportedException(string message) : base(message) { }
}

internal sealed class GeoprocessingCancellationUnconfirmedException : GeoprocessingPreconditionFailedException
{
    public GeoprocessingCancellationUnconfirmedException(string message) : base(message) { }
}

internal sealed class GeoprocessingValidationException : Exception
{
    public GeoprocessingValidationException(string message) : base(message) { }
}

internal sealed class GeoprocessingStoreUnavailableException : Exception
{
    private GeoprocessingStoreUnavailableException(
        string detail,
        string errorCode,
        string? missingDependency,
        string? missingEntitlement,
        string remediation,
        string remediationRef)
        : base(detail)
    {
        ErrorCode = errorCode;
        MissingDependency = missingDependency;
        MissingEntitlement = missingEntitlement;
        CapabilityId = CapabilityUnavailableCodes.DurableJobsCapability;
        Remediation = remediation;
        RemediationRef = remediationRef;
    }

    public GeoprocessingStoreUnavailableException()
        : this(
            CapabilityUnavailableCodes.DurableJobStoreDetail,
            CapabilityUnavailableCodes.ErrorCode,
            CapabilityUnavailableCodes.RedisDependency,
            missingEntitlement: null,
            CapabilityUnavailableCodes.RedisRemediation,
            CapabilityUnavailableCodes.RedisRemediationRef)
    {
    }

    public GeoprocessingStoreUnavailableException(string message) : base(message) { }

    // The receipt names the dependency that is actually missing, so a host that already has Redis is not told to configure Redis.
    public static GeoprocessingStoreUnavailableException ForCause(DurableJobSubstrateCause cause)
        => cause switch
        {
            DurableJobSubstrateCause.RedisNotEntitled => new GeoprocessingStoreUnavailableException(
                CapabilityUnavailableCodes.UnentitledRedisDetail,
                CapabilityUnavailableCodes.EntitlementErrorCode,
                missingDependency: null,
                CapabilityUnavailableCodes.RedisCacheEntitlement,
                CapabilityUnavailableCodes.EntitlementRemediation,
                CapabilityUnavailableCodes.EntitlementRemediationRef),

            DurableJobSubstrateCause.RuntimeIncomplete => new GeoprocessingStoreUnavailableException(
                CapabilityUnavailableCodes.RuntimeIncompleteDetail,
                CapabilityUnavailableCodes.ErrorCode,
                CapabilityUnavailableCodes.JobQueueDependency,
                missingEntitlement: null,
                CapabilityUnavailableCodes.RuntimeIncompleteRemediation,
                CapabilityUnavailableCodes.RedisRemediationRef),

            _ => new GeoprocessingStoreUnavailableException(),
        };

    public static GeoprocessingStoreUnavailableException ForServerlessManagedHost()
        => new(
            CapabilityUnavailableCodes.ServerlessManagedDetail,
            CapabilityUnavailableCodes.ErrorCode,
            CapabilityUnavailableCodes.LongLivedHostDependency,
            missingEntitlement: null,
            CapabilityUnavailableCodes.ServerlessManagedRemediation,
            CapabilityUnavailableCodes.ServerlessManagedRemediationRef);

    public string? ErrorCode { get; }

    public string? MissingDependency { get; }

    public string? MissingEntitlement { get; }

    public string? CapabilityId { get; }

    public string? Remediation { get; }

    public string? RemediationRef { get; }

    public bool HasDependencyReceipt => ErrorCode is not null;
}

internal sealed class GeoprocessingIdempotencyConflictException : Exception
{
    public GeoprocessingIdempotencyConflictException(string? conflictingJobId = null)
        : base("Idempotency key is already associated with a different request.")
    {
        ConflictingJobId = conflictingJobId;
    }

    public string? ConflictingJobId { get; }
}

// Replacement stops when the colliding artifact cannot be deleted, instead of adding a second artifact under the same label.
internal sealed class ArtifactReplacementFailedException : Exception
{
    public string WorkspaceId { get; }

    public string Label { get; }

    public ArtifactReplacementFailedException(string workspaceId, string label)
        : base(
            $"Output '{label}' in workspace '{workspaceId}' could not be replaced: " +
            "the existing artifact could not be deleted, so the replacement was aborted " +
            "to avoid leaving a duplicate output. Retry, or remove the existing output before re-running.")
    {
        WorkspaceId = workspaceId;
        Label = label;
    }
}

internal sealed class GeoprocessingAdmissionException : Exception
{
    public Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionOutcome Outcome { get; }

    public Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionDimension DenyingDimension { get; }

    public string PolicyRef { get; }

    public int RetryAfterSeconds { get; }

    public GeoprocessingAdmissionException(
        Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionOutcome outcome,
        Honua.Core.Features.Geoprocessing.Domain.ExecutionAdmissionDimension dimension,
        string policyRef,
        string reason,
        int retryAfterSeconds)
        : base(reason)
    {
        Outcome = outcome;
        DenyingDimension = dimension;
        PolicyRef = policyRef;
        RetryAfterSeconds = retryAfterSeconds;
    }
}

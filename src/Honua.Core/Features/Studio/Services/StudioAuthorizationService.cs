// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Studio.Services;

/// <inheritdoc cref="IStudioAuthorizationService" />
public sealed class StudioAuthorizationService : IStudioAuthorizationService
{
    /// <summary>
    /// Sentinel operator-grant resource id meaning "any resource the caller owns". Lets an
    /// operator provision a single self-service grant
    /// (<c>Service=StudioDraft, Layer=own, Operation=...</c>) that authorizes every draft/item a
    /// non-admin principal owns, without pre-provisioning a grant per resource id. See
    /// <see cref="OperatorResourceType.StudioDraft"/>.
    /// </summary>
    internal const string OwnResourceSentinel = "own";

    private const string AdminRole = "admin";

    /// <summary>Denial code: the flag is off, so only admins may use the Studio lifecycle surface.</summary>
    public const string EndUserModeDisabledCode = "studio_authorization/end_user_mode_disabled";

    /// <summary>Denial code: the caller does not own the target resource and it is not publicly readable.</summary>
    public const string CrossUserDeniedCode = "studio_authorization/cross_user_denied";

    /// <summary>Denial code: the caller is not authenticated.</summary>
    public const string AuthenticationRequiredCode = "studio_authorization/authentication_required";

    /// <summary>Denial code: an elevated operation (publish-request/rollback) has no matching operator grant.</summary>
    public const string ElevatedGrantRequiredCode = "studio_authorization/elevated_grant_required";

    private readonly IOperatorAuthorizationEvaluator _evaluator;
    private readonly IOptionsMonitor<StudioEndUserAuthorizationOptions> _options;

    /// <summary>Initializes a new Studio authorization service.</summary>
    public StudioAuthorizationService(
        IOperatorAuthorizationEvaluator evaluator,
        IOptionsMonitor<StudioEndUserAuthorizationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(options);
        _evaluator = evaluator;
        _options = options;
    }

    /// <inheritdoc />
    public bool IsEndUserAuthorizationEnabled => _options.CurrentValue.Enabled;

    /// <inheritdoc />
    public bool IsAdmin(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.IsInRole(AdminRole);
    }

    /// <inheritdoc />
    public string? ResolveCallerId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not { IsAuthenticated: true })
        {
            return null;
        }

        // Honua.Core has no ASP.NET dependency, so this uses ClaimsPrincipal.FindFirst rather
        // than the Microsoft.AspNetCore.Authentication FindFirstValue extension.
        var candidate = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.FindFirst("api_key_id")?.Value;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.FindFirst("api_key_name")?.Value;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.Identity.Name;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        candidate = principal.FindFirst(ClaimTypes.Name)?.Value;
        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    /// <inheritdoc />
    public async Task<StudioAuthorizationDecision> AuthorizeAsync(
        ClaimsPrincipal principal,
        string? callerId,
        StudioAuthorizationOperation operation,
        string? resourceOwnerId,
        bool isPubliclyReadable = false,
        string? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Admins always have full, unscoped access -- unchanged before and after #3001, and
        // independent of the feature flag.
        if (IsAdmin(principal))
        {
            return StudioAuthorizationDecision.Allow();
        }

        if (!IsEndUserAuthorizationEnabled)
        {
            return StudioAuthorizationDecision.Deny(
                EndUserModeDisabledCode,
                "Studio package lifecycle operations require the admin role.");
        }

        if (principal.Identity is not { IsAuthenticated: true } || string.IsNullOrWhiteSpace(callerId))
        {
            return StudioAuthorizationDecision.Deny(
                AuthenticationRequiredCode,
                "Authentication is required for Studio package lifecycle operations.");
        }

        var isElevated = operation is StudioAuthorizationOperation.PublishRequest or StudioAuthorizationOperation.Rollback;
        var isRead = operation is StudioAuthorizationOperation.ReadDraft
            or StudioAuthorizationOperation.ReadContentItem
            or StudioAuthorizationOperation.ListOwn;
        var isOwn = resourceOwnerId is null || string.Equals(resourceOwnerId, callerId, StringComparison.Ordinal);

        if (!isOwn && !(isRead && isPubliclyReadable))
        {
            return StudioAuthorizationDecision.Deny(
                CrossUserDeniedCode,
                "The caller does not own this Studio resource.",
                isElevated);
        }

        if (!isElevated)
        {
            // Baseline tier: ownership (or public-read visibility) alone authorizes the
            // operation. No operator grant is required -- the flag itself is the widening
            // switch for REQ-002's "non-admin sessions can complete the draft->version flow on
            // their own items".
            return StudioAuthorizationDecision.Allow();
        }

        // Elevated tier (REQ-003): publish-request and rollback additionally require a matching
        // StudioDraft operator grant, evaluated through the platform's existing role/grant
        // infrastructure rather than a parallel mechanism. A grant scoped to the "own" sentinel
        // authorizes every resource the caller owns; a grant scoped to the concrete resourceId
        // (or the "*" wildcard) authorizes an operator-provisioned delegate, independent of
        // ownership.
        var operatorOperation = operation == StudioAuthorizationOperation.PublishRequest
            ? OperatorOperation.Publish
            : OperatorOperation.Rollback;

        if (isOwn && await HasOperatorGrantAsync(principal, operatorOperation, OwnResourceSentinel, cancellationToken).ConfigureAwait(false))
        {
            return StudioAuthorizationDecision.Allow(elevated: true);
        }

        if (resourceId is not null
            && await HasOperatorGrantAsync(principal, operatorOperation, resourceId, cancellationToken).ConfigureAwait(false))
        {
            return StudioAuthorizationDecision.Allow(elevated: true);
        }

        return StudioAuthorizationDecision.Deny(
            ElevatedGrantRequiredCode,
            $"'{operation}' requires a StudioDraft '{operatorOperation}' operator grant.",
            elevated: true);
    }

    private async Task<bool> HasOperatorGrantAsync(
        ClaimsPrincipal principal,
        OperatorOperation operation,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var decision = await _evaluator.EvaluateAsync(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.StudioDraft,
                ResourceId = resourceId,
                Operation = operation,
            },
            cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed;
    }
}

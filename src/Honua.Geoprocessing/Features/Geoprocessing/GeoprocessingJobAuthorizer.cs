// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Authorization and approval policy gate for <see cref="GeoprocessingJobService"/>.
/// Owns the operator <see cref="IOperatorAuthorizationEvaluator"/> and
/// <see cref="IOperatorApprovalEvaluator"/> collaborators so the job service delegates
/// every access decision through one shared seam rather than re-deriving them per call.
/// Behavior, logging, and the surfaced exceptions are identical to the inline checks the
/// service previously performed; the logger category is preserved by binding to
/// <see cref="GeoprocessingJobService"/>.
/// </summary>
internal sealed class GeoprocessingJobAuthorizer
{
    private readonly IOperatorAuthorizationEvaluator _authEvaluator;
    private readonly IOperatorApprovalEvaluator _approvalEvaluator;
    private readonly IOperatorScopeAuthorizer _scopeAuthorizer;
    private readonly GeoprocessingLayerAccessGuard _layerAccessGuard;
    private readonly ILogger<GeoprocessingJobService> _logger;

    /// <summary>
    /// Creates the authorization gate over the operator authorization and approval evaluators,
    /// plus the OAuth scope authorizer that narrows a bearer token's authority to its scopes
    /// (honua-server#2851) and the per-layer read gate (honua-server#2283).
    /// </summary>
    public GeoprocessingJobAuthorizer(
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        IOperatorScopeAuthorizer scopeAuthorizer,
        GeoprocessingLayerAccessGuard layerAccessGuard,
        ILogger<GeoprocessingJobService> logger)
    {
        _authEvaluator = authEvaluator;
        _approvalEvaluator = approvalEvaluator;
        _scopeAuthorizer = scopeAuthorizer;
        _layerAccessGuard = layerAccessGuard;
        _logger = logger;
    }

    /// <summary>
    /// Enforces read access, for the submitting principal, on every catalog layer the plan's
    /// gated steps will read, and returns the plan with the authorized dataset-layer identity
    /// bound to each gated step. Delegates to <see cref="GeoprocessingLayerAccessGuard"/>, which
    /// is composed here rather than injected alongside this type so that all of the submit-time
    /// authorization concerns reach the job service through a single collaborator.
    /// </summary>
    /// <param name="plan">The submitted analysis plan.</param>
    /// <param name="principal">The submitting principal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan to submit, carrying the authorized dataset-layer bindings.</returns>
    public Task<AnalysisPlan> EnsureLayerReadAccessAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
        => _layerAccessGuard.EnsureLayerReadAccessAsync(plan, principal, cancellationToken);

    /// <summary>
    /// Evaluates the caller's authorization for the specified resource/operation and throws
    /// <see cref="GeoprocessingAuthorizationException"/> (after logging the denial) when the
    /// decision is not allowed.
    /// </summary>
    public async Task EnsureAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken = default)
    {
        var decision = await _authEvaluator.EvaluateAsync(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = resourceType,
                Operation = operation
            },
            cancellationToken).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            // Carry the actual denied operation into both the structured security log and the
            // surfaced exception message so a mutating-process denial is distinguishable from a
            // baseline Execute denial rather than reading as a generic "Execute" 403 (#2798).
            GeoprocessingServiceLog.AuthorizationDenied(_logger, resourceType.ToString(), operation.ToString());
            throw new GeoprocessingAuthorizationException(
                decision.RequiresAuthentication,
                decision.RequiresAuthentication
                    ? "Authentication is required for this operation."
                    : $"You do not have permission to perform '{operation}' on {resourceType}.",
                resourceType,
                operation,
                AuthorizationDenialReason.InsufficientGrant);
        }

        // OAuth 2.1 scope narrowing (honua-server#2851). The grant model above decides what the
        // principal MAY do; when the caller authenticated with a bearer token, its scopes can
        // only narrow that — never widen it. Non-OAuth principals (X-API-Key, interactive,
        // dev-bypass) are not scope-governed and pass through untouched. A scope denial is a
        // distinct structured reason from a grant denial so operators can tell the two apart.
        var scopeDecision = _scopeAuthorizer.Evaluate(principal, resourceType, operation);
        if (!scopeDecision.IsAllowed)
        {
            GeoprocessingServiceLog.AuthorizationScopeDenied(_logger, resourceType.ToString(), operation.ToString());
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                scopeDecision.Reason
                    ?? $"The access token's scopes do not permit '{operation}' on {resourceType}.",
                resourceType,
                operation,
                AuthorizationDenialReason.InsufficientScope);
        }
    }

    /// <summary>
    /// Evaluates the approval requirement for the supplied request. Callers own the
    /// contextual logging and exception flow (submit/cancel/validate) so the surfaced
    /// behavior stays identical to the previous inline evaluation.
    /// </summary>
    public ApprovalRequirement EvaluateApproval(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
        => _approvalEvaluator.Evaluate(principal, request);
}

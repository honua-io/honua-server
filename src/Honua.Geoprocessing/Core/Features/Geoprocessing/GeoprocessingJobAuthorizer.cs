// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;

namespace Honua.Geoprocessing;

internal sealed class GeoprocessingJobAuthorizer
{
    private readonly IOperatorAuthorizationEvaluator _authEvaluator;
    private readonly IOperatorApprovalEvaluator _approvalEvaluator;
    private readonly IOperatorScopeAuthorizer _scopeAuthorizer;
    private readonly ILayerAccessAuthorizer _layerAccessAuthorizer;
    private readonly GeoprocessingLayerAccessGuard _layerAccessGuard;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IPrincipalMembershipSource? _principalMembershipSource;
    private readonly ILogger<GeoprocessingJobService> _logger;

    public GeoprocessingJobAuthorizer(
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        IOperatorScopeAuthorizer scopeAuthorizer,
        ILayerAccessAuthorizer layerAccessAuthorizer,
        GeoprocessingLayerAccessGuard layerAccessGuard,
        IHttpContextAccessor httpContextAccessor,
        ILogger<GeoprocessingJobService> logger,
        IPrincipalMembershipSource? principalMembershipSource = null)
    {
        _authEvaluator = authEvaluator;
        _approvalEvaluator = approvalEvaluator;
        _scopeAuthorizer = scopeAuthorizer;
        _layerAccessAuthorizer = layerAccessAuthorizer;
        _layerAccessGuard = layerAccessGuard;
        _httpContextAccessor = httpContextAccessor;
        _principalMembershipSource = principalMembershipSource;
        _logger = logger;
    }

    public JobSecurityContext CaptureSecurityContext(
        ClaimsPrincipal principal,
        RbacOptions options,
        bool membershipManaged = false)
    {
        var tenantContext = _httpContextAccessor.HttpContext?.RequestServices
            .GetService<ITenantContext>();
        return JobSecurityContextCapture.Capture(principal, options, tenantContext, membershipManaged);
    }

    // The snapshot is never taken from the orchestrator or the approval-resume principal, because one carries admin and the other matches no row policy.
    public async Task<JobSecurityContextMembershipResult> ResolveSubmitterSecurityContextAsync(
        ClaimsPrincipal principal,
        JobSecurityContext? inherited,
        bool inherits,
        bool reportSnapshotFallback,
        RbacOptions rbacOptions,
        CancellationToken cancellationToken)
    {
        if (!inherits)
        {
            // Record managed provenance while the submitter is resolvable, so a deferred approval
            // resume on a replica that cannot re-resolve them fails closed (honua-server#3081).
            var membershipManaged = await JobSecurityContextCapture
                .IsManagedMembershipAsync(principal, _principalMembershipSource, cancellationToken)
                .ConfigureAwait(false);
            return new JobSecurityContextMembershipResult(
                CaptureSecurityContext(principal, rbacOptions, membershipManaged),
                JobSecurityContextMembershipStatus.Current);
        }

        var snapshot = inherited ?? throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                "This submission inherits its row/field security identity from a record that predates "
                + "the submitter security context, so the original submitter's row-level security and "
                + "field masking cannot be reconstructed. Resubmit the workflow run or approval request.",
                OperatorResourceType.Process,
                OperatorOperation.Execute);

        var result = await JobSecurityContextCapture
            .RevalidateRoleMembershipAsync(snapshot, _principalMembershipSource, rbacOptions, cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == JobSecurityContextMembershipStatus.SnapshotFallback && reportSnapshotFallback)
        {
            GeoprocessingServiceLog.SubmitterMembershipSnapshotFallback(
                _logger,
                snapshot.PrincipalId ?? "unknown");
        }
        else if (result.Status == JobSecurityContextMembershipStatus.Inactive)
        {
            GeoprocessingServiceLog.SubmitterMembershipInactive(
                _logger,
                snapshot.PrincipalId ?? "unknown");
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                "The submitter identity is no longer active, so this deferred operation cannot resume.",
                OperatorResourceType.Process,
                OperatorOperation.Execute,
                AuthorizationDenialReason.StalePrincipalMembership);
        }
        else if (result.HasRemovedRoles)
        {
            GeoprocessingServiceLog.SubmitterMembershipNoLongerAuthorizes(
                _logger,
                snapshot.PrincipalId ?? "unknown");
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                "The submitter's current role membership no longer includes every role captured "
                + "by this deferred operation, so its row-level security and field masking cannot "
                + "be safely relaxed.",
                OperatorResourceType.Process,
                OperatorOperation.Execute,
                AuthorizationDenialReason.StalePrincipalMembership);
        }
        else if (result.Status == JobSecurityContextMembershipStatus.Changed)
        {
            GeoprocessingServiceLog.SubmitterMembershipRevalidated(
                _logger,
                snapshot.PrincipalId ?? "unknown");
        }

        return result;
    }

    public Task<AnalysisPlan> EnsureLayerReadAccessAsync(
        AnalysisPlan plan,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
        => _layerAccessGuard.EnsureLayerReadAccessAsync(plan, principal, cancellationToken);

    public Task EnsureGPServerExecutionAuthorizedAsync(
        ClaimsPrincipal principal,
        GPServerExecutionContext execution,
        CancellationToken cancellationToken = default)
        => execution.IsGranted(principal)
            ? Task.CompletedTask
            : EnsureAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Execute, cancellationToken);

    public Task EnsureAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorResourceType resourceType,
        OperatorOperation operation,
        CancellationToken cancellationToken = default)
        => EnsureAuthorizedAsync(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = resourceType,
                Operation = operation
            },
            cancellationToken);

    public async Task EnsureAuthorizedAsync(
        ClaimsPrincipal principal,
        OperatorAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        var decision = await _authEvaluator.EvaluateAsync(principal, request, cancellationToken).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            // Carry the actual denied operation into both the structured security log and the
            // surfaced exception message so a mutating-process denial is distinguishable from a
            // baseline Execute denial rather than reading as a generic "Execute" 403 (#2798).
            GeoprocessingServiceLog.AuthorizationDenied(_logger, request.ResourceType.ToString(), request.Operation.ToString());
            throw new GeoprocessingAuthorizationException(
                decision.RequiresAuthentication,
                decision.RequiresAuthentication
                    ? "Authentication is required for this operation."
                    : $"You do not have permission to perform '{request.Operation}' on {request.ResourceType}.",
                request.ResourceType,
                request.Operation,
                AuthorizationDenialReason.InsufficientGrant);
        }

        // OAuth 2.1 scope narrowing (honua-server#2851). The grant model above decides what the
        // principal MAY do; when the caller authenticated with a bearer token, its scopes can
        // only narrow that — never widen it. Non-OAuth principals (X-API-Key, interactive,
        // dev-bypass) are not scope-governed and pass through untouched. A scope denial is a
        // distinct structured reason from a grant denial so operators can tell the two apart.
        var scopeDecision = _scopeAuthorizer.Evaluate(principal, request.ResourceType, request.Operation);
        if (!scopeDecision.IsAllowed)
        {
            GeoprocessingServiceLog.AuthorizationScopeDenied(_logger, request.ResourceType.ToString(), request.Operation.ToString());
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                scopeDecision.Reason
                    ?? $"The access token's scopes do not permit '{request.Operation}' on {request.ResourceType}.",
                request.ResourceType,
                request.Operation,
                AuthorizationDenialReason.InsufficientScope);
        }
    }

    // A layer denial is the same error whether the layer is forbidden, retired, hidden, or missing, so submit is not an existence oracle.
    public async Task EnsurePlanLayerAccessAsync(
        ClaimsPrincipal principal,
        AnalysisPlan plan,
        IProcessCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        foreach (var reference in PlanLayerReferences.Derive(plan, catalog))
        {
            // The parameter declares WHAT the process does to the layer, so each target is gated
            // on the grant that operation actually needs. A destination-only layer is not held
            // to a read the process never performs, and a destructive process is not gated on
            // `insert` — delete-features needs `delete`, calculate-field needs `update`
            // (honua-server#3046 review).
            var operation = ToAuthorizationOperation(reference.Access);
            var isRead = operation == AuthorizationOperation.Query;

            // OAuth scope narrowing applies to the DERIVED operation too, not just the baseline
            // process gate. Without it, an execute-only delegated token (honua.mcp.execute with
            // no honua.mcp.read) could run a layer-sourced plan purely on the strength of the
            // underlying principal's roles — the token's deliberate lack of read authority was
            // never consulted, and a mutating workflow could then sink that data
            // (honua-server#3046 review). Checked before the grant evaluation because a scope
            // denial is a property of the token and needs no catalog lookup.
            // A layer MUTATION here is performed by the process, not by the caller directly, so
            // the scope that delegates it is the mutating-process scope the tier gate already
            // required — not OperatorOperation.Update, which OperatorScopeCatalog maps to no
            // scope except honua.mcp.full. Mapping it to Update made every normally delegated
            // mutating token fail this gate after passing the tier gate, so
            // data-management.calculate-field could not run with the advertised
            // honua.mcp.execute.mutating scope (honua-server#3046 review). Reads stay on Read,
            // which honua.mcp.read grants, so an execute-only token still cannot read a layer.
            var operatorOperation = isRead
                ? OperatorOperation.Read
                : OperatorOperation.ExecuteMutatingProcess;
            var scopeDecision = _scopeAuthorizer.Evaluate(
                principal, OperatorResourceType.Catalog, operatorOperation);
            if (!scopeDecision.IsAllowed)
            {
                GeoprocessingServiceLog.AuthorizationScopeDenied(
                    _logger, OperatorResourceType.Catalog.ToString(), operatorOperation.ToString());
                throw new GeoprocessingAuthorizationException(
                    requiresAuthentication: false,
                    scopeDecision.Reason
                        ?? $"The access token's scopes do not permit '{operatorOperation}' on "
                        + $"{OperatorResourceType.Catalog}.",
                    OperatorResourceType.Catalog,
                    operatorOperation,
                    AuthorizationDenialReason.InsufficientScope);
            }

            var decision = await _layerAccessAuthorizer.AuthorizeLayerAsync(
                principal,
                reference.LayerId,
                operation,
                cancellationToken).ConfigureAwait(false);

            if (decision.IsAllowed)
            {
                continue;
            }

            GeoprocessingServiceLog.LayerAccessDenied(
                _logger,
                reference.LayerId,
                reference.StepId,
                reference.ProcessId);

            throw new GeoprocessingAuthorizationException(
                decision.RequiresAuthentication,
                decision.RequiresAuthentication
                    ? "Authentication is required for this operation."
                    : $"You do not have permission to {DescribeAccess(reference.Access)} layer "
                      + $"{reference.LayerId} referenced by step '{reference.StepId}'.",
                OperatorResourceType.Catalog,
                operatorOperation,
                AuthorizationDenialReason.InsufficientGrant);
        }
    }

    private static AuthorizationOperation ToAuthorizationOperation(ProcessLayerAccess access)
        => access switch
        {
            ProcessLayerAccess.Insert => AuthorizationOperation.Insert,
            ProcessLayerAccess.Update => AuthorizationOperation.Update,
            ProcessLayerAccess.Delete => AuthorizationOperation.Delete,
            // None is filtered out by PlanLayerReferences.Derive and never arrives here; if it
            // ever did, the conservative reading is the strictest, not the loosest.
            ProcessLayerAccess.None => AuthorizationOperation.Delete,
            _ => AuthorizationOperation.Query,
        };

    private static string DescribeAccess(ProcessLayerAccess access)
        => access switch
        {
            ProcessLayerAccess.Insert => "add features to",
            ProcessLayerAccess.Update => "modify features in",
            ProcessLayerAccess.Delete => "delete features from",
            _ => "read",
        };

    public ApprovalRequirement EvaluateApproval(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
        => _approvalEvaluator.Evaluate(principal, request);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
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
    private readonly ILayerAccessAuthorizer _layerAccessAuthorizer;
    private readonly ILogger<GeoprocessingJobService> _logger;

    /// <summary>
    /// Creates the authorization gate over the operator authorization and approval evaluators,
    /// the OAuth scope authorizer that narrows a bearer token's authority to its scopes
    /// (honua-server#2851), and the shared per-layer access authorizer that gates the catalog
    /// layers a submitted plan will read (honua-server#3046).
    /// </summary>
    public GeoprocessingJobAuthorizer(
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        IOperatorScopeAuthorizer scopeAuthorizer,
        ILayerAccessAuthorizer layerAccessAuthorizer,
        ILogger<GeoprocessingJobService> logger)
    {
        _authEvaluator = authEvaluator;
        _approvalEvaluator = approvalEvaluator;
        _scopeAuthorizer = scopeAuthorizer;
        _layerAccessAuthorizer = layerAccessAuthorizer;
        _logger = logger;
    }

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
    /// Enforces per-layer READ authorization for every catalog layer the submitted plan will
    /// touch, evaluated against the SUBMITTING principal before any job record is created
    /// (honua-server#3046).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layer set is derived generically from the process catalog
    /// (<see cref="PlanLayerReferences.Derive"/>), so this covers the layer-sourced executor
    /// family (<c>analytics.*</c>, <c>generalization.*</c>, <c>conversion.feature-project</c>,
    /// the <c>source.honua-layer</c> DAG connector), the submit-time raster-source resolution
    /// that materializes a catalog raster's bytes onto the job spec, and any future process
    /// that declares a <see cref="ProcessParameterValueType.LayerId"/> parameter.
    /// </para>
    /// <para>
    /// A denial produces one generic <see cref="GeoprocessingAuthorizationException"/> whether
    /// the layer is forbidden, retired, hidden from the caller's tenant, or does not exist, so
    /// the submit path is not an oracle for which layer ids exist. Adapters map it onto their
    /// protocol's 401/403 exactly as they already do for the process-execute gates.
    /// </para>
    /// </remarks>
    /// <param name="principal">The submitting principal.</param>
    /// <param name="plan">The plan being submitted.</param>
    /// <param name="catalog">The process catalog declaring each process's parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Maps a declared layer access onto the canonical authorization operation it requires.
    /// One-to-one by construction, so a member added to either enum forces a decision here.
    /// </summary>
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

    /// <summary>Caller-facing verb for a denial, matching the operation that was refused.</summary>
    private static string DescribeAccess(ProcessLayerAccess access)
        => access switch
        {
            ProcessLayerAccess.Insert => "add features to",
            ProcessLayerAccess.Update => "modify features in",
            ProcessLayerAccess.Delete => "delete features from",
            _ => "read",
        };

    /// <summary>
    /// Evaluates the approval requirement for the supplied request. Callers own the
    /// contextual logging and exception flow (submit/cancel/validate) so the surfaced
    /// behavior stays identical to the previous inline evaluation.
    /// </summary>
    public ApprovalRequirement EvaluateApproval(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
        => _approvalEvaluator.Evaluate(principal, request);
}

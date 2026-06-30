// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

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
    private readonly ILogger<GeoprocessingJobService> _logger;

    /// <summary>
    /// Creates the authorization gate over the operator authorization and approval evaluators.
    /// </summary>
    public GeoprocessingJobAuthorizer(
        IOperatorAuthorizationEvaluator authEvaluator,
        IOperatorApprovalEvaluator approvalEvaluator,
        ILogger<GeoprocessingJobService> logger)
    {
        _authEvaluator = authEvaluator;
        _approvalEvaluator = approvalEvaluator;
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

        if (decision.IsAllowed)
        {
            return;
        }

        GeoprocessingServiceLog.AuthorizationDenied(_logger, resourceType.ToString(), operation.ToString());
        throw new GeoprocessingAuthorizationException(decision.RequiresAuthentication);
    }

    /// <summary>
    /// Evaluates the approval requirement for the supplied request. Callers own the
    /// contextual logging and exception flow (submit/cancel/validate) so the surfaced
    /// behavior stays identical to the previous inline evaluation.
    /// </summary>
    public ApprovalRequirement EvaluateApproval(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
        => _approvalEvaluator.Evaluate(principal, request);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// REST-equivalent authorization and approval boundary shared by every
/// hand-authored MCP projection of <c>service.publish</c>.
/// </summary>
internal static class ServicePublishMcpAuthorization
{
    internal const OperationSideEffectClass SideEffectClass = OperationSideEffectClass.CreatesMetadata;
    internal const OperationApprovalModel ApprovalModel = OperationApprovalModel.OperatorGate;

    internal static async Task<string> EnsureAuthorizedAndApprovedAsync(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var authorization = await OperationAdminAuthorization.EvaluateAsync(
            httpContext,
            principal,
            SideEffectClass,
            cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAuthorized)
        {
            throw new GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                message: "Caller is not authorized to invoke admin operations.");
        }

        var gate = httpContext.RequestServices.GetService<OperatorApprovalGate>()
            ?? throw new InvalidOperationException(
                "The operator approval gate is unavailable in this composition.");
        var approval = gate.CheckApproval(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Catalog,
                Operation = OperatorOperation.Publish,
            });
        if (approval.IsRequired)
        {
            if (string.IsNullOrWhiteSpace(approval.PolicyRef))
            {
                throw new InvalidOperationException(
                    "The operator approval evaluator required approval without a policy reference.");
            }

            throw new GeoprocessingApprovalRequiredException(approval.PolicyRef);
        }

        return authorization.AuthorizationOutcome;
    }
}

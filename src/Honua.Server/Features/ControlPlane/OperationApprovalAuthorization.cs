// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Console;
using Microsoft.AspNetCore.Authorization;

namespace Honua.ControlPlane;

internal sealed class OperationApprovalAuthorization(
    IPermissionResolver permissions,
    IAuthorizationService authorization)
{
    internal const string ApprovalScope = "__operations__";

    public async Task<bool> CanDecideAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var actor = ConsolePrincipal.ResolveActorId(principal);
        if (actor is null || !(await authorization.AuthorizeAsync(
                principal, resource: null, AuthenticationExtensions.AdminApprovePolicy).ConfigureAwait(false)).Succeeded)
        {
            return false;
        }

        var roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray();
        var decision = await permissions.AuthorizeAsync(
            actor, roles, ApprovalScope, layer: null, AuthorizationOperation.Approve,
            isAuthenticated: true, cancellationToken).ConfigureAwait(false);
        return decision.IsAllowed || decision.HasNoMatchingGrant;
    }

    public static bool PermitsOperationScope(ClaimsPrincipal principal, OperationProposal proposal)
    {
        if (!OperatorScopeCatalog.IsScopeGoverned(principal))
        {
            return true;
        }

        var legacyId = proposal.Kind is OperationClass.Deploy or OperationClass.Seed
            or OperationClass.AdminConfigChange or OperationClass.MetadataRelease
            or OperationClass.Geoprocess or OperationClass.ServicePublish
            ? LegacyOperationIds.For(proposal.Kind)
            : null;
        var operationId = proposal.OperationId ?? legacyId ?? string.Empty;
        var request = new OperationRequest
        {
            OperationId = operationId,
            GatewayRequest = legacyId is not null && string.Equals(operationId, legacyId, StringComparison.Ordinal)
                ? new OperationGatewayRequest
                {
                    Kind = proposal.Kind,
                    ActionDiscriminator = proposal.ActionDiscriminator ?? proposal.AutonomyMetadata?.ActionDiscriminator,
                }
                : null,
        };
        return OperationScopeMapping.TryResolve(request, out var operation)
            && OperatorScopeCatalog.PermitsOperation(
                OperatorScopeCatalog.CollectRecognizedScopes(principal), operation);
    }
}

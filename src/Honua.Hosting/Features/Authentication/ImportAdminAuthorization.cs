// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Import.Domain;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Canonical caller-authorization decision for file-import mutations, shared by
/// the REST upload and MCP inline-ingest transport adapters.
/// </summary>
public static class ImportAdminAuthorization
{
    /// <summary>
    /// Returns whether the caller has REST-equivalent admin-write authority for
    /// an import mutation. OAuth scopes only narrow that authority.
    /// </summary>
    public static async Task<bool> IsAuthorizedAsync(
        HttpContext transportContext,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transportContext);
        ArgumentNullException.ThrowIfNull(principal);

        if (!await AgentCredentialAuthority.IsCurrentAsync(principal, transportContext.RequestServices, cancellationToken).ConfigureAwait(false) ||
            !await OperationAdminAuthorization.IsAuthorizedAsync(
                transportContext,
                principal,
                OperationSideEffectClass.CreatesMetadata,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!OperatorScopeCatalog.IsScopeGoverned(principal))
        {
            return true;
        }

        var scopes = OperatorScopeCatalog.CollectRecognizedScopes(principal);
        return OperatorScopeCatalog.PermitsOperation(scopes, OperatorOperation.Create);
    }
    // Shared principal-based kernel for canonical task and accepted-job adapters.
    // It supplies the same Admin policy a typed semantic resource, not HttpContext.
    internal static async Task<bool> IsAuthorizedAsync(IServiceProvider services, ClaimsPrincipal principal,
        string targetId, bool requireWrite, CancellationToken cancellationToken,
        OperatorAuthorizationRequest? sealedOperation = null, DatasetImportDestination? destination = null)
    {
        var resource = new SemanticImportAuthorizationResource(targetId, requireWrite, sealedOperation, destination);
        if (!resource.IsValid || !await AgentCredentialAuthority.IsCurrentObserverAsync(principal, services, cancellationToken).ConfigureAwait(false)) return false;
        var operation = resource.OperatorRequest;
        var source = principal;
        if (JobExecutionLeaseTransientAuthority.IsLeasePrincipal(principal))
        {
            if (!JobExecutionLeaseTransientAuthority.AllowsOperator(principal, operation)
                || !await JobExecutionLeaseTransientAuthority.IsCurrentAsync(principal, cancellationToken).ConfigureAwait(false)) return false;
            // This private source principal never crosses the canonical authorization seam.
            source = JobExecutionLeaseTransientAuthority.GetAuthorizationPrincipal(principal);
        }
        if (!(services.GetService<IOperatorScopeAuthorizer>()?.Evaluate(source,
            operation.ResourceType, operation.Operation).IsAllowed ?? false)) return false;
        if (AgentDatasetImportAuthorization.AllowsOperator(source, operation))
            return await AgentCredentialAuthority.IsCurrentObserverAsync(principal, services, cancellationToken).ConfigureAwait(false);
        var authorization = services.GetService<IAuthorizationService>();
        if (authorization is null) return false;
        var result = await authorization.AuthorizeAsync(source, resource, AuthenticationExtensions.AdminPolicy).ConfigureAwait(false);
        if (!result.Succeeded && (requireWrite || !AdminApiKeyPermission.IsAuthorized(source, resource.Method,
            SemanticImportAuthorizationResource.CanonicalPath))) return false;
        if (source.FindFirst("api_key_id") is not null && !AdminApiKeyPermission.IsAuthorized(source,
            resource.Method, SemanticImportAuthorizationResource.CanonicalPath)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        return await AgentCredentialAuthority.IsCurrentObserverAsync(principal, services, cancellationToken).ConfigureAwait(false);
    }

}

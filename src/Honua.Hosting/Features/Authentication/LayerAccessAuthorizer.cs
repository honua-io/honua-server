// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Default <see cref="ILayerAccessAuthorizer"/>: resolves a catalog layer id through the
/// SAME Metadata v2 triple resolution the HTTP layer validators use
/// (<see cref="LayerValidationHelpers.ResolveV2TripleForTenant"/>) and evaluates it with the
/// SAME decision core the HTTP access helpers use
/// (<see cref="AccessPolicyHelpers.EvaluateResourceAccessCoreAsync"/>), so a principal-only
/// caller such as the geoprocessing submit pipeline reaches an identical decision to a
/// protocol query for the same layer (honua-server#3046).
/// </summary>
/// <remarks>
/// Registered as a singleton because its consumers (the geoprocessing job service and its
/// authorizer) are singletons. The per-request seams it needs — the metadata graph provider,
/// the permission resolver, and the tenant rail — are resolved from the AMBIENT request scope
/// when the call happens on a request thread, exactly as the HTTP helpers do. When no request
/// is ambient (workflow orchestration, schedulers, background replay) a fresh scope is created
/// and tenant filtering is skipped: there is no request tenant rail to resolve, and failing
/// closed there would deny every tenant-scoped layer to server-internal callers that never
/// pass through tenant middleware.
/// </remarks>
internal sealed class LayerAccessAuthorizer : ILayerAccessAuthorizer
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Creates the authorizer over the ambient-request accessor and the scope factory used
    /// for non-request callers.
    /// </summary>
    public LayerAccessAuthorizer(IHttpContextAccessor httpContextAccessor, IServiceScopeFactory scopeFactory)
    {
        _httpContextAccessor = httpContextAccessor;
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<AccessDecision> AuthorizeLayerAsync(
        ClaimsPrincipal principal,
        int layerId,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            return await EvaluateAsync(
                httpContext.RequestServices,
                principal,
                TenantScopeHelpers.ResolveRequestTenantId(httpContext),
                applyTenantScope: true,
                layerId,
                operation,
                cancellationToken).ConfigureAwait(false);
        }

        using var scope = _scopeFactory.CreateScope();
        return await EvaluateAsync(
            scope.ServiceProvider,
            principal,
            tenantId: scope.ServiceProvider.GetService<ITenantContext>()?.TenantId,
            applyTenantScope: false,
            layerId,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AccessDecision> EvaluateAsync(
        IServiceProvider services,
        ClaimsPrincipal principal,
        string? tenantId,
        bool applyTenantScope,
        int layerId,
        AuthorizationOperation operation,
        CancellationToken cancellationToken)
    {
        var provider = services.GetService<IMetadataV2GraphProvider>();
        if (provider is null)
        {
            // No catalog is configured in this deployment, so there is no layer to
            // authorize against and no layer data a job could read through it.
            return AccessDecision.Forbidden(DenialReason);
        }

        var snapshot = await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var (publication, resource, service) = LayerValidationHelpers.ResolveV2TripleForTenant(
            snapshot,
            layerId,
            requiredProtocol: null,
            tenantId,
            applyTenantScope);

        if (publication is null || resource is null ||
            LayerValidationHelpers.IsRetired(publication) || LayerValidationHelpers.IsRetired(resource))
        {
            // Unresolvable layer ids (unknown, retired, or hidden from the caller's tenant)
            // return the SAME denial as a permission failure so the check cannot be used to
            // enumerate which layer ids exist.
            return AccessDecision.Forbidden(DenialReason);
        }

        return await AccessPolicyHelpers.EvaluateResourceAccessCoreAsync(
            services,
            principal,
            tenantId,
            applyTenantScope,
            resource,
            service,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Internal denial reason recorded for unresolvable layers. Never surfaced verbatim to a
    /// client: callers map every denial onto one generic authorization failure.
    /// </summary>
    private const string DenialReason = "The layer is not accessible to this principal.";
}

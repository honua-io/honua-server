// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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

        // A plan's layer id can resolve two different ways, and the executor does not
        // necessarily use the one this gate would pick. `source.honua-layer` hands the id
        // straight to IStreamingFeatureStore.StreamFeaturesAsync, whose security lookup keys on
        // ResourcesByStorageLayerId, while the publication triple resolves it as a
        // service-local publication index. Where a resource's storage layer id differs from its
        // publication index, authorizing only the publication could clear a job that then reads
        // a DIFFERENT, restricted storage layer (honua-server#3046 review).
        //
        // Both candidates are therefore evaluated and BOTH must allow. Requiring the
        // intersection is the only reading that is correct without knowing which index the
        // executor for this particular process uses, and it can only ever deny more than the
        // previous behaviour, never less.
        var candidates = new List<(MetadataV2Resource Resource, MetadataV2Service? Service)>(2);

        var (publication, publicationResource, publicationService) =
            LayerValidationHelpers.ResolveV2TripleForTenant(
                snapshot,
                layerId,
                requiredProtocol: null,
                tenantId,
                applyTenantScope);

        if (publication is not null && publicationResource is not null &&
            !LayerValidationHelpers.IsRetired(publication) && !LayerValidationHelpers.IsRetired(publicationResource))
        {
            candidates.Add((publicationResource, publicationService));
        }

        if (snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var storageResource)
            && !LayerValidationHelpers.IsRetired(storageResource)
            && !candidates.Any(candidate => string.Equals(
                candidate.Resource.Metadata.Id, storageResource.Metadata.Id, StringComparison.Ordinal)))
        {
            // The publication that owns this resource supplies the service context the decision
            // core needs; a resource with no live publication is still authorized, just without
            // service-level policy.
            var storagePublication = snapshot.Index.PublicationsByResource[storageResource.Metadata.Id]
                .FirstOrDefault(candidate => !LayerValidationHelpers.IsRetired(candidate));
            var storageService = storagePublication is null
                ? null
                : snapshot.Index.ServicesById.GetValueOrDefault(storagePublication.ServiceId);

            candidates.Add((storageResource, storageService));
        }

        if (candidates.Count == 0)
        {
            // Unresolvable layer ids (unknown, retired, or hidden from the caller's tenant)
            // return the SAME denial as a permission failure so the check cannot be used to
            // enumerate which layer ids exist.
            return AccessDecision.Forbidden(DenialReason);
        }

        foreach (var (candidateResource, candidateService) in candidates)
        {
            var decision = await AccessPolicyHelpers.EvaluateResourceAccessCoreAsync(
                services,
                principal,
                tenantId,
                applyTenantScope,
                candidateResource,
                candidateService,
                operation,
                cancellationToken).ConfigureAwait(false);

            if (!decision.IsAllowed)
            {
                return decision;
            }
        }

        return AccessDecision.Allowed();
    }

    /// <summary>
    /// Internal denial reason recorded for unresolvable layers. Never surfaced verbatim to a
    /// client: callers map every denial onto one generic authorization failure.
    /// </summary>
    private const string DenialReason = "The layer is not accessible to this principal.";
}

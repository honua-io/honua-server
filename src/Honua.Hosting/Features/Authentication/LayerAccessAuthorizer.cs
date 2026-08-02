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
/// and the tenant is taken from the PRINCIPAL: the deferred-submission lanes authorize a
/// restored author/submitter snapshot that carries the tenant it was captured with, so tenant
/// visibility stays enforced for user-attributed background checks. Only a caller with no
/// tenant at all — a tenant-less deployment, or a server-internal identity that never passed
/// through tenant middleware — skips the filter, because scoping on a null tenant would deny
/// every tenant-scoped layer rather than just the foreign ones.
/// </remarks>
internal sealed class LayerAccessAuthorizer : ILayerAccessAuthorizer
{
    /// <summary>Tenant claim type mirrored from the portal-token grammar.</summary>
    private const string TenantClaimType = "tenant_id";

    /// <summary>
    /// Azure/OIDC tenant claim the tenant middleware also accepts. Reading only
    /// <c>tenant_id</c> left an OIDC principal that carries just <c>tid</c> looking
    /// tenant-less here, which disabled tenant scoping on exactly the background checks this
    /// gate exists to constrain (honua-server#3046 review).
    /// </summary>
    private const string AzureTenantClaimType = "tid";

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
            // Deferred approval execution can run inside the APPROVER's request while
            // authorizing the restored SUBMITTER. The supplied principal owns the resource
            // decision, so its captured tenant must win over the ambient request tenant.
            var authorizationTenantId = ResolveAuthorizationTenantId(
                principal,
                TenantScopeHelpers.ResolveRequestTenantId(httpContext));

            return await EvaluateAsync(
                httpContext.RequestServices,
                principal,
                authorizationTenantId,
                applyTenantScope: true,
                layerId,
                operation,
                cancellationToken).ConfigureAwait(false);
        }

        using var scope = _scopeFactory.CreateScope();

        // No ambient HttpContext, but a background check is still attributed to a real user: the
        // restored author/submitter snapshot carries the tenant it was captured with. Leaving
        // tenant filtering off here meant a stored numeric id later rebound to ANOTHER tenant's
        // layer could be cleared by a matching role or a permissive access policy, and the
        // executor would then read that foreign layer through its global storage address. The
        // principal's own tenant wins; the ambient scope value is the fallback for a background
        // caller that is not user-attributed (honua-server#3046 review).
        var tenantId = ResolveAuthorizationTenantId(
            principal,
            scope.ServiceProvider.GetService<ITenantContext>()?.TenantId);

        return await EvaluateAsync(
            scope.ServiceProvider,
            principal,
            tenantId,
            // A tenant-less deployment resolves nothing here, and scoping on a null tenant would
            // hide every layer rather than the foreign ones. Scope only when a tenant is known.
            applyTenantScope: !string.IsNullOrWhiteSpace(tenantId),
            layerId,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the tenant for the principal being authorized. The ambient tenant is only a
    /// fallback for a tenant-less principal; it may belong to an approver or orchestrator.
    /// </summary>
    internal static string? ResolveAuthorizationTenantId(
        ClaimsPrincipal principal,
        string? ambientTenantId)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.FindFirstValue(TenantClaimType)
            ?? principal.FindFirstValue(AzureTenantClaimType)
            ?? ambientTenantId;
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

        // A plan's layer id can resolve two different ways, and only ONE of them is what the
        // job goes on to read. Every layer-sourced process reaches its data through
        // `source.honua-layer`, which hands the id straight to
        // IStreamingFeatureStore.StreamFeaturesAsync; the provider stores' security lookup keys
        // on ResourcesByStorageLayerId. So the storage resource is the one the executor
        // actually accesses, and it is the one this gate must authorize.
        //
        // The publication triple resolves the same integer as a SERVICE-LOCAL publication
        // index. Those are separate namespaces and small values collide constantly — a
        // publication index of 1 exists in most services — so requiring both to allow denied
        // jobs whose only sin was that an unrelated, restricted publication happened to share
        // the number. A collision does not make that publication an input to this plan
        // (honua-server#3046 review).
        // An id absent from the storage index is REFUSED, never resolved some other way. Falling
        // back to the publication triple authorized whichever publication happened to share the
        // integer, while `HonuaLayerDagSource` still passes it unchanged to StreamFeaturesAsync
        // and the store queries by storage id — so the gate would have cleared one resource and
        // the executor read another (honua-server#3046 review).
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var storageResource)
            || LayerValidationHelpers.IsRetired(storageResource))
        {
            // Unresolvable layer ids (unknown, retired, or not a storage layer at all) return the
            // SAME denial as a permission failure so the check cannot be used to enumerate which
            // layer ids exist.
            return AccessDecision.Forbidden(DenialReason);
        }

        // A resource can be published through SEVERAL services, each with its own policy. Taking
        // the first live publication made the decision depend on metadata array order: a caller
        // holding a grant through service B was denied because restricted service A happened to
        // sort first, even though the synchronous query surface would have allowed it through B.
        // Every live publication is therefore evaluated and ANY grant admits — matching what the
        // caller could already do interactively (honua-server#3046 review).
        var candidates = new List<(MetadataV2Resource Resource, MetadataV2Service? Service)>();

        foreach (var publication in snapshot.Index.PublicationsByResource[storageResource.Metadata.Id]
            .Where(candidate => !LayerValidationHelpers.IsRetired(candidate)))
        {
            candidates.Add((storageResource, snapshot.Index.ServicesById.GetValueOrDefault(publication.ServiceId)));
        }

        if (candidates.Count == 0)
        {
            // A resource with no live publication is still authorized, just without service-level
            // policy — the executor reads it by storage id regardless of publication state.
            candidates.Add((storageResource, null));
        }

        AccessDecision? lastDenial = null;

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

            if (decision.IsAllowed)
            {
                return decision;
            }

            lastDenial = decision;
        }

        // Denied through every publication of the resource, so the caller has no route to it.
        return lastDenial ?? AccessDecision.Forbidden(DenialReason);
    }

    /// <summary>
    /// Internal denial reason recorded for unresolvable layers. Never surfaced verbatim to a
    /// client: callers map every denial onto one generic authorization failure.
    /// </summary>
    private const string DenialReason = "The layer is not accessible to this principal.";
}

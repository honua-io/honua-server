// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Catalog;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;

namespace Honua.Core.Features.Catalog;

public sealed class CatalogReader(IMetadataV2GraphProvider graphProvider, CatalogGraphProjection projection,
    ICatalogProtocolProvider protocolProvider, ITenantContext tenantContext) : ICatalogReader
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    public async Task<IReadOnlyList<CatalogSummary>> ListAsync(CatalogReadRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return Catalogs(snapshot, request.Principal).Select(catalog => new CatalogSummary(catalog.Metadata.Id,
            catalog.Metadata.Title ?? catalog.Metadata.Name, catalog.Configuration!.Audience.Kind,
            null)).ToArray();
    }

    public async Task<CatalogReadResult?> ReadAsync(CatalogReadRequest request, string catalogId, CancellationToken cancellationToken = default)
    {
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var catalog = Catalogs(snapshot, request.Principal).SingleOrDefault(catalog => catalog.Metadata.Id == catalogId);
        if (catalog is null) return null;
        var protocols = await protocolProvider.GetProtocolsAsync(request.Principal, cancellationToken).ConfigureAwait(false);
        var metadataRequest = new PublishedMetadataRequest(snapshot, request.Principal, request.BaseUrl)
        {
            ReadCeiling = catalog.Configuration!.Audience.Kind == "public" ? Anonymous : null
        };
        var candidates = await projection.ProjectAsync(metadataRequest, protocols, cancellationToken).ConfigureAwait(false);
        var prefix = $"/api/v1/catalogs/{Uri.EscapeDataString(catalogId)}/items/";
        var items = new CatalogMembershipPolicy(catalog.Configuration).Apply(candidates)
            .Select(item => item with { DetailHref = prefix + item.Kind + "?id=" + Uri.EscapeDataString(item.Id) }).ToArray();
        return new(new()
        {
            Id = catalogId, Title = catalog.Metadata.Title ?? catalog.Metadata.Name,
            Description = catalog.Metadata.Description, Audience = catalog.Configuration.Audience.Kind,
            Revision = snapshot.Etag
        }, items) { Snapshot = snapshot };
    }

    private IEnumerable<MetadataV2Catalog> Catalogs(MetadataV2GraphSnapshot snapshot, ClaimsPrincipal principal)
        => snapshot.Graph.Catalogs.DistinctBy(catalog => catalog.Metadata.Id)
            .Where(catalog => catalog.Target == "discovery" && catalog.Configuration is not null &&
                catalog.Status?.Lifecycle == MetadataV2LifecycleStatus.Active &&
                MetadataV2TenantVisibility.IsVisibleToTenant(catalog.Metadata.Tenant, tenantContext.TenantId) &&
                CatalogMembershipPolicy.AllowsAudience(catalog.Configuration.Audience, principal));
}

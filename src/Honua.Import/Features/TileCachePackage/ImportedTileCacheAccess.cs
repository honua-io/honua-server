// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Import.TileCachePackage;

/// <summary>
/// A publication a served tile cache is bound to, resolved from the current metadata graph.
/// </summary>
internal sealed record ImportedTileCacheSource(
    MetadataV2Publication Publication,
    MetadataV2Resource Resource,
    MetadataV2Service Service);

/// <summary>
/// Read authorization for tiles served out of the tile catalog under <c>/tiles/imported</c>.
/// A catalog row records only the logical layer identifier it was written for, by the package
/// importer or by the WMTS tile-cache exporter alike. That identifier is bound to the layer it
/// names in the current metadata graph, and the caller must hold the same query grant the
/// layer's own tile endpoints require. A row that names no layer, names more than one layer,
/// or names a layer hidden from the request tenant is not served.
/// </summary>
internal static class ImportedTileCacheAccess
{
    /// <summary>
    /// Whether the current caller may read tiles from the cache. Fails closed: an unbound,
    /// ambiguous, retired, tenant-hidden or access-denied origin returns <see langword="false"/>.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="layerIdentifier">The layer identifier recorded on the cache row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when every bound publication grants the caller query access.</returns>
    internal static async Task<bool> CanReadAsync(
        HttpContext context,
        string? layerIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(layerIdentifier))
        {
            return false;
        }

        var graphProvider = context.RequestServices.GetService<IMetadataV2GraphProvider>();
        if (graphProvider is null)
        {
            return false;
        }

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var sources = ResolveSources(
            snapshot,
            layerIdentifier,
            (publication, resource, service) => TenantScopeHelpers.IsPublicationVisible(context, publication, resource, service));
        if (sources.Count == 0)
        {
            return false;
        }

        // Tiles are a read surface: the same per-operation query grant (with the coarse
        // AccessPolicy fallback) the layer's own tile endpoints evaluate. Every publication
        // the identifier binds to must allow the caller.
        foreach (var source in sources)
        {
            if (!await AccessPolicyHelpers.IsResourceAccessibleAsync(
                    context,
                    source.Resource,
                    source.Service,
                    AuthorizationOperation.Query,
                    cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Binds a cache's layer identifier to the routable, visible publications it names. The
    /// identifier matches a publication's advertised collection id, the publication id or the
    /// resource id. Display titles are not identifiers and never match. When the matches span
    /// more than one resource the binding is ambiguous, and when any match is hidden from the
    /// request tenant the cache is not visible; both return nothing.
    /// </summary>
    /// <param name="snapshot">The current metadata graph snapshot.</param>
    /// <param name="layerIdentifier">The layer identifier recorded on the cache row.</param>
    /// <param name="isVisible">Tenant visibility predicate for the request.</param>
    /// <returns>The bound publications, or an empty list when the origin does not resolve.</returns>
    internal static IReadOnlyList<ImportedTileCacheSource> ResolveSources(
        MetadataV2GraphSnapshot snapshot,
        string layerIdentifier,
        Func<MetadataV2Publication, MetadataV2Resource, MetadataV2Service, bool> isVisible)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(isVisible);

        if (string.IsNullOrWhiteSpace(layerIdentifier))
        {
            return [];
        }

        var matches = new List<ImportedTileCacheSource>();
        foreach (var publication in snapshot.Graph.Publications)
        {
            if (!snapshot.IsRoutable(publication))
            {
                continue;
            }

            var resource = snapshot.ResolveResource(publication);
            if (resource is null ||
                !snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
                !service.IsRoutable())
            {
                continue;
            }

            if (!string.Equals(LayerValidationHelpers.AdvertisedCollectionId(publication, resource), layerIdentifier, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(publication.Metadata.Id, layerIdentifier, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(resource.Metadata.Id, layerIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add(new ImportedTileCacheSource(publication, resource, service));
        }

        if (matches.Count == 0)
        {
            return [];
        }

        var resourceId = matches[0].Resource.Metadata.Id;
        for (var i = 1; i < matches.Count; i++)
        {
            if (!string.Equals(matches[i].Resource.Metadata.Id, resourceId, StringComparison.Ordinal))
            {
                return [];
            }
        }

        // The catalog row carries no tenant. Visibility is checked after the ambiguity rule, so
        // a same-named layer in another tenant makes the identifier ambiguous instead of letting
        // the visible one stand in for it.
        foreach (var match in matches)
        {
            if (!isVisible(match.Publication, match.Resource, match.Service))
            {
                return [];
            }
        }

        return matches;
    }
}

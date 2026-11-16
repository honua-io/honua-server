// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Import.TileCachePackage;

internal sealed record ImportedTileCacheSource(
    MetadataV2Layer Publication,
    MetadataV2Object Resource,
    MetadataV2Service Service);

internal static class ImportedTileCacheAccess
{
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

    internal static IReadOnlyList<ImportedTileCacheSource> ResolveSources(
        MetadataV2GraphSnapshot snapshot,
        string layerIdentifier,
        Func<MetadataV2Layer, MetadataV2Object, MetadataV2Service, bool> isVisible)
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Stac.Services;

/// <summary>
/// Metadata v2 lookup helpers scoped to the STAC protocol family. Collapses the
/// "STAC-enabled publication walk + visible-layer filtering" that catalog, collection,
/// item, and search endpoints share into a single typed call, projecting a
/// <see cref="ResolvedStacPublication"/> triple that handlers consume directly.
/// </summary>
internal static class StacV2Lookups
{
    /// <summary>
    /// Resolved STAC publication view: the canonical resource being published, the
    /// matching <see cref="MetadataV2Publication"/>, the publishing service, and the
    /// integer service-local layer index used to address the underlying feature store.
    /// </summary>
    /// <remarks>
    /// <see cref="LayerIndex"/> is required to be present on every STAC publication; the
    /// resolver skips publications without one because the data path
    /// (<see cref="Honua.Core.Features.FeatureStore.Abstractions.IFeatureReader"/>) is
    /// still keyed on a numeric layer id. This matches the
    /// <c>layer-index identity</c> open question called out by the cutover plan.
    /// </remarks>
    internal readonly record struct ResolvedStacPublication(
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        MetadataV2Service Service,
        int LayerIndex);

    /// <summary>
    /// Enumerates the STAC publications visible to the caller. A publication is visible
    /// when it lives on a service exposing the STAC protocol
    /// and the access policy on the resource (and the service it is published through)
    /// allows the current principal to read it.
    /// </summary>
    public static async Task<ResolvedStacPublication[]> ResolveVisibleStacPublicationsAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);

        var stacServiceIds = snapshot.Graph.Services
            .Where(StacProtocolConstants.IsEnabled)
            .Select(s => s.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (stacServiceIds.Count == 0)
        {
            return [];
        }

        var resolved = new List<ResolvedStacPublication>();
        foreach (var pub in snapshot.Graph.Publications)
        {
            if (!pub.LayerIndex.HasValue)
            {
                continue;
            }
            if (!IsStacCollectionPublication(pub))
            {
                continue;
            }
            if (!stacServiceIds.Contains(pub.ServiceId))
            {
                continue;
            }
            if (!snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service))
            {
                continue;
            }

            var resource = snapshot.ResolveResource(pub);
            if (resource is null)
            {
                continue;
            }

            if (!AccessPolicyHelpers.IsResourceAccessible(context, resource, service))
            {
                continue;
            }

            resolved.Add(new ResolvedStacPublication(pub, resource, service, pub.LayerIndex.Value));
        }

        // Deterministic order keyed on layer index keeps catalog child links stable.
        resolved.Sort(static (a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
        return resolved.ToArray();
    }

    /// <summary>
    /// Resolves a single STAC publication by its STAC collection id. The collection id
    /// matches against the publication's service-local id, path, machine name, or graph
    /// id (case-insensitive). When the collection id parses to an integer, that integer
    /// is matched against <see cref="MetadataV2Publication.LayerIndex"/> as a fallback so
    /// the v1 numeric-collection-id contract continues to resolve.
    /// </summary>
    public static async Task<ResolvedStacPublication?> ResolveStacPublicationAsync(
        HttpContext context,
        string collectionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return null;
        }

        var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);

        int? numericId = int.TryParse(
            collectionId.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
                ? parsed
                : null;

        foreach (var pub in snapshot.Graph.Publications)
        {
            if (!pub.LayerIndex.HasValue)
            {
                continue;
            }
            if (!IsStacCollectionPublication(pub))
            {
                continue;
            }

            if (!snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service))
            {
                continue;
            }
            if (!StacProtocolConstants.IsEnabled(service))
            {
                continue;
            }

            if (!MatchesCollectionId(pub, collectionId, numericId))
            {
                continue;
            }

            var resource = snapshot.ResolveResource(pub);
            if (resource is null)
            {
                continue;
            }

            return new ResolvedStacPublication(pub, resource, service, pub.LayerIndex.Value);
        }

        return null;
    }

    private static bool MatchesCollectionId(MetadataV2Publication publication, string collectionId, int? numericId)
    {
        if (string.Equals(publication.ServiceLocalId, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Path, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publication.Metadata.Id, collectionId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return numericId.HasValue && publication.LayerIndex == numericId.Value;
    }

    private static bool IsStacCollectionPublication(MetadataV2Publication publication)
        => publication.PublicationType == MetadataV2PublicationType.StacCollection;

    private static async Task<MetadataV2GraphSnapshot> GetSnapshotAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        return await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }
}

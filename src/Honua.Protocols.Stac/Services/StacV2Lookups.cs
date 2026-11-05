// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Stac.Services;

internal static class StacV2Lookups
{
    internal readonly record struct ResolvedStacPublication(
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        MetadataV2Service Service,
        int LayerIndex,
        MetadataV2GraphSnapshot Snapshot)
    {
        public int? StorageLayerId => Snapshot.ResolveStorageLayerId(Publication, Resource);
    }

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
            if (!snapshot.IsRoutable(pub))
            {
                continue;
            }

            if (!TenantScopeHelpers.IsPublicationVisible(context, pub, resource, service))
            {
                continue;
            }

            if (!AccessPolicyHelpers.IsResourceAccessible(context, resource, service))
            {
                continue;
            }

            resolved.Add(new ResolvedStacPublication(pub, resource, service, pub.LayerIndex.Value, snapshot));
        }

        // Deterministic order keyed on layer index keeps catalog child links stable.
        resolved.Sort(static (a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
        return resolved.ToArray();
    }

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
            if (!snapshot.IsRoutable(pub))
            {
                continue;
            }

            if (!TenantScopeHelpers.IsPublicationVisible(context, pub, resource, service))
            {
                continue;
            }

            return new ResolvedStacPublication(pub, resource, service, pub.LayerIndex.Value, snapshot);
        }

        return null;
    }

    internal static bool MatchesCollectionId(MetadataV2Publication publication, string collectionId, int? numericId)
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

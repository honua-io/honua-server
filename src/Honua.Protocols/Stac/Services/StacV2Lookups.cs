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
        MetadataV2Layer Publication,
        MetadataV2Object Resource,
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

    internal readonly record struct StacPublicationResolution(
        ResolvedStacPublication? Publication,
        string? AmbiguityError);

    public static async Task<ResolvedStacPublication?> ResolveStacPublicationAsync(
        HttpContext context,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var result = await ResolveStacPublicationResultAsync(context, collectionId, cancellationToken)
            .ConfigureAwait(false);
        return result.AmbiguityError is null ? result.Publication : null;
    }

    public static async Task<StacPublicationResolution> ResolveStacPublicationResultAsync(
        HttpContext context,
        string collectionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return default;
        }

        var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        int? numericId = int.TryParse(
            collectionId.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
                ? parsed
                : null;

        var visibleStac = CollectVisibleStacPublications(context, snapshot);
        if (numericId.HasValue)
        {
            var byStorage = visibleStac
                .Where(item => snapshot.ResolveStorageLayerId(item.Publication, item.Resource) == numericId.Value)
                .ToArray();
            if (byStorage.Length > 0)
            {
                return new StacPublicationResolution(byStorage[0], null);
            }
        }

        var matches = visibleStac
            .Where(item => MatchesCollectionId(item.Publication, collectionId, numericId))
            .ToArray();
        return numericId.HasValue
            ? SelectNumericStacResolution(matches, collectionId)
            : SelectNamedStacResolution(matches);
    }

    private static StacPublicationResolution SelectNamedStacResolution(ResolvedStacPublication[] matches)
        => matches.Length == 0 ? default : new StacPublicationResolution(matches[0], null);

    private static StacPublicationResolution SelectNumericStacResolution(
        ResolvedStacPublication[] matches,
        string collectionId)
    {
        if (matches.Length == 0)
        {
            return default;
        }

        if (matches.Select(static m => m.Resource.Metadata.Id).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            var serviceNames = matches
                .Select(static m => $"'{m.Service.Metadata.Name}'")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase);
            return new StacPublicationResolution(
                null,
                $"STAC collection '{collectionId}' is ambiguous across multiple services ({string.Join(", ", serviceNames)}). Use the storage layer id or collection name.");
        }

        return new StacPublicationResolution(matches[0], null);
    }

    private static List<ResolvedStacPublication> CollectVisibleStacPublications(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot)
    {
        var results = new List<ResolvedStacPublication>();
        foreach (var pub in snapshot.Graph.Publications)
        {
            if (!pub.LayerIndex.HasValue || !IsStacCollectionPublication(pub))
            {
                continue;
            }

            if (!snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service) ||
                !StacProtocolConstants.IsEnabled(service))
            {
                continue;
            }

            var resource = snapshot.ResolveResource(pub);
            if (resource is null || !snapshot.IsRoutable(pub) ||
                !TenantScopeHelpers.IsPublicationVisible(context, pub, resource, service))
            {
                continue;
            }

            results.Add(new ResolvedStacPublication(pub, resource, service, pub.LayerIndex.Value, snapshot));
        }

        return results;
    }

    internal static bool MatchesCollectionId(MetadataV2Layer publication, string collectionId, int? numericId)
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

    private static bool IsStacCollectionPublication(MetadataV2Layer publication)
        => publication.PublicationType == MetadataV2LayerType.StacCollection;

    private static async Task<MetadataV2GraphSnapshot> GetSnapshotAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        return await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }
}

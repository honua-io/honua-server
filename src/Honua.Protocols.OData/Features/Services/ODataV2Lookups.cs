// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.OData.Services;

internal static class ODataV2Lookups
{
    internal readonly record struct ResolvedODataPublication(
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        MetadataV2Service Service,
        int LayerIndex,
        int StorageLayerId);

    public static async Task<ResolvedODataPublication[]> ResolveVisibleODataPublicationsAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var resolved = new List<ResolvedODataPublication>();
        var publications = TenantScopeHelpers.FilterVisiblePublications(context, snapshot, snapshot.Graph.Publications).ToArray();
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMapV2(snapshot, ODataProtocolConstants.ProtocolName, publications);
        var selectedPublicationIds = SelectODataPublicationIds(snapshot, primaryServices, publications);

        foreach (var pub in publications)
        {
            if (!selectedPublicationIds.Contains(pub.Metadata.Id))
            {
                continue;
            }
            if (!pub.LayerIndex.HasValue)
            {
                continue;
            }
            if (!snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service))
            {
                continue;
            }
            if (!IsPrimaryPublicationForLayer(pub, service, primaryServices))
            {
                continue;
            }
            if (!ODataProtocolConstants.IsEnabled(service))
            {
                continue;
            }

            var resource = snapshot.ResolveResource(pub);
            if (!snapshot.IsRoutable(pub))
            {
                continue;
            }

            var storageLayerId = ResolveStorageLayerId(snapshot, pub, resource!);
            if (!storageLayerId.HasValue)
            {
                continue;
            }

            if (!AccessPolicyHelpers.IsResourceAccessible(context, resource!, service))
            {
                continue;
            }

            resolved.Add(new ResolvedODataPublication(pub, resource!, service, pub.LayerIndex.Value, storageLayerId.Value));
        }

        resolved.Sort(static (a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
        return resolved.ToArray();
    }

    public static async Task<ResolvedODataPublication[]> ResolveAllODataPublicationsAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var resolved = new List<ResolvedODataPublication>();
        var publications = TenantScopeHelpers.FilterVisiblePublications(context, snapshot, snapshot.Graph.Publications).ToArray();
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMapV2(snapshot, ODataProtocolConstants.ProtocolName, publications);
        var selectedPublicationIds = SelectODataPublicationIds(snapshot, primaryServices, publications);

        foreach (var pub in publications)
        {
            if (!selectedPublicationIds.Contains(pub.Metadata.Id))
            {
                continue;
            }
            if (!pub.LayerIndex.HasValue)
            {
                continue;
            }
            if (!snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service))
            {
                continue;
            }
            if (!IsPrimaryPublicationForLayer(pub, service, primaryServices))
            {
                continue;
            }
            if (!ODataProtocolConstants.IsEnabled(service))
            {
                continue;
            }

            var resource = snapshot.ResolveResource(pub);
            if (!snapshot.IsRoutable(pub))
            {
                continue;
            }

            var storageLayerId = ResolveStorageLayerId(snapshot, pub, resource!);
            if (!storageLayerId.HasValue)
            {
                continue;
            }

            resolved.Add(new ResolvedODataPublication(pub, resource!, service, pub.LayerIndex.Value, storageLayerId.Value));
        }

        resolved.Sort(static (a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
        return resolved.ToArray();
    }

    public static async Task<int?> ResolveStorageLayerIdAsync(
        HttpContext context,
        MetadataV2Publication? publication,
        MetadataV2Resource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);

        var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        return ResolveStorageLayerId(snapshot, publication, resource);
    }

    internal static int? ResolveStorageLayerId(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication? publication,
        MetadataV2Resource resource)
        => publication is null
            ? snapshot.ResolveStorageLayerId(resource)
            : snapshot.ResolveStorageLayerId(publication, resource);

    private static bool IsPrimaryPublicationForLayer(
        MetadataV2Publication publication,
        MetadataV2Service service,
        IReadOnlyDictionary<int, MetadataV2Service> primaryServices)
    {
        return publication.LayerIndex.HasValue &&
            primaryServices.TryGetValue(publication.LayerIndex.Value, out var primaryService) &&
            string.Equals(primaryService.Metadata.Id, service.Metadata.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> SelectODataPublicationIds(
        MetadataV2GraphSnapshot snapshot,
        IReadOnlyDictionary<int, MetadataV2Service> primaryServices,
        IEnumerable<MetadataV2Publication> publications)
    {
        return publications
            .Where(publication => publication.LayerIndex.HasValue)
            .Where(publication => snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) &&
                IsPrimaryPublicationForLayer(publication, service, primaryServices) &&
                ODataProtocolConstants.IsEnabled(service) &&
                snapshot.IsRoutable(publication))
            .GroupBy(publication => (publication.ServiceId, LayerIndex: publication.LayerIndex!.Value))
            .Select(group => group.FirstOrDefault(publication =>
                    ServiceProtocols.IsPreferredPublicationType(
                        ServiceProtocols.OData,
                        publication.PublicationType)) ??
                group.First())
            .Select(publication => publication.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<MetadataV2GraphSnapshot> GetSnapshotAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        return await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }
}

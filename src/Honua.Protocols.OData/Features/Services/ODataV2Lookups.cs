// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Metadata v2 lookup helpers scoped to the OData protocol family. Collapses the
/// "OData-enabled publication walk + visible-layer filtering" used by the OData service
/// document, $metadata, and layer collection endpoints into a single typed call.
/// </summary>
internal static class ODataV2Lookups
{
    /// <summary>
    /// Resolved OData publication view: the canonical resource being published, the
    /// matching publication, the publishing service, and the integer service-local
    /// layer index used to address the underlying feature store.
    /// </summary>
    /// <remarks>
    /// <see cref="LayerIndex"/> is required to be present on every OData publication; the
    /// resolver skips publications without one because the data path
    /// (<see cref="Honua.Core.Features.FeatureStore.Abstractions.IFeatureReader"/>) is still
    /// keyed on a numeric layer id. This matches the layer-index identity open question
    /// called out by the cutover plan.
    /// </remarks>
    internal readonly record struct ResolvedODataPublication(
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        MetadataV2Service Service,
        int LayerIndex,
        int StorageLayerId);

    /// <summary>
    /// Returns the OData publications visible to the caller. A publication is OData-enabled
    /// when its service protocol list contains the OData protocol name. The visible
    /// subset adds the access policy check.
    /// </summary>
    public static async Task<ResolvedODataPublication[]> ResolveVisibleODataPublicationsAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var resolved = new List<ResolvedODataPublication>();
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMapV2(snapshot, ODataProtocolConstants.ProtocolName);

        foreach (var pub in snapshot.Graph.Publications)
        {
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
            if (resource is null)
            {
                continue;
            }

            var storageLayerId = ResolveStorageLayerId(snapshot, pub, resource);
            if (!storageLayerId.HasValue)
            {
                continue;
            }

            if (!AccessPolicyHelpers.IsResourceAccessible(context, resource, service))
            {
                continue;
            }

            resolved.Add(new ResolvedODataPublication(pub, resource, service, pub.LayerIndex.Value, storageLayerId.Value));
        }

        resolved.Sort(static (a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
        return resolved.ToArray();
    }

    /// <summary>
    /// Returns every OData-enabled publication (regardless of visibility) so callers can
    /// emit "auth required" / "forbidden" responses when no resources are visible to the
    /// caller but some exist.
    /// </summary>
    public static async Task<ResolvedODataPublication[]> ResolveAllODataPublicationsAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        var resolved = new List<ResolvedODataPublication>();
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMapV2(snapshot, ODataProtocolConstants.ProtocolName);

        foreach (var pub in snapshot.Graph.Publications)
        {
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
            if (resource is null)
            {
                continue;
            }

            var storageLayerId = ResolveStorageLayerId(snapshot, pub, resource);
            if (!storageLayerId.HasValue)
            {
                continue;
            }

            resolved.Add(new ResolvedODataPublication(pub, resource, service, pub.LayerIndex.Value, storageLayerId.Value));
        }

        resolved.Sort(static (a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
        return resolved.ToArray();
    }

    /// <summary>
    /// Resolves the integer storage-layer handle used by the feature-store boundary for
    /// a validated OData publication/resource pair.
    /// </summary>
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

    private static int? ResolveStorageLayerId(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication? publication,
        MetadataV2Resource resource)
    {
        if (publication is not null)
        {
            var publicationStorageLayerId = snapshot.ResolveStorageLayerId(publication);
            if (publicationStorageLayerId.HasValue)
            {
                return publicationStorageLayerId;
            }
        }

        return snapshot.ResolveStorageLayerId(resource);
    }

    private static bool IsPrimaryPublicationForLayer(
        MetadataV2Publication publication,
        MetadataV2Service service,
        IReadOnlyDictionary<int, MetadataV2Service> primaryServices)
    {
        return publication.LayerIndex.HasValue &&
            primaryServices.TryGetValue(publication.LayerIndex.Value, out var primaryService) &&
            string.Equals(primaryService.Metadata.Id, service.Metadata.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<MetadataV2GraphSnapshot> GetSnapshotAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        return await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }
}

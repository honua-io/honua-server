// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Consumer-facing helpers over a <see cref="MetadataV2GraphSnapshot"/>.
/// These collapse common multi-hop lookups (service → publication → resource → binding)
/// into single calls so consumers do not reach into the index directly.
/// </summary>
public static class MetadataV2GraphSnapshotExtensions
{
    /// <summary>
    /// Finds a service by case-insensitive name.
    /// </summary>
    public static MetadataV2Service? FindService(this MetadataV2GraphSnapshot snapshot, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrEmpty(serviceName))
        {
            return null;
        }
        return snapshot.Index.ServicesByName.TryGetValue(serviceName, out var service) ? service : null;
    }

    /// <summary>
    /// Returns all publications on a service.
    /// </summary>
    public static IReadOnlyList<MetadataV2Publication> PublicationsForService(
        this MetadataV2GraphSnapshot snapshot, string serviceId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Index.PublicationsByService[serviceId].ToArray();
    }

    /// <summary>
    /// Resolves the canonical resource backing a publication.
    /// </summary>
    public static MetadataV2Resource? ResolveResource(
        this MetadataV2GraphSnapshot snapshot, MetadataV2Publication publication)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(publication);
        return snapshot.Index.ResourcesById.TryGetValue(publication.ResourceId, out var r) ? r : null;
    }

    /// <summary>
    /// Resolves the storage binding to use for this publication. Prefers the publication's
    /// explicit binding, falls back to the resource's primary binding, then to any binding.
    /// </summary>
    public static MetadataV2StorageBinding? ResolveStorageBinding(
        this MetadataV2GraphSnapshot snapshot, MetadataV2Publication publication)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(publication);

        if (!string.IsNullOrEmpty(publication.StorageBindingId)
            && snapshot.Index.StorageBindingsById.TryGetValue(publication.StorageBindingId, out var explicitBinding))
        {
            return explicitBinding;
        }

        if (!snapshot.Index.ResourcesById.TryGetValue(publication.ResourceId, out var resource))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(resource.PrimaryStorageBindingId)
            && snapshot.Index.StorageBindingsById.TryGetValue(resource.PrimaryStorageBindingId, out var primary))
        {
            return primary;
        }

        return snapshot.Index.StorageBindingsByResource[resource.Metadata.Id].FirstOrDefault();
    }

    /// <summary>
    /// Returns the connection backing a storage binding, if any.
    /// </summary>
    public static MetadataV2Connection? ResolveConnection(
        this MetadataV2GraphSnapshot snapshot, MetadataV2StorageBinding binding)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrEmpty(binding.ConnectionId))
        {
            return null;
        }
        return snapshot.Index.ConnectionsById.TryGetValue(binding.ConnectionId, out var c) ? c : null;
    }

    /// <summary>
    /// Returns publications on the given service whose route segment or service-local id matches the provided key.
    /// </summary>
    public static MetadataV2Publication? FindPublicationOnService(
        this MetadataV2GraphSnapshot snapshot,
        string serviceId,
        string serviceLocalIdOrPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrEmpty(serviceId) || string.IsNullOrEmpty(serviceLocalIdOrPath))
        {
            return null;
        }
        foreach (var pub in snapshot.Index.PublicationsByService[serviceId])
        {
            if (string.Equals(pub.ServiceLocalId, serviceLocalIdOrPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pub.Path, serviceLocalIdOrPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pub.Metadata.Name, serviceLocalIdOrPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pub.Metadata.Id, serviceLocalIdOrPath, StringComparison.OrdinalIgnoreCase))
            {
                return pub;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds a publication on a service by its service-local layer index.
    /// </summary>
    public static MetadataV2Publication? FindPublicationByLayerIndex(
        this MetadataV2GraphSnapshot snapshot,
        string serviceId,
        int layerIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var pub in snapshot.Index.PublicationsByService[serviceId])
        {
            if (pub.LayerIndex == layerIndex)
            {
                return pub;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns fields on a resource that declare any of the given semantic roles.
    /// </summary>
    public static IEnumerable<MetadataV2Field> FieldsWithSemanticRole(
        this MetadataV2Resource resource, string semanticRole)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrEmpty(semanticRole))
        {
            yield break;
        }
        foreach (var field in resource.SchemaFields)
        {
            for (int i = 0; i < field.SemanticRoles.Count; i++)
            {
                if (string.Equals(field.SemanticRoles[i], semanticRole, StringComparison.OrdinalIgnoreCase))
                {
                    yield return field;
                    break;
                }
            }
        }
    }
}

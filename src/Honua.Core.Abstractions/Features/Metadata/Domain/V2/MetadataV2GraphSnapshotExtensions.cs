// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2GraphSnapshotExtensions
{
    public static bool IsRoutable(
        this MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service) ||
            !service.IsRoutable())
        {
            return false;
        }

        var resource = snapshot.ResolveResource(publication);
        if (!publication.IsRoutable(resource))
        {
            return false;
        }

        var binding = snapshot.ResolveStorageBinding(publication);
        return binding is null || snapshot.IsRoutable(binding, resource);
    }

    public static bool IsRoutable(
        this MetadataV2GraphSnapshot snapshot,
        MetadataV2StorageBinding binding,
        MetadataV2Resource? resource)
        => binding.IsRoutable(resource) && IsUnderstoodConnection(snapshot.ResolveConnection(binding));

    public static bool IsRoutable(this MetadataV2Service service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return IsKnownEnumValue(service.ServiceType, MetadataV2ServiceType.Unknown) &&
            IsServingStatus(service.Status);
    }

    // Retired entries stay addressable for administration and must not be served.
    public static bool IsRoutable(
        this MetadataV2Publication publication,
        MetadataV2Resource? resource)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return IsKnownEnumValue(publication.PublicationType, MetadataV2PublicationType.Unknown) &&
               IsServingStatus(publication.Status) && IsServingResource(resource);
    }

    private static bool IsRoutable(
        this MetadataV2StorageBinding binding,
        MetadataV2Resource? resource)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return IsKnownEnumValue(binding.StorageType, MetadataV2StorageType.Unknown) &&
               IsServingStatus(binding.Status) && IsServingResource(resource);
    }

    private static bool IsServingResource(MetadataV2Resource? resource)
        => resource is not null &&
           IsKnownEnumValue(resource.Type, MetadataV2ResourceType.Unknown) && IsServingStatus(resource.Status);

    private static bool IsUnderstoodConnection(MetadataV2Connection? connection)
        => connection is null ||
           IsKnownEnumValue(connection.Type, MetadataV2ConnectionType.Unknown) &&
           (connection.Status is null ||
            IsKnownEnumValue(connection.Status.Lifecycle, MetadataV2LifecycleStatus.Unknown));

    private static bool IsKnownEnumValue<TEnum>(TEnum value, TEnum unknownValue)
        where TEnum : struct, Enum
        => Enum.IsDefined(value) && !EqualityComparer<TEnum>.Default.Equals(value, unknownValue);

    // A missing status object is an omitted caller value, not an explicit draft.
    // JSON deserialization can leave the non-nullable property null when the
    // member is absent; that entity stays routable. Explicit draft does not.
    private static bool IsServingStatus(MetadataV2Status? status)
        => status is null || status.Lifecycle is MetadataV2LifecycleStatus.Active or MetadataV2LifecycleStatus.Deprecated;

    public static MetadataV2Service? FindService(this MetadataV2GraphSnapshot snapshot, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrEmpty(serviceName))
        {
            return null;
        }
        return snapshot.Index.ServicesByName.TryGetValue(serviceName, out var service) ? service : null;
    }

    public static IReadOnlyList<MetadataV2Publication> PublicationsForService(
        this MetadataV2GraphSnapshot snapshot, string serviceId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Index.PublicationsByService[serviceId].ToArray();
    }

    public static MetadataV2Resource? ResolveResource(
        this MetadataV2GraphSnapshot snapshot, MetadataV2Publication publication)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(publication);
        return snapshot.Index.ResourcesById.TryGetValue(publication.ResourceId, out var r) ? r : null;
    }

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

    public static int? ResolveStorageLayerId(
        this MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication)
    {
        var binding = snapshot.ResolveStorageBinding(publication);
        return binding?.StorageLayerId;
    }

    // The storage handle is resolved from the binding before the service-local index, because those numbers name different resources when they diverge.
    public static int? ResolveStorageLayerId(
        this MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        MetadataV2Resource? resource)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(publication);

        var fromBinding = snapshot.ResolveStorageLayerId(publication);
        if (fromBinding.HasValue)
        {
            return fromBinding;
        }

        var canonical = resource ?? snapshot.ResolveResource(publication);
        var fromResource = canonical is null ? null : snapshot.ResolveStorageLayerId(canonical);
        if (fromResource.HasValue)
        {
            return fromResource;
        }

        // Last resort for graphs that carry no binding handle. The service-local index is
        // only usable as a storage handle when no other resource's binding claims that
        // number; otherwise it names a different resource's storage and the publication
        // resolves to no handle (SEC-4).
        var layerIndex = publication.LayerIndex;
        if (layerIndex is int index
            && snapshot.Index.StorageBindingsByStorageLayerId.TryGetValue(index, out var claimant)
            && !string.Equals(claimant.ResourceId, canonical?.Metadata.Id, StringComparison.Ordinal))
        {
            return null;
        }

        return layerIndex;
    }

    public static int? ResolveStorageLayerId(
        this MetadataV2GraphSnapshot snapshot,
        MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resource);
        if (!string.IsNullOrEmpty(resource.PrimaryStorageBindingId)
            && snapshot.Index.StorageBindingsById.TryGetValue(resource.PrimaryStorageBindingId, out var primary))
        {
            return primary.StorageLayerId;
        }
        var any = snapshot.Index.StorageBindingsByResource[resource.Metadata.Id].FirstOrDefault();
        return any?.StorageLayerId;
    }

    public static IEnumerable<MetadataV2Publication> PublicationsForStorageLayer(
        this MetadataV2GraphSnapshot snapshot,
        int storageLayerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Graph.Publications.Where(publication =>
            snapshot.IsRoutable(publication)
            && snapshot.ResolveStorageLayerId(publication) == storageLayerId);
    }

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
        foreach (var pub in snapshot.Index.PublicationsByService[serviceId].Where(pub =>
            snapshot.IsRoutable(pub) &&
            (string.Equals(pub.ServiceLocalId, serviceLocalIdOrPath, StringComparison.OrdinalIgnoreCase)
             || string.Equals(pub.Path, serviceLocalIdOrPath, StringComparison.OrdinalIgnoreCase)
             || string.Equals(pub.Metadata.Name, serviceLocalIdOrPath, StringComparison.OrdinalIgnoreCase)
             || string.Equals(pub.Metadata.Id, serviceLocalIdOrPath, StringComparison.OrdinalIgnoreCase))))
        {
            return pub;
        }
        return null;
    }

    public static MetadataV2Publication? FindPublicationByLayerIndex(
        this MetadataV2GraphSnapshot snapshot,
        string serviceId,
        int layerIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var pub in snapshot.Index.PublicationsByService[serviceId]
                     .Where(pub => pub.LayerIndex == layerIndex && snapshot.IsRoutable(pub)))
        {
            return pub;
        }
        return null;
    }

    public static int? ResolveStorageSrid(this MetadataV2GraphSnapshot snapshot, int storageLayerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(storageLayerId, out var resource))
        {
            return null;
        }

        var binding = snapshot.Graph.StorageBindings.SingleOrDefault(b =>
            b.StorageLayerId == storageLayerId && b.ResourceId == resource.Metadata.Id);
        if (binding is null || binding.StorageType != MetadataV2StorageType.RelationalTable)
        {
            return null;
        }

        return FeatureStorageMapping.FromMetadata(resource, binding).StorageSrid;
    }

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

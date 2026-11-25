// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed partial class MetadataV2GraphIndex
{
    private static RoutablePublicationIndexes BuildRoutablePublicationIndexes(
        IReadOnlyList<MetadataV2Layer> publications,
        RoutabilityLookupContext context)
    {
        var builders = new RoutablePublicationBuilders();
        foreach (var publication in publications)
        {
            var result = EvaluatePublicationRoutability(publication, context);
            if (result.IsRoutable)
            {
                builders.Add(publication, result.Binding, context.PublicationsById);
            }
        }

        return builders.Freeze();
    }

    private static StorageSridIndexes BuildStorageSridIndexes(
        IReadOnlyList<MetadataV2StorageBinding> bindings,
        Dictionary<int, MetadataV2Object> resourcesByStorageLayerId)
    {
        var (matchedBindings, invalidOrAmbiguous) = CollectUniqueStorageBindings(bindings, resourcesByStorageLayerId);
        var storageSridByLayerId = new Dictionary<int, int?>();

        foreach (var (storageLayerId, binding) in matchedBindings)
        {
            if (binding.StorageType != MetadataV2StorageType.RelationalTable)
            {
                continue;
            }

            var sridResolution = ResolveRelationalStorageSrid(binding, resourcesByStorageLayerId[storageLayerId]);
            if (sridResolution.IsValid)
            {
                storageSridByLayerId[storageLayerId] = sridResolution.Srid;
            }
            else
            {
                invalidOrAmbiguous.Add(storageLayerId);
            }
        }

        return new StorageSridIndexes
        {
            ByStorageLayerId = storageSridByLayerId.ToFrozenDictionary(),
            InvalidOrAmbiguousLayerIds = invalidOrAmbiguous.ToFrozenSet(),
        };
    }

    private static (Dictionary<int, MetadataV2StorageBinding> Matched, HashSet<int> Ambiguous) CollectUniqueStorageBindings(
        IReadOnlyList<MetadataV2StorageBinding> bindings,
        Dictionary<int, MetadataV2Object> resourcesByStorageLayerId)
    {
        var matched = new Dictionary<int, MetadataV2StorageBinding>();
        var ambiguous = new HashSet<int>();

        foreach (var binding in bindings)
        {
            if (binding.StorageLayerId is not int storageLayerId ||
                !resourcesByStorageLayerId.TryGetValue(storageLayerId, out var resource) ||
                !string.Equals(binding.ResourceId, resource.Metadata.Id, StringComparison.Ordinal) ||
                ambiguous.Contains(storageLayerId))
            {
                continue;
            }

            if (!matched.TryAdd(storageLayerId, binding))
            {
                matched.Remove(storageLayerId);
                ambiguous.Add(storageLayerId);
            }
        }

        return (matched, ambiguous);
    }

    private static StorageSridResolution ResolveRelationalStorageSrid(
        MetadataV2StorageBinding binding,
        MetadataV2Object resource)
    {
        var (_, tableName) = binding.ResolveRelationalTable();
        var primaryKeyColumn = binding.ReadStringOption("primaryKeyColumn")
            ?? resource.FindPrimaryIdField()?.Name
            ?? FieldNames.ObjectId;
        var srid = binding.ReadIntOption("storageSrid")
            ?? binding.ReadIntOption("srid")
            ?? resource.Spatial?.StorageCrs?.ResolveSrid()
            ?? resource.ReadSrid();

        var isValid = !string.IsNullOrWhiteSpace(tableName) &&
            !string.IsNullOrWhiteSpace(primaryKeyColumn) &&
            srid is not <= 0;
        return new StorageSridResolution { IsValid = isValid, Srid = srid };
    }

    private static RoutabilityResult EvaluatePublicationRoutability(
        MetadataV2Layer publication,
        RoutabilityLookupContext context)
    {
        if (!context.ServicesById.TryGetValue(publication.ServiceId, out var service) || !service.IsRoutable())
        {
            return RoutabilityResult.NotRoutable;
        }

        context.ResourcesById.TryGetValue(publication.ResourceId, out var resource);
        if (!publication.IsRoutable(resource))
        {
            return RoutabilityResult.NotRoutable;
        }

        var binding = ResolveBinding(publication, resource, context);
        if (binding is null)
        {
            return RoutabilityResult.Routable(null);
        }

        MetadataV2Connection? connection = null;
        if (!string.IsNullOrEmpty(binding.ConnectionId))
        {
            context.ConnectionsById.TryGetValue(binding.ConnectionId, out connection);
        }

        return MetadataV2GraphSnapshotExtensions.IsRoutableBinding(binding, resource, connection)
            ? RoutabilityResult.Routable(binding)
            : RoutabilityResult.NotRoutable;
    }

    private static MetadataV2StorageBinding? ResolveBinding(
        MetadataV2Layer publication,
        MetadataV2Object? resource,
        RoutabilityLookupContext context)
    {
        if (!string.IsNullOrEmpty(publication.StorageBindingId) &&
            context.StorageBindingsById.TryGetValue(publication.StorageBindingId, out var explicitBinding))
        {
            return explicitBinding;
        }

        if (resource is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(resource.PrimaryStorageBindingId) &&
            context.StorageBindingsById.TryGetValue(resource.PrimaryStorageBindingId, out var primary))
        {
            return primary;
        }

        return context.StorageBindingsByResourceMap.TryGetValue(resource.Metadata.Id, out var bindings) &&
            !bindings.IsEmpty
            ? bindings[0]
            : null;
    }
}

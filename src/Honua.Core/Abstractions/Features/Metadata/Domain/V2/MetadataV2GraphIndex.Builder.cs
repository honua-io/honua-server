// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed partial class MetadataV2GraphIndex
{
    public static MetadataV2GraphIndex Build(MetadataV2Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var primary = BuildPrimaryIndexes(graph);
        var routabilityContext = new RoutabilityLookupContext
        {
            ServicesById = primary.Services.ById,
            ResourcesById = primary.ResourcesById,
            StorageBindingsById = primary.Storage.ById,
            StorageBindingsByResourceMap = primary.Storage.ByResourceMap,
            ConnectionsById = primary.ConnectionsById,
            PublicationsById = primary.Publications.ById,
        };
        var routable = BuildRoutablePublicationIndexes(graph.Publications, routabilityContext);
        var storageSrids = BuildStorageSridIndexes(graph.StorageBindings, primary.Storage.ResourcesByStorageLayerId);

        return new MetadataV2GraphIndex(BuildGraphIndexState(primary, routable, storageSrids));
    }

    private static PrimaryGraphIndexes BuildPrimaryIndexes(MetadataV2Graph graph)
    {
        // First-wins on duplicate Metadata.Id values keeps index construction deterministic on invalid graphs.
        var resourcesById = IndexById(graph.Resources, static r => r.Metadata.Id);
        var services = BuildServiceIndexes(graph.Services);
        return new PrimaryGraphIndexes
        {
            CatalogsById = IndexById(graph.Catalogs, static c => c.Metadata.Id),
            ResourcesById = resourcesById,
            ResourcesByName = BuildResourcesByName(graph.Resources),
            ConnectionsById = IndexById(graph.Connections, static c => c.Metadata.Id),
            Storage = BuildStorageBindingIndexes(graph.StorageBindings, resourcesById),
            Services = services,
            Publications = BuildPublicationIndexes(graph.Publications, services.ById),
            Profiles = BuildProjectionProfileIndexes(graph.ProjectionProfiles),
            PoliciesById = IndexById(graph.Policies, static p => p.Metadata.Id),
            RolesById = IndexById(graph.Roles, static r => r.Metadata.Id),
            ResourcesByStyleMap = BuildResourcesByStyleMap(graph.Resources),
        };
    }

    private static GraphIndexState BuildGraphIndexState(
        PrimaryGraphIndexes primary,
        RoutablePublicationIndexes routable,
        StorageSridIndexes storageSrids)
        => new()
        {
            CatalogsById = primary.CatalogsById.ToFrozenDictionary(StringComparer.Ordinal),
            ResourcesById = primary.ResourcesById.ToFrozenDictionary(StringComparer.Ordinal),
            ResourcesByName = primary.ResourcesByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            ConnectionsById = primary.ConnectionsById.ToFrozenDictionary(StringComparer.Ordinal),
            StorageBindingsById = primary.Storage.ById.ToFrozenDictionary(StringComparer.Ordinal),
            StorageBindingsByResourceMap = primary.Storage.ByResourceMap,
            StorageBindingsByStorageLayerId = primary.Storage.ByStorageLayerId.ToFrozenDictionary(),
            ResourcesByStorageLayerId = primary.Storage.ResourcesByStorageLayerId.ToFrozenDictionary(),
            ServicesById = primary.Services.ById.ToFrozenDictionary(StringComparer.Ordinal),
            ServicesByName = primary.Services.ByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            PublicationsById = primary.Publications.ById.ToFrozenDictionary(StringComparer.Ordinal),
            PublicationsByServiceMap = primary.Publications.ByServiceMap,
            PublicationsByResourceMap = primary.Publications.ByResourceMap,
            ServiceNamesByResource = primary.Publications.ServiceNamesByResource,
            ProjectionProfilesById = primary.Profiles.ById.ToFrozenDictionary(StringComparer.Ordinal),
            ProjectionProfilesByTargetMap = primary.Profiles.ByTargetMap,
            PoliciesById = primary.PoliciesById.ToFrozenDictionary(StringComparer.Ordinal),
            RolesById = primary.RolesById.ToFrozenDictionary(StringComparer.Ordinal),
            ResourcesByStyleResourceIdMap = primary.ResourcesByStyleMap,
            RoutablePublicationIds = routable.Ids,
            RoutablePublicationsByStorageLayerId = routable.ByStorageLayerId,
            RoutablePublicationsByLayerIndex = routable.ByLayerIndex,
            RoutablePublicationsByServiceAndLayerIndex = routable.ByServiceAndLayerIndex,
            RoutablePublicationsByServiceAndIdentifier = routable.ByServiceAndIdentifier,
            StorageSridByStorageLayerId = storageSrids.ByStorageLayerId,
            InvalidOrAmbiguousStorageSridLayerIds = storageSrids.InvalidOrAmbiguousLayerIds,
        };

    private static Dictionary<string, T> IndexById<T>(IReadOnlyList<T> items, Func<T, string> getId)
    {
        var map = new Dictionary<string, T>(items.Count, StringComparer.Ordinal);
        foreach (var item in items)
        {
            map.TryAdd(getId(item), item);
        }

        return map;
    }

    private static Dictionary<string, MetadataV2Object> BuildResourcesByName(IReadOnlyList<MetadataV2Object> resources)
    {
        var map = new Dictionary<string, MetadataV2Object>(resources.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in resources)
        {
            if (!string.IsNullOrEmpty(r.Metadata.Name))
            {
                map.TryAdd(r.Metadata.Name, r);
            }
        }

        return map;
    }

    private static StorageBindingIndexes BuildStorageBindingIndexes(
        IReadOnlyList<MetadataV2StorageBinding> bindings,
        Dictionary<string, MetadataV2Object> resourcesById)
    {
        var byId = new Dictionary<string, MetadataV2StorageBinding>(bindings.Count, StringComparer.Ordinal);
        var byResourceBuilders = new Dictionary<string, ImmutableArray<MetadataV2StorageBinding>.Builder>(StringComparer.Ordinal);
        var byStorageLayerId = new Dictionary<int, MetadataV2StorageBinding>();
        var resourcesByStorageLayerId = new Dictionary<int, MetadataV2Object>();

        foreach (var binding in bindings)
        {
            byId.TryAdd(binding.Metadata.Id, binding);
            AddToGroupedMap(byResourceBuilders, binding.ResourceId, binding);

            if (binding.StorageLayerId.HasValue)
            {
                var storageLayerId = binding.StorageLayerId.GetValueOrDefault();
                byStorageLayerId.TryAdd(storageLayerId, binding);
                if (resourcesById.TryGetValue(binding.ResourceId, out var resource))
                {
                    resourcesByStorageLayerId.TryAdd(storageLayerId, resource);
                }
            }
        }

        return new StorageBindingIndexes
        {
            ById = byId,
            ByResourceMap = FreezeGroupedMap(byResourceBuilders, StringComparer.Ordinal),
            ByStorageLayerId = byStorageLayerId,
            ResourcesByStorageLayerId = resourcesByStorageLayerId,
        };
    }

    private static ServiceIndexes BuildServiceIndexes(IReadOnlyList<MetadataV2Service> services)
    {
        var byId = new Dictionary<string, MetadataV2Service>(services.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, MetadataV2Service>(services.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var s in services)
        {
            byId.TryAdd(s.Metadata.Id, s);
            if (!string.IsNullOrEmpty(s.Metadata.Name))
            {
                byName.TryAdd(s.Metadata.Name, s);
            }
        }

        return new ServiceIndexes { ById = byId, ByName = byName };
    }

    private static PublicationIndexes BuildPublicationIndexes(
        IReadOnlyList<MetadataV2Layer> publications,
        Dictionary<string, MetadataV2Service> servicesById)
    {
        var byId = new Dictionary<string, MetadataV2Layer>(publications.Count, StringComparer.Ordinal);
        var byServiceBuilders = new Dictionary<string, ImmutableArray<MetadataV2Layer>.Builder>(StringComparer.Ordinal);
        var byResourceBuilders = new Dictionary<string, ImmutableArray<MetadataV2Layer>.Builder>(StringComparer.Ordinal);

        foreach (var p in publications)
        {
            byId.TryAdd(p.Metadata.Id, p);
            AddToGroupedMap(byServiceBuilders, p.ServiceId, p);
            AddToGroupedMap(byResourceBuilders, p.ResourceId, p);
        }

        return new PublicationIndexes
        {
            ById = byId,
            ByServiceMap = FreezeGroupedMap(byServiceBuilders, StringComparer.Ordinal),
            ByResourceMap = FreezeGroupedMap(byResourceBuilders, StringComparer.Ordinal),
            ServiceNamesByResource = BuildServiceNamesByResource(byId, servicesById),
        };
    }

    private static FrozenDictionary<string, ImmutableArray<string>> BuildServiceNamesByResource(
        Dictionary<string, MetadataV2Layer> publicationsById,
        Dictionary<string, MetadataV2Service> servicesById)
    {
        var serviceNameSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var publication in publicationsById.Values)
        {
            if (!servicesById.TryGetValue(publication.ServiceId, out var service) ||
                string.IsNullOrWhiteSpace(service.Metadata.Name))
            {
                continue;
            }

            if (!serviceNameSets.TryGetValue(publication.ResourceId, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                serviceNameSets.Add(publication.ResourceId, names);
            }

            names.Add(service.Metadata.Name);
        }

        return serviceNameSets.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableArray(),
            StringComparer.Ordinal);
    }

    private static ProjectionProfileIndexes BuildProjectionProfileIndexes(
        IReadOnlyList<MetadataV2ProjectionProfile> profiles)
    {
        var byId = new Dictionary<string, MetadataV2ProjectionProfile>(profiles.Count, StringComparer.Ordinal);
        var byTargetBuilders = new Dictionary<string, ImmutableArray<MetadataV2ProjectionProfile>.Builder>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in profiles)
        {
            byId.TryAdd(p.Metadata.Id, p);
            AddToGroupedMap(byTargetBuilders, p.Target, p);
        }

        return new ProjectionProfileIndexes
        {
            ById = byId,
            ByTargetMap = FreezeGroupedMap(byTargetBuilders, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static FrozenDictionary<string, ImmutableArray<MetadataV2Object>> BuildResourcesByStyleMap(
        IReadOnlyList<MetadataV2Object> resources)
    {
        var builders = new Dictionary<string, ImmutableArray<MetadataV2Object>.Builder>(StringComparer.Ordinal);
        foreach (var r in resources)
        {
            var styleIds = r.StyleResourceIds;
            for (var i = 0; i < styleIds.Count; i++)
            {
                var styleId = styleIds[i];
                if (!string.IsNullOrEmpty(styleId))
                {
                    AddToGroupedMap(builders, styleId, r);
                }
            }
        }

        return FreezeGroupedMap(builders, StringComparer.Ordinal);
    }

    private static void AddToGroupedMap<TKey, TValue>(
        Dictionary<TKey, ImmutableArray<TValue>.Builder> map,
        TKey key,
        TValue value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var builder))
        {
            builder = ImmutableArray.CreateBuilder<TValue>();
            map.Add(key, builder);
        }

        builder.Add(value);
    }

    private static FrozenDictionary<TKey, ImmutableArray<TValue>> FreezeGroupedMap<TKey, TValue>(
        Dictionary<TKey, ImmutableArray<TValue>.Builder> builders,
        IEqualityComparer<TKey> comparer)
        where TKey : notnull
        => builders.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToImmutable(), comparer);

    private static void TryAddIdentifier(
        Dictionary<string, MetadataV2Layer> map,
        string? identifier,
        MetadataV2Layer publication)
    {
        if (!string.IsNullOrEmpty(identifier))
        {
            map.TryAdd(identifier, publication);
        }
    }
}

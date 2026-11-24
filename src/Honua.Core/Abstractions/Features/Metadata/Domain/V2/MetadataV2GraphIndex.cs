// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed class MetadataV2GraphIndex
{
    private MetadataV2GraphIndex(
        FrozenDictionary<string, MetadataV2Catalog> catalogsById,
        FrozenDictionary<string, MetadataV2Object> resourcesById,
        FrozenDictionary<string, MetadataV2Object> resourcesByName,
        FrozenDictionary<string, MetadataV2Connection> connectionsById,
        FrozenDictionary<string, MetadataV2StorageBinding> storageBindingsById,
        FrozenDictionary<string, ImmutableArray<MetadataV2StorageBinding>> storageBindingsByResourceMap,
        FrozenDictionary<int, MetadataV2StorageBinding> storageBindingsByStorageLayerId,
        FrozenDictionary<int, MetadataV2Object> resourcesByStorageLayerId,
        FrozenDictionary<string, MetadataV2Service> servicesById,
        FrozenDictionary<string, MetadataV2Service> servicesByName,
        FrozenDictionary<string, MetadataV2Layer> publicationsById,
        FrozenDictionary<string, ImmutableArray<MetadataV2Layer>> publicationsByServiceMap,
        FrozenDictionary<string, ImmutableArray<MetadataV2Layer>> publicationsByResourceMap,
        FrozenDictionary<string, ImmutableArray<string>> serviceNamesByResource,
        FrozenDictionary<string, MetadataV2ProjectionProfile> projectionProfilesById,
        FrozenDictionary<string, ImmutableArray<MetadataV2ProjectionProfile>> projectionProfilesByTargetMap,
        FrozenDictionary<string, MetadataV2Policy> policiesById,
        FrozenDictionary<string, MetadataV2Role> rolesById,
        FrozenDictionary<string, ImmutableArray<MetadataV2Object>> resourcesByStyleResourceIdMap,
        FrozenSet<string> routablePublicationIds,
        FrozenDictionary<int, ImmutableArray<MetadataV2Layer>> routablePublicationsByStorageLayerId,
        FrozenDictionary<int, ImmutableArray<MetadataV2Layer>> routablePublicationsByLayerIndex,
        FrozenDictionary<(string ServiceId, int LayerIndex), MetadataV2Layer> routablePublicationsByServiceAndLayerIndex,
        FrozenDictionary<string, FrozenDictionary<string, MetadataV2Layer>> routablePublicationsByServiceAndIdentifier,
        FrozenDictionary<int, int?> storageSridByStorageLayerId,
        FrozenSet<int> invalidOrAmbiguousStorageSridLayerIds)
    {
        CatalogsById = catalogsById;
        ResourcesById = resourcesById;
        ResourcesByName = resourcesByName;
        ConnectionsById = connectionsById;
        StorageBindingsById = storageBindingsById;
        StorageBindingsByResourceMap = storageBindingsByResourceMap;
        StorageBindingsByResource = new FrozenLookup<string, MetadataV2StorageBinding>(storageBindingsByResourceMap);
        StorageBindingsByStorageLayerId = storageBindingsByStorageLayerId;
        ResourcesByStorageLayerId = resourcesByStorageLayerId;
        ServicesById = servicesById;
        ServicesByName = servicesByName;
        PublicationsById = publicationsById;
        PublicationsByServiceMap = publicationsByServiceMap;
        PublicationsByService = new FrozenLookup<string, MetadataV2Layer>(publicationsByServiceMap);
        PublicationsByResource = new FrozenLookup<string, MetadataV2Layer>(publicationsByResourceMap);
        ServiceNamesByResource = serviceNamesByResource;
        ProjectionProfilesById = projectionProfilesById;
        ProjectionProfilesByTarget = new FrozenLookup<string, MetadataV2ProjectionProfile>(projectionProfilesByTargetMap);
        PoliciesById = policiesById;
        RolesById = rolesById;
        ResourcesByStyleResourceId = new FrozenLookup<string, MetadataV2Object>(resourcesByStyleResourceIdMap);
        RoutablePublicationIds = routablePublicationIds;
        RoutablePublicationsByStorageLayerId = routablePublicationsByStorageLayerId;
        RoutablePublicationsByLayerIndex = routablePublicationsByLayerIndex;
        RoutablePublicationsByServiceAndLayerIndex = routablePublicationsByServiceAndLayerIndex;
        RoutablePublicationsByServiceAndIdentifier = routablePublicationsByServiceAndIdentifier;
        StorageSridByStorageLayerId = storageSridByStorageLayerId;
        InvalidOrAmbiguousStorageSridLayerIds = invalidOrAmbiguousStorageSridLayerIds;
    }

    public IReadOnlyDictionary<string, MetadataV2Catalog> CatalogsById { get; }

    public IReadOnlyDictionary<string, MetadataV2Object> ResourcesById { get; }

    public IReadOnlyDictionary<string, MetadataV2Object> ResourcesByName { get; }

    public IReadOnlyDictionary<string, MetadataV2Connection> ConnectionsById { get; }

    public IReadOnlyDictionary<string, MetadataV2StorageBinding> StorageBindingsById { get; }

    public ILookup<string, MetadataV2StorageBinding> StorageBindingsByResource { get; }

    public IReadOnlyDictionary<int, MetadataV2StorageBinding> StorageBindingsByStorageLayerId { get; }

    public IReadOnlyDictionary<int, MetadataV2Object> ResourcesByStorageLayerId { get; }

    public IReadOnlyDictionary<string, MetadataV2Service> ServicesById { get; }

    public IReadOnlyDictionary<string, MetadataV2Service> ServicesByName { get; }

    public IReadOnlyDictionary<string, MetadataV2Layer> PublicationsById { get; }

    public ILookup<string, MetadataV2Layer> PublicationsByService { get; }

    public ILookup<string, MetadataV2Layer> PublicationsByResource { get; }

    public IReadOnlyDictionary<string, ImmutableArray<string>> ServiceNamesByResource { get; }

    public IReadOnlyDictionary<string, MetadataV2ProjectionProfile> ProjectionProfilesById { get; }

    public ILookup<string, MetadataV2ProjectionProfile> ProjectionProfilesByTarget { get; }

    public IReadOnlyDictionary<string, MetadataV2Policy> PoliciesById { get; }

    public IReadOnlyDictionary<string, MetadataV2Role> RolesById { get; }

    public ILookup<string, MetadataV2Object> ResourcesByStyleResourceId { get; }

    public FrozenSet<string> RoutablePublicationIds { get; }

    public FrozenDictionary<int, ImmutableArray<MetadataV2Layer>> RoutablePublicationsByStorageLayerId { get; }

    public FrozenDictionary<int, ImmutableArray<MetadataV2Layer>> RoutablePublicationsByLayerIndex { get; }

    public FrozenDictionary<(string ServiceId, int LayerIndex), MetadataV2Layer> RoutablePublicationsByServiceAndLayerIndex { get; }

    internal FrozenDictionary<string, ImmutableArray<MetadataV2StorageBinding>> StorageBindingsByResourceMap { get; }

    internal FrozenDictionary<string, ImmutableArray<MetadataV2Layer>> PublicationsByServiceMap { get; }

    internal FrozenDictionary<string, FrozenDictionary<string, MetadataV2Layer>> RoutablePublicationsByServiceAndIdentifier { get; }

    internal FrozenDictionary<int, int?> StorageSridByStorageLayerId { get; }

    internal FrozenSet<int> InvalidOrAmbiguousStorageSridLayerIds { get; }

    public static MetadataV2GraphIndex Build(MetadataV2Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // First-wins on duplicate Metadata.Id values: graph validation surfaces duplicates as soft
        // errors rather than preventing persistence, so loading a graph with duplicate ids (e.g. a
        // hand-edited file fixture or a legacy snapshot) must not crash with an opaque
        // ArgumentException from ToDictionary. The TryAdd strategy here matches the documented
        // strategy on the name / storage-layer-id maps below and keeps the index deterministic
        // even on a bad graph.
        var catalogsById = new Dictionary<string, MetadataV2Catalog>(graph.Catalogs.Count, StringComparer.Ordinal);
        foreach (var c in graph.Catalogs) catalogsById.TryAdd(c.Metadata.Id, c);

        var resourcesById = new Dictionary<string, MetadataV2Object>(graph.Resources.Count, StringComparer.Ordinal);
        foreach (var r in graph.Resources) resourcesById.TryAdd(r.Metadata.Id, r);

        // Multiple resources may share the same display name (different IDs / namespaces).
        // First-wins keeps the index deterministic and avoids ArgumentException on construction.
        var resourcesByName = new Dictionary<string, MetadataV2Object>(graph.Resources.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in graph.Resources)
        {
            if (!string.IsNullOrEmpty(r.Metadata.Name))
            {
                resourcesByName.TryAdd(r.Metadata.Name, r);
            }
        }

        var connectionsById = new Dictionary<string, MetadataV2Connection>(graph.Connections.Count, StringComparer.Ordinal);
        foreach (var c in graph.Connections) connectionsById.TryAdd(c.Metadata.Id, c);

        var storageBindingsById = new Dictionary<string, MetadataV2StorageBinding>(graph.StorageBindings.Count, StringComparer.Ordinal);
        var storageBindingsByResourceBuilders = new Dictionary<string, ImmutableArray<MetadataV2StorageBinding>.Builder>(StringComparer.Ordinal);
        var storageBindingsByStorageLayerId = new Dictionary<int, MetadataV2StorageBinding>();
        var resourcesByStorageLayerId = new Dictionary<int, MetadataV2Object>();
        foreach (var binding in graph.StorageBindings)
        {
            storageBindingsById.TryAdd(binding.Metadata.Id, binding);
            AddToGroupedMap(storageBindingsByResourceBuilders, binding.ResourceId, binding);

            if (binding.StorageLayerId.HasValue)
            {
                var storageLayerId = binding.StorageLayerId.GetValueOrDefault();
                // First-wins on numeric storage-layer collisions keeps the index deterministic.
                storageBindingsByStorageLayerId.TryAdd(storageLayerId, binding);
                if (resourcesById.TryGetValue(binding.ResourceId, out var resource))
                {
                    resourcesByStorageLayerId.TryAdd(storageLayerId, resource);
                }
            }
        }

        var storageBindingsByResourceMap = FreezeGroupedMap(storageBindingsByResourceBuilders, StringComparer.Ordinal);

        var servicesById = new Dictionary<string, MetadataV2Service>(graph.Services.Count, StringComparer.Ordinal);
        var servicesByName = new Dictionary<string, MetadataV2Service>(graph.Services.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var s in graph.Services)
        {
            servicesById.TryAdd(s.Metadata.Id, s);
            // Multiple V2 services may share the same display name when the same logical
            // service is exposed through multiple protocols (e.g. an OGC API Features
            // service + an Esri Feature Service for the same dataset). First-wins keeps
            // the index deterministic; consumers that need ambiguity-aware routing use
            // BuildPrimaryServiceMapV2 instead.
            if (!string.IsNullOrEmpty(s.Metadata.Name))
            {
                servicesByName.TryAdd(s.Metadata.Name, s);
            }
        }

        var publicationsById = new Dictionary<string, MetadataV2Layer>(graph.Publications.Count, StringComparer.Ordinal);
        var publicationsByServiceBuilders = new Dictionary<string, ImmutableArray<MetadataV2Layer>.Builder>(StringComparer.Ordinal);
        var publicationsByResourceBuilders = new Dictionary<string, ImmutableArray<MetadataV2Layer>.Builder>(StringComparer.Ordinal);
        foreach (var p in graph.Publications)
        {
            publicationsById.TryAdd(p.Metadata.Id, p);
            AddToGroupedMap(publicationsByServiceBuilders, p.ServiceId, p);
            AddToGroupedMap(publicationsByResourceBuilders, p.ResourceId, p);
        }

        var publicationsByServiceMap = FreezeGroupedMap(publicationsByServiceBuilders, StringComparer.Ordinal);
        var publicationsByResourceMap = FreezeGroupedMap(publicationsByResourceBuilders, StringComparer.Ordinal);

        // Resolve immutable security source names once, retaining first-wins duplicate IDs and all scopes.
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

        var serviceNamesByResource = serviceNameSets.ToFrozenDictionary(
            pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal);

        var projectionProfilesById = new Dictionary<string, MetadataV2ProjectionProfile>(graph.ProjectionProfiles.Count, StringComparer.Ordinal);
        var projectionProfilesByTargetBuilders = new Dictionary<string, ImmutableArray<MetadataV2ProjectionProfile>.Builder>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in graph.ProjectionProfiles)
        {
            projectionProfilesById.TryAdd(p.Metadata.Id, p);
            AddToGroupedMap(projectionProfilesByTargetBuilders, p.Target, p);
        }

        var projectionProfilesByTargetMap = FreezeGroupedMap(projectionProfilesByTargetBuilders, StringComparer.OrdinalIgnoreCase);

        var policiesById = new Dictionary<string, MetadataV2Policy>(graph.Policies.Count, StringComparer.Ordinal);
        foreach (var p in graph.Policies) policiesById.TryAdd(p.Metadata.Id, p);

        var rolesById = new Dictionary<string, MetadataV2Role>(graph.Roles.Count, StringComparer.Ordinal);
        foreach (var r in graph.Roles) rolesById.TryAdd(r.Metadata.Id, r);

        // Flatten (resource, styleResourceId) pairs into a reverse lookup so
        // callers can answer "which resources use style X?" in O(1).
        var resourcesByStyleBuilders = new Dictionary<string, ImmutableArray<MetadataV2Object>.Builder>(StringComparer.Ordinal);
        foreach (var r in graph.Resources)
        {
            var styleIds = r.StyleResourceIds;
            if (styleIds.Count == 0)
            {
                continue;
            }

            for (var i = 0; i < styleIds.Count; i++)
            {
                var styleId = styleIds[i];
                if (!string.IsNullOrEmpty(styleId))
                {
                    AddToGroupedMap(resourcesByStyleBuilders, styleId, r);
                }
            }
        }

        var resourcesByStyleResourceIdMap = FreezeGroupedMap(resourcesByStyleBuilders, StringComparer.Ordinal);

        // Precompute routability and hot-path publication lookup tables once per snapshot.
        var routablePublicationIds = new HashSet<string>(StringComparer.Ordinal);
        var routableByStorageLayerBuilders = new Dictionary<int, ImmutableArray<MetadataV2Layer>.Builder>();
        var routableByLayerIndexBuilders = new Dictionary<int, ImmutableArray<MetadataV2Layer>.Builder>();
        var routableByServiceAndLayerIndex = new Dictionary<(string ServiceId, int LayerIndex), MetadataV2Layer>();
        var routableByServiceAndIdentifierBuilders = new Dictionary<string, Dictionary<string, MetadataV2Layer>>(StringComparer.Ordinal);

        foreach (var publication in graph.Publications)
        {
            if (!IsPublicationRoutable(
                    publication,
                    servicesById,
                    resourcesById,
                    storageBindingsById,
                    storageBindingsByResourceMap,
                    connectionsById,
                    out var binding))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(publication.Metadata.Id) &&
                publicationsById.TryGetValue(publication.Metadata.Id, out var canonical) &&
                ReferenceEquals(canonical, publication))
            {
                routablePublicationIds.Add(publication.Metadata.Id);
            }

            if (binding?.StorageLayerId is int storageLayerId)
            {
                AddToGroupedMap(routableByStorageLayerBuilders, storageLayerId, publication);
            }

            if (publication.LayerIndex is int layerIndex)
            {
                AddToGroupedMap(routableByLayerIndexBuilders, layerIndex, publication);
                routableByServiceAndLayerIndex.TryAdd((publication.ServiceId, layerIndex), publication);
            }

            if (!string.IsNullOrEmpty(publication.ServiceId))
            {
                if (!routableByServiceAndIdentifierBuilders.TryGetValue(publication.ServiceId, out var identifierMap))
                {
                    identifierMap = new Dictionary<string, MetadataV2Layer>(StringComparer.OrdinalIgnoreCase);
                    routableByServiceAndIdentifierBuilders.Add(publication.ServiceId, identifierMap);
                }

                TryAddIdentifier(identifierMap, publication.ServiceLocalId, publication);
                TryAddIdentifier(identifierMap, publication.Path, publication);
                TryAddIdentifier(identifierMap, publication.Metadata.Name, publication);
                TryAddIdentifier(identifierMap, publication.Metadata.Id, publication);
            }
        }

        var routablePublicationsByServiceAndIdentifier = routableByServiceAndIdentifierBuilders.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);

        // Precompute storage SRID per storage layer ID without throwing on incomplete or duplicate fixtures.
        var storageSridByStorageLayerId = new Dictionary<int, int?>();
        var invalidOrAmbiguousStorageSridLayerIds = new HashSet<int>();
        var matchedBindingByLayerId = new Dictionary<int, MetadataV2StorageBinding>();
        var ambiguousLayerIds = new HashSet<int>();

        foreach (var binding in graph.StorageBindings)
        {
            if (binding.StorageLayerId is not int storageLayerId ||
                !resourcesByStorageLayerId.TryGetValue(storageLayerId, out var resource) ||
                !string.Equals(binding.ResourceId, resource.Metadata.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (ambiguousLayerIds.Contains(storageLayerId))
            {
                continue;
            }

            if (!matchedBindingByLayerId.TryAdd(storageLayerId, binding))
            {
                matchedBindingByLayerId.Remove(storageLayerId);
                ambiguousLayerIds.Add(storageLayerId);
                invalidOrAmbiguousStorageSridLayerIds.Add(storageLayerId);
            }
        }

        foreach (var (storageLayerId, binding) in matchedBindingByLayerId)
        {
            if (binding.StorageType != MetadataV2StorageType.RelationalTable)
            {
                continue;
            }

            var resource = resourcesByStorageLayerId[storageLayerId];
            var (_, tableName) = binding.ResolveRelationalTable();
            var primaryKeyColumn = binding.ReadStringOption("primaryKeyColumn")
                ?? resource.FindPrimaryIdField()?.Name
                ?? FieldNames.ObjectId;
            var srid = binding.ReadIntOption("storageSrid")
                ?? binding.ReadIntOption("srid")
                ?? resource.Spatial?.StorageCrs?.ResolveSrid()
                ?? resource.ReadSrid();

            if (!string.IsNullOrWhiteSpace(tableName) &&
                !string.IsNullOrWhiteSpace(primaryKeyColumn) &&
                srid is not <= 0)
            {
                storageSridByStorageLayerId[storageLayerId] = srid;
            }
            else
            {
                invalidOrAmbiguousStorageSridLayerIds.Add(storageLayerId);
            }
        }

        return new MetadataV2GraphIndex(
            catalogsById.ToFrozenDictionary(StringComparer.Ordinal),
            resourcesById.ToFrozenDictionary(StringComparer.Ordinal),
            resourcesByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            connectionsById.ToFrozenDictionary(StringComparer.Ordinal),
            storageBindingsById.ToFrozenDictionary(StringComparer.Ordinal),
            storageBindingsByResourceMap,
            storageBindingsByStorageLayerId.ToFrozenDictionary(),
            resourcesByStorageLayerId.ToFrozenDictionary(),
            servicesById.ToFrozenDictionary(StringComparer.Ordinal),
            servicesByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            publicationsById.ToFrozenDictionary(StringComparer.Ordinal),
            publicationsByServiceMap,
            publicationsByResourceMap,
            serviceNamesByResource,
            projectionProfilesById.ToFrozenDictionary(StringComparer.Ordinal),
            projectionProfilesByTargetMap,
            policiesById.ToFrozenDictionary(StringComparer.Ordinal),
            rolesById.ToFrozenDictionary(StringComparer.Ordinal),
            resourcesByStyleResourceIdMap,
            routablePublicationIds.ToFrozenSet(StringComparer.Ordinal),
            FreezeGroupedMap(routableByStorageLayerBuilders, EqualityComparer<int>.Default),
            FreezeGroupedMap(routableByLayerIndexBuilders, EqualityComparer<int>.Default),
            routableByServiceAndLayerIndex.ToFrozenDictionary(),
            routablePublicationsByServiceAndIdentifier,
            storageSridByStorageLayerId.ToFrozenDictionary(),
            invalidOrAmbiguousStorageSridLayerIds.ToFrozenSet());
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

    private static bool IsPublicationRoutable(
        MetadataV2Layer publication,
        Dictionary<string, MetadataV2Service> servicesById,
        Dictionary<string, MetadataV2Object> resourcesById,
        Dictionary<string, MetadataV2StorageBinding> storageBindingsById,
        FrozenDictionary<string, ImmutableArray<MetadataV2StorageBinding>> storageBindingsByResourceMap,
        Dictionary<string, MetadataV2Connection> connectionsById,
        out MetadataV2StorageBinding? binding)
    {
        binding = null;
        if (!servicesById.TryGetValue(publication.ServiceId, out var service) || !service.IsRoutable())
        {
            return false;
        }

        resourcesById.TryGetValue(publication.ResourceId, out var resource);
        if (!publication.IsRoutable(resource))
        {
            return false;
        }

        binding = ResolveBinding(publication, resource, storageBindingsById, storageBindingsByResourceMap);
        if (binding is null)
        {
            return true;
        }

        MetadataV2Connection? connection = null;
        if (!string.IsNullOrEmpty(binding.ConnectionId))
        {
            connectionsById.TryGetValue(binding.ConnectionId, out connection);
        }

        return MetadataV2GraphSnapshotExtensions.IsRoutableBinding(binding, resource, connection);
    }

    private static MetadataV2StorageBinding? ResolveBinding(
        MetadataV2Layer publication,
        MetadataV2Object? resource,
        Dictionary<string, MetadataV2StorageBinding> storageBindingsById,
        FrozenDictionary<string, ImmutableArray<MetadataV2StorageBinding>> storageBindingsByResourceMap)
    {
        if (!string.IsNullOrEmpty(publication.StorageBindingId) &&
            storageBindingsById.TryGetValue(publication.StorageBindingId, out var explicitBinding))
        {
            return explicitBinding;
        }

        if (resource is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(resource.PrimaryStorageBindingId) &&
            storageBindingsById.TryGetValue(resource.PrimaryStorageBindingId, out var primary))
        {
            return primary;
        }

        return storageBindingsByResourceMap.TryGetValue(resource.Metadata.Id, out var bindings) && !bindings.IsEmpty
            ? bindings[0]
            : null;
    }

    private sealed class FrozenLookup<TKey, TValue>(FrozenDictionary<TKey, ImmutableArray<TValue>> map)
        : ILookup<TKey, TValue>
        where TKey : notnull
    {
        public int Count => map.Count;

        public IEnumerable<TValue> this[TKey key]
            => map.TryGetValue(key, out var values) ? values : ImmutableArray<TValue>.Empty;

        public bool Contains(TKey key) => map.ContainsKey(key);

        public IEnumerator<IGrouping<TKey, TValue>> GetEnumerator()
        {
            foreach (var pair in map)
            {
                yield return new FrozenGrouping(pair.Key, pair.Value);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class FrozenGrouping(TKey key, ImmutableArray<TValue> values) : IGrouping<TKey, TValue>
        {
            public TKey Key => key;

            public IEnumerator<TValue> GetEnumerator() => ((IEnumerable<TValue>)values).GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}

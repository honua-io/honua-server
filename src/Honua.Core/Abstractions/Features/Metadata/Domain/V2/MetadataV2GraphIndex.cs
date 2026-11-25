// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed partial class MetadataV2GraphIndex
{
    private MetadataV2GraphIndex(GraphIndexState state)
    {
        CatalogsById = state.CatalogsById;
        ResourcesById = state.ResourcesById;
        ResourcesByName = state.ResourcesByName;
        ConnectionsById = state.ConnectionsById;
        StorageBindingsById = state.StorageBindingsById;
        StorageBindingsByResourceMap = state.StorageBindingsByResourceMap;
        StorageBindingsByResource = new FrozenLookup<string, MetadataV2StorageBinding>(state.StorageBindingsByResourceMap);
        StorageBindingsByStorageLayerId = state.StorageBindingsByStorageLayerId;
        ResourcesByStorageLayerId = state.ResourcesByStorageLayerId;
        ServicesById = state.ServicesById;
        ServicesByName = state.ServicesByName;
        PublicationsById = state.PublicationsById;
        PublicationsByServiceMap = state.PublicationsByServiceMap;
        PublicationsByService = new FrozenLookup<string, MetadataV2Layer>(state.PublicationsByServiceMap);
        PublicationsByResource = new FrozenLookup<string, MetadataV2Layer>(state.PublicationsByResourceMap);
        ServiceNamesByResource = state.ServiceNamesByResource;
        ProjectionProfilesById = state.ProjectionProfilesById;
        ProjectionProfilesByTarget = new FrozenLookup<string, MetadataV2ProjectionProfile>(state.ProjectionProfilesByTargetMap);
        PoliciesById = state.PoliciesById;
        RolesById = state.RolesById;
        ResourcesByStyleResourceId = new FrozenLookup<string, MetadataV2Object>(state.ResourcesByStyleResourceIdMap);
        RoutablePublicationIds = state.RoutablePublicationIds;
        RoutablePublicationsByStorageLayerId = state.RoutablePublicationsByStorageLayerId;
        RoutablePublicationsByLayerIndex = state.RoutablePublicationsByLayerIndex;
        RoutablePublicationsByServiceAndLayerIndex = state.RoutablePublicationsByServiceAndLayerIndex;
        RoutablePublicationsByServiceAndIdentifier = state.RoutablePublicationsByServiceAndIdentifier;
        StorageSridByStorageLayerId = state.StorageSridByStorageLayerId;
        InvalidOrAmbiguousStorageSridLayerIds = state.InvalidOrAmbiguousStorageSridLayerIds;
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

    private sealed class GraphIndexState
    {
        public required FrozenDictionary<string, MetadataV2Catalog> CatalogsById { get; init; }

        public required FrozenDictionary<string, MetadataV2Object> ResourcesById { get; init; }

        public required FrozenDictionary<string, MetadataV2Object> ResourcesByName { get; init; }

        public required FrozenDictionary<string, MetadataV2Connection> ConnectionsById { get; init; }

        public required FrozenDictionary<string, MetadataV2StorageBinding> StorageBindingsById { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2StorageBinding>> StorageBindingsByResourceMap { get; init; }

        public required FrozenDictionary<int, MetadataV2StorageBinding> StorageBindingsByStorageLayerId { get; init; }

        public required FrozenDictionary<int, MetadataV2Object> ResourcesByStorageLayerId { get; init; }

        public required FrozenDictionary<string, MetadataV2Service> ServicesById { get; init; }

        public required FrozenDictionary<string, MetadataV2Service> ServicesByName { get; init; }

        public required FrozenDictionary<string, MetadataV2Layer> PublicationsById { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2Layer>> PublicationsByServiceMap { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2Layer>> PublicationsByResourceMap { get; init; }

        public required FrozenDictionary<string, ImmutableArray<string>> ServiceNamesByResource { get; init; }

        public required FrozenDictionary<string, MetadataV2ProjectionProfile> ProjectionProfilesById { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2ProjectionProfile>> ProjectionProfilesByTargetMap { get; init; }

        public required FrozenDictionary<string, MetadataV2Policy> PoliciesById { get; init; }

        public required FrozenDictionary<string, MetadataV2Role> RolesById { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2Object>> ResourcesByStyleResourceIdMap { get; init; }

        public required FrozenSet<string> RoutablePublicationIds { get; init; }

        public required FrozenDictionary<int, ImmutableArray<MetadataV2Layer>> RoutablePublicationsByStorageLayerId { get; init; }

        public required FrozenDictionary<int, ImmutableArray<MetadataV2Layer>> RoutablePublicationsByLayerIndex { get; init; }

        public required FrozenDictionary<(string ServiceId, int LayerIndex), MetadataV2Layer> RoutablePublicationsByServiceAndLayerIndex { get; init; }

        public required FrozenDictionary<string, FrozenDictionary<string, MetadataV2Layer>> RoutablePublicationsByServiceAndIdentifier { get; init; }

        public required FrozenDictionary<int, int?> StorageSridByStorageLayerId { get; init; }

        public required FrozenSet<int> InvalidOrAmbiguousStorageSridLayerIds { get; init; }
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

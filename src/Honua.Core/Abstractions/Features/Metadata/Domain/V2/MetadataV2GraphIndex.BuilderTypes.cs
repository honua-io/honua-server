// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed partial class MetadataV2GraphIndex
{
    private sealed class PrimaryGraphIndexes
    {
        public required Dictionary<string, MetadataV2Catalog> CatalogsById { get; init; }

        public required Dictionary<string, MetadataV2Object> ResourcesById { get; init; }

        public required Dictionary<string, MetadataV2Object> ResourcesByName { get; init; }

        public required Dictionary<string, MetadataV2Connection> ConnectionsById { get; init; }

        public required StorageBindingIndexes Storage { get; init; }

        public required ServiceIndexes Services { get; init; }

        public required PublicationIndexes Publications { get; init; }

        public required ProjectionProfileIndexes Profiles { get; init; }

        public required Dictionary<string, MetadataV2Policy> PoliciesById { get; init; }

        public required Dictionary<string, MetadataV2Role> RolesById { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2Object>> ResourcesByStyleMap { get; init; }
    }

    private sealed class StorageBindingIndexes
    {
        public required Dictionary<string, MetadataV2StorageBinding> ById { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2StorageBinding>> ByResourceMap { get; init; }

        public required Dictionary<int, MetadataV2StorageBinding> ByStorageLayerId { get; init; }

        public required Dictionary<int, MetadataV2Object> ResourcesByStorageLayerId { get; init; }
    }

    private sealed class ServiceIndexes
    {
        public required Dictionary<string, MetadataV2Service> ById { get; init; }

        public required Dictionary<string, MetadataV2Service> ByName { get; init; }
    }

    private sealed class PublicationIndexes
    {
        public required Dictionary<string, MetadataV2Layer> ById { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2Layer>> ByServiceMap { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2Layer>> ByResourceMap { get; init; }

        public required FrozenDictionary<string, ImmutableArray<string>> ServiceNamesByResource { get; init; }
    }

    private sealed class ProjectionProfileIndexes
    {
        public required Dictionary<string, MetadataV2ProjectionProfile> ById { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2ProjectionProfile>> ByTargetMap { get; init; }
    }

    private sealed class RoutabilityLookupContext
    {
        public required Dictionary<string, MetadataV2Service> ServicesById { get; init; }

        public required Dictionary<string, MetadataV2Object> ResourcesById { get; init; }

        public required Dictionary<string, MetadataV2StorageBinding> StorageBindingsById { get; init; }

        public required FrozenDictionary<string, ImmutableArray<MetadataV2StorageBinding>> StorageBindingsByResourceMap { get; init; }

        public required Dictionary<string, MetadataV2Connection> ConnectionsById { get; init; }

        public required Dictionary<string, MetadataV2Layer> PublicationsById { get; init; }
    }

    private readonly record struct RoutabilityResult
    {
        public bool IsRoutable { get; init; }

        public MetadataV2StorageBinding? Binding { get; init; }

        public static RoutabilityResult NotRoutable => new() { IsRoutable = false };

        public static RoutabilityResult Routable(MetadataV2StorageBinding? binding)
            => new() { IsRoutable = true, Binding = binding };
    }

    private sealed class RoutablePublicationIndexes
    {
        public required FrozenSet<string> Ids { get; init; }

        public required FrozenDictionary<int, ImmutableArray<MetadataV2Layer>> ByStorageLayerId { get; init; }

        public required FrozenDictionary<int, ImmutableArray<MetadataV2Layer>> ByLayerIndex { get; init; }

        public required FrozenDictionary<(string ServiceId, int LayerIndex), MetadataV2Layer> ByServiceAndLayerIndex { get; init; }

        public required FrozenDictionary<string, FrozenDictionary<string, MetadataV2Layer>> ByServiceAndIdentifier { get; init; }
    }

    private sealed class RoutablePublicationBuilders
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<int, ImmutableArray<MetadataV2Layer>.Builder> _byStorageLayer = new();
        private readonly Dictionary<int, ImmutableArray<MetadataV2Layer>.Builder> _byLayerIndex = new();
        private readonly Dictionary<(string ServiceId, int LayerIndex), MetadataV2Layer> _byServiceAndLayerIndex = new();
        private readonly Dictionary<string, Dictionary<string, MetadataV2Layer>> _byServiceAndIdentifier = new(StringComparer.Ordinal);

        public void Add(
            MetadataV2Layer publication,
            MetadataV2StorageBinding? binding,
            Dictionary<string, MetadataV2Layer> publicationsById)
        {
            if (!string.IsNullOrEmpty(publication.Metadata.Id) &&
                publicationsById.TryGetValue(publication.Metadata.Id, out var canonical) &&
                ReferenceEquals(canonical, publication))
            {
                _ids.Add(publication.Metadata.Id);
            }

            if (binding?.StorageLayerId is int storageLayerId)
            {
                AddToGroupedMap(_byStorageLayer, storageLayerId, publication);
            }

            if (publication.LayerIndex is int layerIndex)
            {
                AddToGroupedMap(_byLayerIndex, layerIndex, publication);
                _byServiceAndLayerIndex.TryAdd((publication.ServiceId, layerIndex), publication);
            }

            AddIdentifiers(publication);
        }

        public RoutablePublicationIndexes Freeze()
            => new()
            {
                Ids = _ids.ToFrozenSet(StringComparer.Ordinal),
                ByStorageLayerId = FreezeGroupedMap(_byStorageLayer, EqualityComparer<int>.Default),
                ByLayerIndex = FreezeGroupedMap(_byLayerIndex, EqualityComparer<int>.Default),
                ByServiceAndLayerIndex = _byServiceAndLayerIndex.ToFrozenDictionary(),
                ByServiceAndIdentifier = _byServiceAndIdentifier.ToFrozenDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                    StringComparer.Ordinal),
            };

        private void AddIdentifiers(MetadataV2Layer publication)
        {
            if (string.IsNullOrEmpty(publication.ServiceId))
            {
                return;
            }

            if (!_byServiceAndIdentifier.TryGetValue(publication.ServiceId, out var identifierMap))
            {
                identifierMap = new Dictionary<string, MetadataV2Layer>(StringComparer.OrdinalIgnoreCase);
                _byServiceAndIdentifier.Add(publication.ServiceId, identifierMap);
            }

            TryAddIdentifier(identifierMap, publication.ServiceLocalId, publication);
            TryAddIdentifier(identifierMap, publication.Path, publication);
            TryAddIdentifier(identifierMap, publication.Metadata.Name, publication);
            TryAddIdentifier(identifierMap, publication.Metadata.Id, publication);
        }
    }

    private sealed class StorageSridIndexes
    {
        public required FrozenDictionary<int, int?> ByStorageLayerId { get; init; }

        public required FrozenSet<int> InvalidOrAmbiguousLayerIds { get; init; }
    }

    private readonly record struct StorageSridResolution
    {
        public bool IsValid { get; init; }

        public int? Srid { get; init; }
    }
}

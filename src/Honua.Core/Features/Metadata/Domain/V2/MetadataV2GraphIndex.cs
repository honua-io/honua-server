// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// O(1) lookup indexes built from a <see cref="MetadataV2Graph"/>.
/// Snapshots are immutable; the index is safe for concurrent reads.
/// </summary>
public sealed class MetadataV2GraphIndex
{
    private MetadataV2GraphIndex(
        IReadOnlyDictionary<string, MetadataV2Catalog> catalogsById,
        IReadOnlyDictionary<string, MetadataV2Resource> resourcesById,
        IReadOnlyDictionary<string, MetadataV2Resource> resourcesByName,
        IReadOnlyDictionary<string, MetadataV2Connection> connectionsById,
        IReadOnlyDictionary<string, MetadataV2StorageBinding> storageBindingsById,
        ILookup<string, MetadataV2StorageBinding> storageBindingsByResource,
        IReadOnlyDictionary<int, MetadataV2StorageBinding> storageBindingsByStorageLayerId,
        IReadOnlyDictionary<int, MetadataV2Resource> resourcesByStorageLayerId,
        IReadOnlyDictionary<string, MetadataV2Service> servicesById,
        IReadOnlyDictionary<string, MetadataV2Service> servicesByName,
        IReadOnlyDictionary<string, MetadataV2Publication> publicationsById,
        ILookup<string, MetadataV2Publication> publicationsByService,
        ILookup<string, MetadataV2Publication> publicationsByResource,
        IReadOnlyDictionary<string, MetadataV2ProjectionProfile> projectionProfilesById,
        ILookup<string, MetadataV2ProjectionProfile> projectionProfilesByTarget,
        IReadOnlyDictionary<string, MetadataV2Policy> policiesById,
        IReadOnlyDictionary<string, MetadataV2Role> rolesById)
    {
        CatalogsById = catalogsById;
        ResourcesById = resourcesById;
        ResourcesByName = resourcesByName;
        ConnectionsById = connectionsById;
        StorageBindingsById = storageBindingsById;
        StorageBindingsByResource = storageBindingsByResource;
        StorageBindingsByStorageLayerId = storageBindingsByStorageLayerId;
        ResourcesByStorageLayerId = resourcesByStorageLayerId;
        ServicesById = servicesById;
        ServicesByName = servicesByName;
        PublicationsById = publicationsById;
        PublicationsByService = publicationsByService;
        PublicationsByResource = publicationsByResource;
        ProjectionProfilesById = projectionProfilesById;
        ProjectionProfilesByTarget = projectionProfilesByTarget;
        PoliciesById = policiesById;
        RolesById = rolesById;
    }

    public IReadOnlyDictionary<string, MetadataV2Catalog> CatalogsById { get; }
    public IReadOnlyDictionary<string, MetadataV2Resource> ResourcesById { get; }
    public IReadOnlyDictionary<string, MetadataV2Resource> ResourcesByName { get; }
    public IReadOnlyDictionary<string, MetadataV2Connection> ConnectionsById { get; }
    public IReadOnlyDictionary<string, MetadataV2StorageBinding> StorageBindingsById { get; }
    public ILookup<string, MetadataV2StorageBinding> StorageBindingsByResource { get; }

    /// <summary>
    /// Lookup from <see cref="MetadataV2StorageBinding.StorageLayerId"/> to the
    /// owning storage binding. Bindings with a null StorageLayerId are excluded.
    /// </summary>
    public IReadOnlyDictionary<int, MetadataV2StorageBinding> StorageBindingsByStorageLayerId { get; }

    /// <summary>
    /// Lookup from <see cref="MetadataV2StorageBinding.StorageLayerId"/> to the
    /// owning canonical resource. This is the most frequent runtime lookup: the
    /// storage backends (IFeatureReader, IFeatureWriter, ILayerStyleCatalog,
    /// OutputCacheInvalidationService) all hand off integer layer ids, and the
    /// V2 protocol handlers need the resource to do field / spatial / temporal
    /// resolution. Bindings without a StorageLayerId or with a missing resource
    /// reference are excluded.
    /// </summary>
    public IReadOnlyDictionary<int, MetadataV2Resource> ResourcesByStorageLayerId { get; }

    public IReadOnlyDictionary<string, MetadataV2Service> ServicesById { get; }
    public IReadOnlyDictionary<string, MetadataV2Service> ServicesByName { get; }
    public IReadOnlyDictionary<string, MetadataV2Publication> PublicationsById { get; }
    public ILookup<string, MetadataV2Publication> PublicationsByService { get; }
    public ILookup<string, MetadataV2Publication> PublicationsByResource { get; }
    public IReadOnlyDictionary<string, MetadataV2ProjectionProfile> ProjectionProfilesById { get; }
    public ILookup<string, MetadataV2ProjectionProfile> ProjectionProfilesByTarget { get; }
    public IReadOnlyDictionary<string, MetadataV2Policy> PoliciesById { get; }
    public IReadOnlyDictionary<string, MetadataV2Role> RolesById { get; }

    /// <summary>
    /// Builds an index from a graph document.
    /// </summary>
    public static MetadataV2GraphIndex Build(MetadataV2Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var catalogsById = graph.Catalogs.ToDictionary(c => c.Metadata.Id, StringComparer.Ordinal);
        var resourcesById = graph.Resources.ToDictionary(r => r.Metadata.Id, StringComparer.Ordinal);
        // Multiple resources may share the same display name (different IDs / namespaces).
        // First-wins keeps the index deterministic and avoids ArgumentException on construction.
        var resourcesByName = new Dictionary<string, MetadataV2Resource>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in graph.Resources)
        {
            if (!string.IsNullOrEmpty(r.Metadata.Name))
            {
                resourcesByName.TryAdd(r.Metadata.Name, r);
            }
        }
        var connectionsById = graph.Connections.ToDictionary(c => c.Metadata.Id, StringComparer.Ordinal);
        var storageBindingsById = graph.StorageBindings.ToDictionary(s => s.Metadata.Id, StringComparer.Ordinal);
        var storageBindingsByResource = graph.StorageBindings.ToLookup(s => s.ResourceId, StringComparer.Ordinal);

        var storageBindingsByStorageLayerId = new Dictionary<int, MetadataV2StorageBinding>();
        var resourcesByStorageLayerId = new Dictionary<int, MetadataV2Resource>();
        foreach (var binding in graph.StorageBindings)
        {
            if (binding.StorageLayerId is not int storageLayerId) continue;
            // First-wins on collisions — graph validation surfaces duplicates
            // separately so the index stays deterministic even on a bad graph.
            storageBindingsByStorageLayerId.TryAdd(storageLayerId, binding);
            if (resourcesById.TryGetValue(binding.ResourceId, out var resource))
            {
                resourcesByStorageLayerId.TryAdd(storageLayerId, resource);
            }
        }

        var servicesById = graph.Services.ToDictionary(s => s.Metadata.Id, StringComparer.Ordinal);
        // Multiple V2 services may share the same display name when the same logical
        // service is exposed through multiple protocols (e.g. an OGC API Features
        // service + an Esri Feature Service for the same dataset). First-wins keeps
        // the index deterministic; consumers that need ambiguity-aware routing use
        // BuildPrimaryServiceMapV2 instead.
        var servicesByName = new Dictionary<string, MetadataV2Service>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in graph.Services)
        {
            if (!string.IsNullOrEmpty(s.Metadata.Name))
            {
                servicesByName.TryAdd(s.Metadata.Name, s);
            }
        }
        var publicationsById = graph.Publications.ToDictionary(p => p.Metadata.Id, StringComparer.Ordinal);
        var publicationsByService = graph.Publications.ToLookup(p => p.ServiceId, StringComparer.Ordinal);
        var publicationsByResource = graph.Publications.ToLookup(p => p.ResourceId, StringComparer.Ordinal);
        var projectionProfilesById = graph.ProjectionProfiles.ToDictionary(p => p.Metadata.Id, StringComparer.Ordinal);
        var projectionProfilesByTarget = graph.ProjectionProfiles.ToLookup(p => p.Target, StringComparer.OrdinalIgnoreCase);
        var policiesById = graph.Policies.ToDictionary(p => p.Metadata.Id, StringComparer.Ordinal);
        var rolesById = graph.Roles.ToDictionary(r => r.Metadata.Id, StringComparer.Ordinal);

        return new MetadataV2GraphIndex(
            catalogsById,
            resourcesById,
            resourcesByName,
            connectionsById,
            storageBindingsById,
            storageBindingsByResource,
            storageBindingsByStorageLayerId,
            resourcesByStorageLayerId,
            servicesById,
            servicesByName,
            publicationsById,
            publicationsByService,
            publicationsByResource,
            projectionProfilesById,
            projectionProfilesByTarget,
            policiesById,
            rolesById);
    }
}

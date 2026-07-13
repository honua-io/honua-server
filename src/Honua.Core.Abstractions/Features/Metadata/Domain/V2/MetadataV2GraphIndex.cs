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
        IReadOnlyDictionary<string, MetadataV2Role> rolesById,
        ILookup<string, MetadataV2Resource> resourcesByStyleResourceId)
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
        ResourcesByStyleResourceId = resourcesByStyleResourceId;
    }

    /// <summary>Catalogs keyed by their identifier.</summary>
    public IReadOnlyDictionary<string, MetadataV2Catalog> CatalogsById { get; }

    /// <summary>Resources keyed by their identifier.</summary>
    public IReadOnlyDictionary<string, MetadataV2Resource> ResourcesById { get; }

    /// <summary>Resources keyed by their name.</summary>
    public IReadOnlyDictionary<string, MetadataV2Resource> ResourcesByName { get; }

    /// <summary>Connections keyed by their identifier.</summary>
    public IReadOnlyDictionary<string, MetadataV2Connection> ConnectionsById { get; }

    /// <summary>Storage bindings keyed by their identifier.</summary>
    public IReadOnlyDictionary<string, MetadataV2StorageBinding> StorageBindingsById { get; }

    /// <summary>Storage bindings grouped by the resource they bind.</summary>
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

    /// <summary>Services keyed by their identifier.</summary>
    public IReadOnlyDictionary<string, MetadataV2Service> ServicesById { get; }

    /// <summary>Services keyed by their name.</summary>
    public IReadOnlyDictionary<string, MetadataV2Service> ServicesByName { get; }

    /// <summary>Publications keyed by their identifier.</summary>
    public IReadOnlyDictionary<string, MetadataV2Publication> PublicationsById { get; }

    /// <summary>Publications grouped by the service they belong to.</summary>
    public ILookup<string, MetadataV2Publication> PublicationsByService { get; }

    /// <summary>Publications grouped by the resource they expose.</summary>
    public ILookup<string, MetadataV2Publication> PublicationsByResource { get; }

    /// <summary>Projection profiles keyed by their identifier.</summary>
    public IReadOnlyDictionary<string, MetadataV2ProjectionProfile> ProjectionProfilesById { get; }

    /// <summary>Projection profiles grouped by their target.</summary>
    public ILookup<string, MetadataV2ProjectionProfile> ProjectionProfilesByTarget { get; }

    /// <summary>Policies keyed by their identifier.</summary>
    public IReadOnlyDictionary<string, MetadataV2Policy> PoliciesById { get; }

    /// <summary>Roles keyed by their identifier.</summary>
    public IReadOnlyDictionary<string, MetadataV2Role> RolesById { get; }

    /// <summary>
    /// Reverse lookup from a style resource id to every resource that
    /// references it through <see cref="MetadataV2Resource.StyleResourceIds"/>.
    /// </summary>
    public ILookup<string, MetadataV2Resource> ResourcesByStyleResourceId { get; }

    /// <summary>
    /// Builds an index from a graph document.
    /// </summary>
    public static MetadataV2GraphIndex Build(MetadataV2Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // First-wins on duplicate Metadata.Id values: graph validation surfaces duplicates as soft
        // errors rather than preventing persistence, so loading a graph with duplicate ids (e.g. a
        // hand-edited file fixture or a legacy snapshot) must not crash with an opaque
        // ArgumentException from ToDictionary. The TryAdd strategy here matches the documented
        // strategy on the name / storage-layer-id maps below and keeps the index deterministic
        // even on a bad graph.
        var catalogsById = new Dictionary<string, MetadataV2Catalog>(StringComparer.Ordinal);
        foreach (var c in graph.Catalogs) catalogsById.TryAdd(c.Metadata.Id, c);

        var resourcesById = new Dictionary<string, MetadataV2Resource>(StringComparer.Ordinal);
        foreach (var r in graph.Resources) resourcesById.TryAdd(r.Metadata.Id, r);

        // Multiple resources may share the same display name (different IDs / namespaces).
        // First-wins keeps the index deterministic and avoids ArgumentException on construction.
        var resourcesByName = new Dictionary<string, MetadataV2Resource>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in graph.Resources.Where(r => !string.IsNullOrEmpty(r.Metadata.Name)))
        {
            resourcesByName.TryAdd(r.Metadata.Name, r);
        }

        var connectionsById = new Dictionary<string, MetadataV2Connection>(StringComparer.Ordinal);
        foreach (var c in graph.Connections) connectionsById.TryAdd(c.Metadata.Id, c);

        var storageBindingsById = new Dictionary<string, MetadataV2StorageBinding>(StringComparer.Ordinal);
        foreach (var s in graph.StorageBindings) storageBindingsById.TryAdd(s.Metadata.Id, s);
        var storageBindingsByResource = graph.StorageBindings.ToLookup(s => s.ResourceId, StringComparer.Ordinal);

        var storageBindingsByStorageLayerId = new Dictionary<int, MetadataV2StorageBinding>();
        var resourcesByStorageLayerId = new Dictionary<int, MetadataV2Resource>();
        // judgment call: the `is not int storageLayerId` pattern-match both filters and binds the
        // extracted value used below; a .Where() would have to re-extract it, which is messier
        // than the guard-clause form here.
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

        var servicesById = new Dictionary<string, MetadataV2Service>(StringComparer.Ordinal);
        foreach (var s in graph.Services) servicesById.TryAdd(s.Metadata.Id, s);

        // Multiple V2 services may share the same display name when the same logical
        // service is exposed through multiple protocols (e.g. an OGC API Features
        // service + an Esri Feature Service for the same dataset). First-wins keeps
        // the index deterministic; consumers that need ambiguity-aware routing use
        // BuildPrimaryServiceMapV2 instead.
        var servicesByName = new Dictionary<string, MetadataV2Service>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in graph.Services.Where(s => !string.IsNullOrEmpty(s.Metadata.Name)))
        {
            servicesByName.TryAdd(s.Metadata.Name, s);
        }

        var publicationsById = new Dictionary<string, MetadataV2Publication>(StringComparer.Ordinal);
        foreach (var p in graph.Publications) publicationsById.TryAdd(p.Metadata.Id, p);

        var publicationsByService = graph.Publications.ToLookup(p => p.ServiceId, StringComparer.Ordinal);
        var publicationsByResource = graph.Publications.ToLookup(p => p.ResourceId, StringComparer.Ordinal);

        var projectionProfilesById = new Dictionary<string, MetadataV2ProjectionProfile>(StringComparer.Ordinal);
        foreach (var p in graph.ProjectionProfiles) projectionProfilesById.TryAdd(p.Metadata.Id, p);

        var projectionProfilesByTarget = graph.ProjectionProfiles.ToLookup(p => p.Target, StringComparer.OrdinalIgnoreCase);

        var policiesById = new Dictionary<string, MetadataV2Policy>(StringComparer.Ordinal);
        foreach (var p in graph.Policies) policiesById.TryAdd(p.Metadata.Id, p);

        var rolesById = new Dictionary<string, MetadataV2Role>(StringComparer.Ordinal);
        foreach (var r in graph.Roles) rolesById.TryAdd(r.Metadata.Id, r);

        // Flatten (resource, styleResourceId) pairs into a reverse lookup so
        // callers can answer "which resources use style X?" in O(1).
        var resourcesByStyleResourceId = graph.Resources
            .SelectMany(r => (r.StyleResourceIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => (StyleId: id, Resource: r)))
            .ToLookup(pair => pair.StyleId, pair => pair.Resource, StringComparer.Ordinal);

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
            rolesById,
            resourcesByStyleResourceId);
    }
}

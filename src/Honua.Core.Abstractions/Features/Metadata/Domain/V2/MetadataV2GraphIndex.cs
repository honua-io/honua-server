// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed class MetadataV2GraphIndex
{
    private MetadataV2GraphIndex(
        IReadOnlyDictionary<string, MetadataV2Catalog> catalogsById,
        IReadOnlyDictionary<string, MetadataV2Object> resourcesById,
        IReadOnlyDictionary<string, MetadataV2Object> resourcesByName,
        IReadOnlyDictionary<string, MetadataV2Connection> connectionsById,
        IReadOnlyDictionary<string, MetadataV2StorageBinding> storageBindingsById,
        ILookup<string, MetadataV2StorageBinding> storageBindingsByResource,
        IReadOnlyDictionary<int, MetadataV2StorageBinding> storageBindingsByStorageLayerId,
        IReadOnlyDictionary<int, MetadataV2Object> resourcesByStorageLayerId,
        IReadOnlyDictionary<string, MetadataV2Service> servicesById,
        IReadOnlyDictionary<string, MetadataV2Service> servicesByName,
        IReadOnlyDictionary<string, MetadataV2Layer> publicationsById,
        ILookup<string, MetadataV2Layer> publicationsByService,
        ILookup<string, MetadataV2Layer> publicationsByResource,
        IReadOnlyDictionary<string, ImmutableArray<string>> serviceNamesByResource,
        IReadOnlyDictionary<string, MetadataV2ProjectionProfile> projectionProfilesById,
        ILookup<string, MetadataV2ProjectionProfile> projectionProfilesByTarget,
        IReadOnlyDictionary<string, MetadataV2Policy> policiesById,
        IReadOnlyDictionary<string, MetadataV2Role> rolesById,
        ILookup<string, MetadataV2Object> resourcesByStyleResourceId)
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
        ServiceNamesByResource = serviceNamesByResource;
        ProjectionProfilesById = projectionProfilesById;
        ProjectionProfilesByTarget = projectionProfilesByTarget;
        PoliciesById = policiesById;
        RolesById = rolesById;
        ResourcesByStyleResourceId = resourcesByStyleResourceId;
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

        var resourcesById = new Dictionary<string, MetadataV2Object>(StringComparer.Ordinal);
        foreach (var r in graph.Resources) resourcesById.TryAdd(r.Metadata.Id, r);

        // Multiple resources may share the same display name (different IDs / namespaces).
        // First-wins keeps the index deterministic and avoids ArgumentException on construction.
        var resourcesByName = new Dictionary<string, MetadataV2Object>(StringComparer.OrdinalIgnoreCase);
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
        var resourcesByStorageLayerId = new Dictionary<int, MetadataV2Object>();
        foreach (var binding in graph.StorageBindings.Where(binding => binding.StorageLayerId.HasValue))
        {
            var storageLayerId = binding.StorageLayerId.GetValueOrDefault();
            // First-wins on numeric storage-layer collisions keeps the index deterministic.
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

        var publicationsById = new Dictionary<string, MetadataV2Layer>(StringComparer.Ordinal);
        foreach (var p in graph.Publications) publicationsById.TryAdd(p.Metadata.Id, p);

        var publicationsByService = graph.Publications.ToLookup(p => p.ServiceId, StringComparer.Ordinal);
        var publicationsByResource = graph.Publications.ToLookup(p => p.ResourceId, StringComparer.Ordinal);

        // Security sources previously scanned PublicationsById for every read. Resolve
        // these immutable names once, retaining first-wins duplicate IDs and all scopes.
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

        var serviceNamesByResource = serviceNameSets.ToDictionary(
            pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal);


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
            serviceNamesByResource,
            projectionProfilesById,
            projectionProfilesByTarget,
            policiesById,
            rolesById,
            resourcesByStyleResourceId);
    }
}

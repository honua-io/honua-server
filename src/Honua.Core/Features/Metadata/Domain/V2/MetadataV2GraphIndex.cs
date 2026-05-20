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
        var resourcesByName = graph.Resources
            .Where(r => !string.IsNullOrEmpty(r.Metadata.Name))
            .ToDictionary(r => r.Metadata.Name, StringComparer.OrdinalIgnoreCase);
        var connectionsById = graph.Connections.ToDictionary(c => c.Metadata.Id, StringComparer.Ordinal);
        var storageBindingsById = graph.StorageBindings.ToDictionary(s => s.Metadata.Id, StringComparer.Ordinal);
        var storageBindingsByResource = graph.StorageBindings.ToLookup(s => s.ResourceId, StringComparer.Ordinal);
        var servicesById = graph.Services.ToDictionary(s => s.Metadata.Id, StringComparer.Ordinal);
        var servicesByName = graph.Services
            .Where(s => !string.IsNullOrEmpty(s.Metadata.Name))
            .ToDictionary(s => s.Metadata.Name, StringComparer.OrdinalIgnoreCase);
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

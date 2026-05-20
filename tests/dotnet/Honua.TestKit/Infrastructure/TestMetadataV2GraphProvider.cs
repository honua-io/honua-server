// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// In-memory <see cref="IMetadataV2GraphProvider"/> for tests. Holds a single graph
/// snapshot supplied at construction; callers can swap the snapshot at any time.
/// </summary>
public sealed class TestMetadataV2GraphProvider : IMetadataV2GraphProvider
{
    private MetadataV2GraphSnapshot _snapshot;

    public TestMetadataV2GraphProvider(MetadataV2Graph graph, string? etag = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _snapshot = new MetadataV2GraphSnapshot(
            graph,
            etag ?? $"\"test-{graph.Revision}\"",
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Replaces the snapshot returned by future calls.
    /// </summary>
    public void SetGraph(MetadataV2Graph graph, string? etag = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _snapshot = new MetadataV2GraphSnapshot(
            graph,
            etag ?? $"\"test-{graph.Revision}\"",
            DateTimeOffset.UtcNow);
    }

    public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        => new(_snapshot);

    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
        => new(_snapshot.Graph.Revision == revision ? _snapshot : null);
}

/// <summary>
/// Fluent builder for Metadata v2 graphs used in tests. Provides the minimum boilerplate
/// to assemble well-formed graphs with resources, services, publications, and storage
/// bindings without writing JSON.
/// </summary>
public sealed class TestMetadataV2GraphBuilder
{
    private long _revision = 1;
    private string _environment = "test";
    private readonly List<MetadataV2Resource> _resources = [];
    private readonly List<MetadataV2Connection> _connections = [];
    private readonly List<MetadataV2StorageBinding> _bindings = [];
    private readonly List<MetadataV2Service> _services = [];
    private readonly List<MetadataV2Publication> _publications = [];

    public TestMetadataV2GraphBuilder WithRevision(long revision)
    {
        _revision = revision;
        return this;
    }

    public TestMetadataV2GraphBuilder WithEnvironment(string environment)
    {
        _environment = environment;
        return this;
    }

    public TestMetadataV2GraphBuilder AddConnection(
        string id,
        string name,
        MetadataV2ConnectionType type = MetadataV2ConnectionType.Managed,
        string? provider = null)
    {
        _connections.Add(new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
            Type = type,
            Provider = provider,
        });
        return this;
    }

    public TestMetadataV2GraphBuilder AddResource(
        string id,
        string name,
        MetadataV2ResourceType type = MetadataV2ResourceType.FeatureDataset,
        IEnumerable<MetadataV2Field>? fields = null)
    {
        _resources.Add(new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
            Type = type,
            SchemaFields = fields?.ToArray() ?? Array.Empty<MetadataV2Field>(),
        });
        return this;
    }

    public TestMetadataV2GraphBuilder AddStorageBinding(
        string id,
        string resourceId,
        string locator,
        string? connectionId = null,
        MetadataV2StorageType storageType = MetadataV2StorageType.RelationalTable)
    {
        _bindings.Add(new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            ResourceId = resourceId,
            ConnectionId = connectionId,
            StorageType = storageType,
            Locator = locator,
        });

        // Attach to the resource's binding list so the graph is internally consistent.
        var resourceIndex = _resources.FindIndex(r => r.Metadata.Id == resourceId);
        if (resourceIndex >= 0)
        {
            var existing = _resources[resourceIndex];
            var updated = existing with
            {
                StorageBindingIds = existing.StorageBindingIds.Append(id).Distinct(StringComparer.Ordinal).ToArray(),
                PrimaryStorageBindingId = existing.PrimaryStorageBindingId ?? id,
            };
            _resources[resourceIndex] = updated;
        }
        return this;
    }

    public TestMetadataV2GraphBuilder AddService(
        string id,
        string name,
        MetadataV2ServiceType serviceType = MetadataV2ServiceType.OgcApiFeatures,
        string? route = null)
    {
        _services.Add(new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
            ServiceType = serviceType,
            Route = route,
        });
        return this;
    }

    public TestMetadataV2GraphBuilder AddPublication(
        string id,
        string serviceId,
        string resourceId,
        string? path = null,
        int? layerIndex = null,
        string? storageBindingId = null,
        string? serviceLocalId = null,
        MetadataV2PublicationType publicationType = MetadataV2PublicationType.OgcCollection)
    {
        _publications.Add(new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = serviceLocalId ?? id },
            ServiceId = serviceId,
            ResourceId = resourceId,
            StorageBindingId = storageBindingId,
            Path = path,
            LayerIndex = layerIndex,
            ServiceLocalId = serviceLocalId,
            PublicationType = publicationType,
        });

        // Attach to the service's publication list.
        var serviceIndex = _services.FindIndex(s => s.Metadata.Id == serviceId);
        if (serviceIndex >= 0)
        {
            var existing = _services[serviceIndex];
            var updated = existing with
            {
                PublicationIds = existing.PublicationIds.Append(id).Distinct(StringComparer.Ordinal).ToArray(),
            };
            _services[serviceIndex] = updated;
        }
        return this;
    }

    public MetadataV2Graph Build()
    {
        return new MetadataV2Graph
        {
            Revision = _revision,
            Environment = _environment,
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = _resources.ToArray(),
            Connections = _connections.ToArray(),
            StorageBindings = _bindings.ToArray(),
            Services = _services.ToArray(),
            Publications = _publications.ToArray(),
        };
    }

    public TestMetadataV2GraphProvider BuildProvider()
        => new(Build());
}

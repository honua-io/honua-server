// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Middleware;

namespace Honua.TestKit.Infrastructure;

public sealed class TestMetadataV2GraphProvider : IMetadataV2GraphStore
{
    private const string BaselineSchemaKey = "<baseline>";

    private readonly ConcurrentDictionary<string, MetadataV2GraphSnapshot> _bySchema = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MetadataV2GraphSnapshot> _history = new(StringComparer.Ordinal);
    private MetadataV2GraphSnapshot _baseline;
    private MetadataV2Graph? _activateAfterNextRead;

    public TestMetadataV2GraphProvider(MetadataV2Graph graph, string? etag = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _baseline = new MetadataV2GraphSnapshot(
            graph,
            etag ?? $"\"test-{graph.Revision}\"",
            DateTimeOffset.UtcNow);
        _history[RevisionKey(BaselineSchemaKey, graph.Revision)] = _baseline;
    }

    public void SetGraph(MetadataV2Graph graph, string? etag = null, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var snapshot = new MetadataV2GraphSnapshot(
            graph,
            etag ?? $"\"test-{graph.Revision}\"",
            DateTimeOffset.UtcNow);

        var key = ResolveSchemaKey(schema);
        _history[RevisionKey(key, graph.Revision)] = snapshot;
        if (string.Equals(key, BaselineSchemaKey, StringComparison.Ordinal))
        {
            _baseline = snapshot;
        }
        else
        {
            _bySchema[key] = snapshot;
        }
    }

    public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = CurrentSnapshot();
        var activatedGraph = Interlocked.Exchange(ref _activateAfterNextRead, null);
        if (activatedGraph is not null)
        {
            SetGraph(activatedGraph);
        }

        return new(snapshot);
    }

    public void ClearPendingActivation() => Volatile.Write(ref _activateAfterNextRead, null);

    public void ActivateAfterNextRead(MetadataV2Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Volatile.Write(ref _activateAfterNextRead, graph);
    }

    internal MetadataV2GraphSnapshot GetCurrentForSchema(string? schema)
    {
        var key = ResolveSchemaKey(schema);
        if (!string.Equals(key, BaselineSchemaKey, StringComparison.Ordinal) &&
            _bySchema.TryGetValue(key, out var scoped))
        {
            return scoped;
        }

        return _baseline;
    }

    public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
    {
        var key = ResolveSchemaKey(null);
        if (_history.TryGetValue(RevisionKey(key, revision), out var snapshot))
        {
            return new(snapshot);
        }

        return !string.Equals(key, BaselineSchemaKey, StringComparison.Ordinal) &&
               _history.TryGetValue(RevisionKey(BaselineSchemaKey, revision), out snapshot)
            ? new(snapshot)
            : new((MetadataV2GraphSnapshot?)null);
    }

    public Task<MetadataV2GraphSnapshot> SaveAsync(
        MetadataV2Graph graph,
        string? expectedEtag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var current = CurrentSnapshot();
        if (expectedEtag is not null &&
            !string.Equals(expectedEtag, current.Etag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Metadata v2 graph ETag mismatch.");
        }

        var snapshot = new MetadataV2GraphSnapshot(
            graph,
            $"\"test-{graph.Revision}\"",
            DateTimeOffset.UtcNow);

        var key = ResolveSchemaKey(null);
        _history[RevisionKey(key, graph.Revision)] = snapshot;
        if (string.Equals(key, BaselineSchemaKey, StringComparison.Ordinal))
        {
            _baseline = snapshot;
        }
        else
        {
            _bySchema[key] = snapshot;
        }

        return Task.FromResult(snapshot);
    }

    public async Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(
        long revision,
        string? expectedCurrentEtag,
        CancellationToken cancellationToken = default)
    {
        var current = CurrentSnapshot();
        if (expectedCurrentEtag is not null &&
            !string.Equals(expectedCurrentEtag, current.Etag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Metadata v2 graph ETag mismatch.");
        }

        var retained = await GetByRevisionAsync(revision, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Metadata v2 revision {revision} is not retained.");
        var activated = new MetadataV2GraphSnapshot(
            retained.Graph,
            retained.Etag,
            DateTimeOffset.UtcNow);
        var key = ResolveSchemaKey(null);
        if (string.Equals(key, BaselineSchemaKey, StringComparison.Ordinal))
        {
            _baseline = activated;
        }
        else
        {
            _bySchema[key] = activated;
        }

        return activated;
    }

    private static string RevisionKey(string schema, long revision)
        => $"{schema}\0{revision}";

    private MetadataV2GraphSnapshot CurrentSnapshot()
    {
        var key = ResolveSchemaKey(null);
        if (!string.Equals(key, BaselineSchemaKey, StringComparison.Ordinal) &&
            _bySchema.TryGetValue(key, out var scoped))
        {
            return scoped;
        }

        return _baseline;
    }

    private static string ResolveSchemaKey(string? schema)
    {
        var resolved = schema ?? SchemaContext.AmbientCurrentSchema;
        return string.IsNullOrWhiteSpace(resolved) ? BaselineSchemaKey : resolved;
    }
}

public sealed class TestMetadataV2GraphBuilder
{
    private long _revision = 1;
    private string _environment = "test";
    private readonly List<MetadataV2Object> _resources = [];
    private readonly List<MetadataV2Connection> _connections = [];
    private readonly List<MetadataV2StorageBinding> _bindings = [];
    private readonly List<MetadataV2Service> _services = [];
    private readonly List<MetadataV2Layer> _publications = [];

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
        MetadataV2ObjectType type = MetadataV2ObjectType.FeatureDataset,
        IEnumerable<MetadataV2Field>? fields = null,
        AccessPolicy? accessPolicy = null,
        MetadataV2ObjectSpatial? spatial = null,
        MetadataV2ObjectTemporal? temporal = null,
        IReadOnlyDictionary<string, string>? annotations = null,
        IReadOnlyDictionary<string, JsonElement>? extensions = null)
    {
        _resources.Add(new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = id,
                Name = name,
                Annotations = annotations ?? new Dictionary<string, string>()
            },
            Type = type,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            SchemaFields = fields?.ToArray() ?? Array.Empty<MetadataV2Field>(),
            AccessPolicy = accessPolicy,
            Spatial = spatial,
            Temporal = temporal,
            Extensions = extensions ?? new Dictionary<string, JsonElement>(),
        });
        return this;
    }

    public TestMetadataV2GraphBuilder WithOwnerEditPolicy(string resourceId, MetadataV2OwnerEditPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var index = _resources.FindIndex(resource => resource.Metadata.Id == resourceId);
        if (index < 0)
        {
            throw new ArgumentException($"Resource '{resourceId}' was not added.", nameof(resourceId));
        }

        _resources[index] = _resources[index] with { OwnerEditPolicy = policy };
        return this;
    }

    public TestMetadataV2GraphBuilder WithPublicationCapabilities(string publicationId, params string[] capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var index = _publications.FindIndex(publication => publication.Metadata.Id == publicationId);
        if (index < 0)
        {
            throw new ArgumentException($"Publication '{publicationId}' was not added.", nameof(publicationId));
        }

        _publications[index] = _publications[index] with { Capabilities = capabilities };
        return this;
    }

    public TestMetadataV2GraphBuilder AddStorageBinding(
        string id,
        string resourceId,
        string locator,
        string? connectionId = null,
        MetadataV2StorageType storageType = MetadataV2StorageType.RelationalTable,
        int? storageLayerId = null,
        IReadOnlyDictionary<string, JsonElement>? options = null)
    {
        _bindings.Add(new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            ResourceId = resourceId,
            ConnectionId = connectionId,
            StorageType = storageType,
            Locator = locator,
            StorageLayerId = storageLayerId,
            Options = options ?? new Dictionary<string, JsonElement>(),
        });

        // Attach to the resource's binding list so the graph is internally consistent.
        var resourceIndex = _resources.FindIndex(r => r.Metadata.Id == resourceId);
        if (resourceIndex >= 0)
        {
            var existing = _resources[resourceIndex];
            var updated = existing with
            {
                // StorageBindingIds[0] is primary by convention (design slice 56/N).
                StorageBindingIds = existing.StorageBindingIds.Append(id).Distinct(StringComparer.Ordinal).ToArray(),
            };
            _resources[resourceIndex] = updated;
        }
        return this;
    }

    public TestMetadataV2GraphBuilder AddService(
        string id,
        string name,
        string? route = null,
        IReadOnlyList<string>? protocols = null,
        AccessPolicy? accessPolicy = null,
        IReadOnlyDictionary<string, JsonElement>? options = null)
    {
        _services.Add(new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = name },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            Route = route,
            Protocols = protocols ?? Array.Empty<string>(),
            AccessPolicy = accessPolicy,
            Options = options ?? new Dictionary<string, JsonElement>(),
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
        MetadataV2LayerType publicationType = MetadataV2LayerType.OgcCollection,
        bool isPrimary = false)
    {
        // The three legacy publication-identification slots collapsed onto one
        // typed MetadataV2LayerIdentifier in design slice 61/N. Builder
        // resolution: layerIndex (numeric) > serviceLocalId (name-based) > "".
        // path is now passed through Identifier.PathOverride.
        string idValue;
        bool isNumeric;
        if (layerIndex is int idx)
        {
            idValue = idx.ToString(System.Globalization.CultureInfo.InvariantCulture);
            isNumeric = true;
        }
        else
        {
            idValue = serviceLocalId ?? string.Empty;
            isNumeric = false;
        }

        _publications.Add(new MetadataV2Layer
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = serviceLocalId ?? id },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            ServiceId = serviceId,
            ResourceId = resourceId,
            StorageBindingId = storageBindingId,
            Identifier = new MetadataV2LayerIdentifier
            {
                Value = idValue,
                IsNumeric = isNumeric,
                PathOverride = path,
            },
            PublicationType = publicationType,
            IsPrimary = isPrimary,
        });

        // Service→publication membership is derived from publication.ServiceId
        // (the redundant Service.PublicationIds slot was removed in design slice 55/N),
        // so no service-side update is needed.
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

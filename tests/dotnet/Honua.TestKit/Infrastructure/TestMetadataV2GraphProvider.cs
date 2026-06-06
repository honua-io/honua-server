// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Middleware;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// In-memory <see cref="IMetadataV2GraphProvider"/> for tests.
/// </summary>
/// <remarks>
/// <para>
/// Parallel-isolation contract (#1359): the shared test server is process-wide singleton
/// state, so a single mutable graph snapshot would let two schema-isolated tests running
/// concurrently in the same assembly clobber each other's metadata. To make the shared
/// server safe to run under parallel xUnit collections, this provider partitions the graph
/// <b>by database schema</b>.
/// </para>
/// <para>
/// Reads (<see cref="GetCurrentAsync"/>) resolve the per-request schema from the ambient
/// <see cref="SchemaContext"/> that <c>TestSchemaMiddleware</c> populates from the
/// <c>X-Honua-Test-Schema</c> header, so a request issued by test A only ever sees graph
/// mutations that test A applied to its own schema. Writes from the fixture
/// (<see cref="SetGraph(MetadataV2Graph, string?, string?)"/>) target an explicit schema key
/// supplied by the owning <c>WebAppFixture</c>. When no schema is in scope (isolated-mode
/// fixtures that build their own server, or tests that never send the header) the provider
/// falls back to a shared baseline partition — those callers already get a dedicated server
/// instance, so the baseline is private to them.
/// </para>
/// </remarks>
public sealed class TestMetadataV2GraphProvider : IMetadataV2GraphStore
{
    private const string BaselineSchemaKey = "<baseline>";

    private readonly ConcurrentDictionary<string, MetadataV2GraphSnapshot> _bySchema = new(StringComparer.Ordinal);
    private MetadataV2GraphSnapshot _baseline;

    public TestMetadataV2GraphProvider(MetadataV2Graph graph, string? etag = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _baseline = new MetadataV2GraphSnapshot(
            graph,
            etag ?? $"\"test-{graph.Revision}\"",
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Replaces the snapshot for the supplied schema partition. When <paramref name="schema"/>
    /// is null the ambient request schema is used, falling back to the shared baseline.
    /// </summary>
    public void SetGraph(MetadataV2Graph graph, string? etag = null, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var snapshot = new MetadataV2GraphSnapshot(
            graph,
            etag ?? $"\"test-{graph.Revision}\"",
            DateTimeOffset.UtcNow);

        StoreSnapshot(snapshot, schema);
    }

    public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        => new(CurrentSnapshot());

    /// <summary>
    /// Returns the snapshot for an explicit schema partition (or the shared baseline when
    /// <paramref name="schema"/> resolves to no partition). Fixture mutation helpers use this
    /// to read-modify-write a test's own partition without depending on an ambient request
    /// schema being in scope.
    /// </summary>
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
        var snapshot = CurrentSnapshot();
        return new(snapshot.Graph.Revision == revision ? snapshot : null);
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

        StoreSnapshot(snapshot, schema: null);
        return Task.FromResult(snapshot);
    }

    private void StoreSnapshot(MetadataV2GraphSnapshot snapshot, string? schema)
    {
        var key = ResolveSchemaKey(schema);
        if (string.Equals(key, BaselineSchemaKey, StringComparison.Ordinal))
        {
            _baseline = snapshot;
        }
        else
        {
            _bySchema[key] = snapshot;
        }
    }

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
        IEnumerable<MetadataV2Field>? fields = null,
        AccessPolicy? accessPolicy = null,
        MetadataV2ResourceSpatial? spatial = null,
        MetadataV2ResourceTemporal? temporal = null,
        IReadOnlyDictionary<string, string>? annotations = null,
        IReadOnlyDictionary<string, JsonElement>? extensions = null)
    {
        _resources.Add(new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = id,
                Name = name,
                Annotations = annotations ?? new Dictionary<string, string>()
            },
            Type = type,
            SchemaFields = fields?.ToArray() ?? Array.Empty<MetadataV2Field>(),
            AccessPolicy = accessPolicy,
            Spatial = spatial,
            Temporal = temporal,
            Extensions = extensions ?? new Dictionary<string, JsonElement>(),
        });
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
        MetadataV2PublicationType publicationType = MetadataV2PublicationType.OgcCollection,
        bool isPrimary = false)
    {
        // The three legacy publication-identification slots collapsed onto one
        // typed MetadataV2PublicationIdentifier in design slice 61/N. Builder
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

        _publications.Add(new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = serviceLocalId ?? id },
            ServiceId = serviceId,
            ResourceId = resourceId,
            StorageBindingId = storageBindingId,
            Identifier = new MetadataV2PublicationIdentifier
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Canonical Metadata v2 root graph.
/// </summary>
public sealed record MetadataV2Graph
{
    /// <summary>
    /// Canonical schema version for this graph document.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = MetadataV2Constants.SchemaVersion;

    /// <summary>
    /// Metadata v2 API version for the graph document.
    /// </summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = MetadataV2Constants.ApiVersion;

    /// <summary>
    /// Monotonic graph revision used by cache snapshots and migration diagnostics.
    /// </summary>
    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    /// <summary>
    /// Environment identifier, such as dev, staging, or production.
    /// </summary>
    [JsonPropertyName("environment")]
    public string Environment { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp when this graph snapshot was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Declared namespaces used by graph entity identifiers.
    /// </summary>
    [JsonPropertyName("namespaces")]
    public IReadOnlyList<string> Namespaces { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Graph metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Canonical resources. Publications expose these resources through services.
    /// </summary>
    [JsonPropertyName("resources")]
    public IReadOnlyList<MetadataV2Resource> Resources { get; init; } = Array.Empty<MetadataV2Resource>();

    /// <summary>
    /// Connections referenced by storage bindings.
    /// </summary>
    [JsonPropertyName("connections")]
    public IReadOnlyList<MetadataV2Connection> Connections { get; init; } = Array.Empty<MetadataV2Connection>();

    /// <summary>
    /// Physical storage bindings for canonical resources.
    /// </summary>
    [JsonPropertyName("storageBindings")]
    public IReadOnlyList<MetadataV2StorageBinding> StorageBindings { get; init; } = Array.Empty<MetadataV2StorageBinding>();

    /// <summary>
    /// Services that publish resources.
    /// </summary>
    [JsonPropertyName("services")]
    public IReadOnlyList<MetadataV2Service> Services { get; init; } = Array.Empty<MetadataV2Service>();

    /// <summary>
    /// Resource-first publication links from resourceId to serviceId.
    /// </summary>
    [JsonPropertyName("publications")]
    public IReadOnlyList<MetadataV2Publication> Publications { get; init; } = Array.Empty<MetadataV2Publication>();

    /// <summary>
    /// Extension data for the graph document.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Canonical Metadata v2 resource.
/// </summary>
public sealed record MetadataV2Resource
{
    /// <summary>
    /// Resource metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Logical resource type.
    /// </summary>
    [JsonPropertyName("type")]
    public MetadataV2ResourceType Type { get; init; } = MetadataV2ResourceType.FeatureDataset;

    /// <summary>
    /// Storage bindings that can materialize this canonical resource.
    /// <see cref="StorageBindingIds"/><c>[0]</c> is the primary binding by
    /// convention; secondary bindings (read replicas, alternative backends) come
    /// after. Empty for derived/document/external resources that have no
    /// physical storage.
    /// </summary>
    [JsonPropertyName("storageBindingIds")]
    public IReadOnlyList<string> StorageBindingIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The primary storage binding id for this resource — i.e.
    /// <c>StorageBindingIds[0]</c> when present, or <c>null</c> for resources
    /// with no physical storage.
    /// </summary>
    public string? PrimaryStorageBindingId =>
        StorageBindingIds.Count == 0 ? null : StorageBindingIds[0];

    /// <summary>
    /// Canonical schema fields and field-level semantic roles. The single source
    /// of truth for the resource's field set.
    /// </summary>
    [JsonPropertyName("schemaFields")]
    public IReadOnlyList<MetadataV2Field> SchemaFields { get; init; } = Array.Empty<MetadataV2Field>();

    /// <summary>
    /// Resource-to-resource relationships exposed by this resource.
    /// </summary>
    [JsonPropertyName("relationships")]
    public IReadOnlyList<MetadataV2Relationship> Relationships { get; init; } = Array.Empty<MetadataV2Relationship>();

    /// <summary>
    /// Optional access policy controlling who can read/write this resource.
    /// Composes with the owning service's <see cref="MetadataV2Service.AccessPolicy"/>
    /// under deny-wins semantics: both policies must pass for a request to be
    /// allowed. Resource-level denials are reported in preference to service-level
    /// denials. When both are unset, authentication is required by default; set
    /// <c>AllowAnonymous = true</c> at either level to open public access.
    /// </summary>
    [JsonPropertyName("accessPolicy")]
    public AccessPolicy? AccessPolicy { get; init; }

    /// <summary>
    /// Typed spatial metadata (CRS, geometry type, bbox, primary geometry field).
    /// Unset for non-spatial tabular resources.
    /// </summary>
    [JsonPropertyName("spatial")]
    public MetadataV2ResourceSpatial? Spatial { get; init; }

    /// <summary>
    /// Typed temporal metadata (time-field names + optional declared extent).
    /// Unset for non-time-aware resources.
    /// </summary>
    [JsonPropertyName("temporal")]
    public MetadataV2ResourceTemporal? Temporal { get; init; }

    /// <summary>
    /// Optional permanent filter applied to every query against this resource.
    /// Mirrors the v1 <c>LayerMetadata.PermanentFilter</c>. The storage backends
    /// (Postgres / MySql / DuckDB / SqlServer FeatureStores) honour this by ANDing
    /// it with the per-request filter before SQL translation.
    /// </summary>
    [JsonPropertyName("permanentFilter")]
    public MetadataV2PermanentFilter? PermanentFilter { get; init; }

    /// <summary>
    /// Extension data for the resource.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Connection used by storage bindings.
/// </summary>
public sealed record MetadataV2Connection
{
    /// <summary>
    /// Connection metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Connection type.
    /// </summary>
    [JsonPropertyName("type")]
    public MetadataV2ConnectionType Type { get; init; } = MetadataV2ConnectionType.Managed;

    /// <summary>
    /// Provider name, such as postgres, s3, stac, or honua.
    /// </summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>
    /// Endpoint or base URI, when safe to expose in metadata.
    /// </summary>
    [JsonPropertyName("endpoint")]
    public Uri? Endpoint { get; init; }

    /// <summary>
    /// Reference to secret material outside the metadata graph.
    /// </summary>
    [JsonPropertyName("secretRef")]
    public string? SecretRef { get; init; }

    /// <summary>
    /// Connection options.
    /// </summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Extension data for the connection.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Physical storage binding for a canonical resource.
/// </summary>
public sealed record MetadataV2StorageBinding
{
    /// <summary>
    /// Storage binding metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Canonical resource identifier this storage binding materializes.
    /// </summary>
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>
    /// Optional connection identifier.
    /// </summary>
    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; init; }

    /// <summary>
    /// Physical storage type. This is not a service or publication type.
    /// </summary>
    [JsonPropertyName("storageType")]
    public MetadataV2StorageType StorageType { get; init; } = MetadataV2StorageType.RelationalTable;

    /// <summary>
    /// Storage locator, such as table name, object key, URI, or API route.
    /// </summary>
    [JsonPropertyName("locator")]
    public string Locator { get; init; } = string.Empty;

    /// <summary>
    /// Integer handle identifying this binding inside its backing store. Distinct
    /// from <see cref="MetadataV2Publication.LayerIndex"/> (which is the service-local
    /// /protocol-facing index). <c>IFeatureReader</c>, <c>ILayerStyleCatalog</c>,
    /// and <c>OutputCacheInvalidationService</c> all take this integer as their
    /// "layer id" argument. Required for any feature/raster/table resource;
    /// unused for derived/document/external resource types.
    /// </summary>
    [JsonPropertyName("storageLayerId")]
    public int? StorageLayerId { get; init; }

    /// <summary>
    /// Capabilities supported by this storage binding.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<MetadataV2StorageBindingCapability> Capabilities { get; init; } =
        Array.Empty<MetadataV2StorageBindingCapability>();

    /// <summary>
    /// Binding-specific options.
    /// </summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Extension data for the storage binding.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Returns true when the storage binding declares the capability.
    /// </summary>
    /// <param name="capability">Capability to check.</param>
    /// <returns>True when the capability is present.</returns>
    public bool Supports(MetadataV2StorageBindingCapability capability)
    {
        return Capabilities.Contains(capability);
    }
}

/// <summary>
/// Service that can publish canonical resources.
/// </summary>
public sealed record MetadataV2Service
{
    /// <summary>
    /// Service metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Service route or base path.
    /// </summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    /// <summary>
    /// Optional access policy controlling who can read/write this service.
    /// Composes with each <see cref="MetadataV2Resource.AccessPolicy"/> on resources
    /// published through this service under deny-wins semantics (see the docs on
    /// <see cref="MetadataV2Resource.AccessPolicy"/>). A service-level deny blocks
    /// access to every resource on the service; a service-level
    /// <c>AllowAnonymous = true</c> does NOT override a resource-level restriction.
    /// </summary>
    [JsonPropertyName("accessPolicy")]
    public AccessPolicy? AccessPolicy { get; init; }

    /// <summary>
    /// Service-level output CRS. Map/tile services use this as their declared
    /// rendering CRS; feature services use it when no per-request CRS override is
    /// provided. Independent of the resource-level <see cref="MetadataV2ResourceSpatial.SpatialReference"/>
    /// because a service can re-project source data on the fly.
    /// </summary>
    [JsonPropertyName("spatialReference")]
    public MetadataV2SpatialReference? SpatialReference { get; init; }

    /// <summary>
    /// Protocols exposed by this service. The single source of truth for protocol
    /// gating — <c>ServiceProtocols.IsProtocolEnabled(MetadataV2Service, string)</c>
    /// checks membership here directly. Values are the canonical
    /// <c>ServiceProtocols.*</c> string constants. Empty means the service exposes
    /// nothing; it does NOT mean "everything".
    /// </summary>
    [JsonPropertyName("protocols")]
    public IReadOnlyList<string> Protocols { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Primary protocol identifier for routing/display purposes — defined as
    /// <see cref="Protocols"/>[0] when present, otherwise null.
    /// </summary>
    [JsonIgnore]
    public string? PrimaryProtocol => Protocols.Count == 0 ? null : Protocols[0];

    /// <summary>
    /// Service-specific options.
    /// </summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Extension data for the service.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Resource-first publication link from a canonical resource to a service.
/// </summary>
public sealed record MetadataV2Publication
{
    /// <summary>
    /// Publication metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Canonical resource identifier being published.
    /// </summary>
    [JsonPropertyName("resourceId")]
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>
    /// Service identifier through which the resource is published.
    /// </summary>
    [JsonPropertyName("serviceId")]
    public string ServiceId { get; init; } = string.Empty;

    /// <summary>
    /// Optional storage binding identifier used for this publication.
    /// </summary>
    [JsonPropertyName("storageBindingId")]
    public string? StorageBindingId { get; init; }

    /// <summary>
    /// Publication type. This is not a storage type.
    /// </summary>
    [JsonPropertyName("publicationType")]
    public MetadataV2PublicationType PublicationType { get; init; } = MetadataV2PublicationType.OgcCollection;

    /// <summary>
    /// Optional title override for this service or catalog publication.
    /// </summary>
    [JsonPropertyName("titleOverride")]
    public string? TitleOverride { get; init; }

    /// <summary>
    /// Unified protocol-facing identifier (collection id, layer name, or stringified
    /// layer index). Collapses the prior <c>LayerIndex</c> / <c>ServiceLocalId</c>
    /// / <c>Path</c> trio so the three cannot desync.
    /// </summary>
    [JsonPropertyName("identifier")]
    public MetadataV2PublicationIdentifier Identifier { get; init; } = new();

    /// <summary>
    /// Computed integer layer index (legacy GeoServices-style routing). Returns
    /// the int parsed from <see cref="Identifier"/> when
    /// <see cref="MetadataV2PublicationIdentifier.IsNumeric"/> is true; otherwise <c>null</c>.
    /// Kept as a derived property so existing call sites that read
    /// <c>publication.LayerIndex</c> compile unchanged.
    /// </summary>
    [JsonIgnore]
    public int? LayerIndex => Identifier.IsNumeric
        && int.TryParse(Identifier.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : null;

    /// <summary>
    /// Computed full URL path override read from <see cref="Identifier"/>.
    /// Kept as a derived property so existing call sites that read
    /// <c>publication.Path</c> compile unchanged.
    /// </summary>
    [JsonIgnore]
    public string? Path => Identifier.PathOverride;

    /// <summary>
    /// Computed service-local id read from <see cref="Identifier"/>'s value.
    /// Kept as a derived property so existing call sites that read
    /// <c>publication.ServiceLocalId</c> compile unchanged.
    /// </summary>
    [JsonIgnore]
    public string? ServiceLocalId => string.IsNullOrEmpty(Identifier.Value) ? null : Identifier.Value;

    /// <summary>
    /// When true, this publication is the primary publication of its resource on
    /// its service. Used by resolvers that need to pick "the" publication of a
    /// resource (e.g. cache invalidation, mutation events, redirect-on-name lookups)
    /// when a resource is published through more than one service or more than once
    /// on a single service. At most one publication per (resourceId, serviceId)
    /// should set this. Graph integrity validation enforces the constraint.
    /// </summary>
    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; init; }

    /// <summary>
    /// Publication-specific options. The catch-all bag for publication-shape
    /// extensions that don't deserve a typed slot (per-publication output
    /// formats, capability overrides, field aliases, …). Compare to the
    /// per-entity <see cref="Extensions"/> dictionary — Options is for
    /// publication-specific config the producer knows about; Extensions is for
    /// out-of-band annotations a third-party tool attaches.
    /// </summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Extension data for the publication.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

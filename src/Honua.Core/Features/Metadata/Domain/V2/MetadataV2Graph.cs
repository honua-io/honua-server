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
    /// Catalog definitions that can project canonical resources.
    /// </summary>
    [JsonPropertyName("catalogs")]
    public IReadOnlyList<MetadataV2Catalog> Catalogs { get; init; } = Array.Empty<MetadataV2Catalog>();

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
    /// Projection profiles for service and catalog target formats.
    /// </summary>
    [JsonPropertyName("projectionProfiles")]
    public IReadOnlyList<MetadataV2ProjectionProfile> ProjectionProfiles { get; init; } =
        Array.Empty<MetadataV2ProjectionProfile>();

    /// <summary>
    /// Policy definitions referenced by resources, services, publications, and roles.
    /// </summary>
    [JsonPropertyName("policies")]
    public IReadOnlyList<MetadataV2Policy> Policies { get; init; } = Array.Empty<MetadataV2Policy>();

    /// <summary>
    /// Role definitions used by metadata access control.
    /// </summary>
    [JsonPropertyName("roles")]
    public IReadOnlyList<MetadataV2Role> Roles { get; init; } = Array.Empty<MetadataV2Role>();

    /// <summary>
    /// Runtime snapshot details for cache-safe graph materialization.
    /// </summary>
    [JsonPropertyName("runtime")]
    public MetadataV2RuntimeSnapshot Runtime { get; init; } = new();

    /// <summary>
    /// Declared extension points for graph consumers.
    /// </summary>
    [JsonPropertyName("extensionPoints")]
    public IReadOnlyList<MetadataV2ExtensionPoint> ExtensionPoints { get; init; } = Array.Empty<MetadataV2ExtensionPoint>();

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
    /// </summary>
    [JsonPropertyName("storageBindingIds")]
    public IReadOnlyList<string> StorageBindingIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional primary storage binding identifier.
    /// </summary>
    [JsonPropertyName("primaryStorageBindingId")]
    public string? PrimaryStorageBindingId { get; init; }

    /// <summary>
    /// Optional schema or field metadata for the resource.
    /// </summary>
    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; init; }

    /// <summary>
    /// Canonical schema fields and field-level semantic roles.
    /// </summary>
    [JsonPropertyName("schemaFields")]
    public IReadOnlyList<MetadataV2Field> SchemaFields { get; init; } = Array.Empty<MetadataV2Field>();

    /// <summary>
    /// Style resources attached to this canonical resource.
    /// </summary>
    [JsonPropertyName("styleResourceIds")]
    public IReadOnlyList<string> StyleResourceIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Policy identifiers attached to this resource.
    /// </summary>
    [JsonPropertyName("policyIds")]
    public IReadOnlyList<string> PolicyIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Resource-to-resource relationships exposed by this resource (the v2 equivalent of
    /// the v1 <c>LayerDefinition.LayerRelationships</c> set).
    /// </summary>
    [JsonPropertyName("relationships")]
    public IReadOnlyList<MetadataV2Relationship> Relationships { get; init; } = Array.Empty<MetadataV2Relationship>();

    /// <summary>
    /// Optional access policy controlling who can read/write this resource.
    /// </summary>
    [JsonPropertyName("accessPolicy")]
    public AccessPolicy? AccessPolicy { get; init; }

    /// <summary>
    /// Optional spatial extent, CRS, or geometry metadata.
    /// </summary>
    [JsonPropertyName("spatial")]
    public JsonElement? Spatial { get; init; }

    /// <summary>
    /// Optional temporal extent metadata.
    /// </summary>
    [JsonPropertyName("temporal")]
    public JsonElement? Temporal { get; init; }

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

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
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

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
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

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
    /// Public service type. This is not a storage type.
    /// </summary>
    [JsonPropertyName("serviceType")]
    public MetadataV2ServiceType ServiceType { get; init; } = MetadataV2ServiceType.OgcApiFeatures;

    /// <summary>
    /// Service route or base path.
    /// </summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    /// <summary>
    /// Optional publication identifiers exposed by this service.
    /// </summary>
    [JsonPropertyName("publicationIds")]
    public IReadOnlyList<string> PublicationIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional access policy controlling who can read/write this service.
    /// </summary>
    [JsonPropertyName("accessPolicy")]
    public AccessPolicy? AccessPolicy { get; init; }

    /// <summary>
    /// Protocol identifiers explicitly enabled on this service. When null, the
    /// <see cref="ServiceType"/> implies a single canonical protocol (matched by
    /// <c>MetadataV2ServiceTypeMapping.Map</c>); when set, this list lets a service
    /// expose more than one protocol simultaneously (e.g. an
    /// <see cref="MetadataV2ServiceType.OgcApiFeatures"/> service that also advertises
    /// <c>OGC-API-Maps</c>, <c>OGC-API-Tiles</c>, or <c>OGC-API-Coverages</c> on the same
    /// publications). Values match the v1 <c>ServiceProtocols.*</c> constants for source
    /// compatibility.
    /// </summary>
    [JsonPropertyName("enabledProtocols")]
    public IReadOnlyList<string>? EnabledProtocols { get; init; }

    /// <summary>
    /// Service-specific options.
    /// </summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

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
    /// Service path or catalog route override for this publication.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// Service-local layer index when the target protocol requires one.
    /// </summary>
    [JsonPropertyName("layerIndex")]
    public int? LayerIndex { get; init; }

    /// <summary>
    /// Format identifiers supported by this publication.
    /// </summary>
    [JsonPropertyName("supportedFormats")]
    public IReadOnlyList<string> SupportedFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Service-specific field aliases.
    /// </summary>
    [JsonPropertyName("fieldAliases")]
    public IReadOnlyDictionary<string, string> FieldAliases { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Publication capabilities after service and storage validation.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Route segment, layer id, collection id, or other service-local publication key.
    /// </summary>
    [JsonPropertyName("serviceLocalId")]
    public string? ServiceLocalId { get; init; }

    /// <summary>
    /// Publication-specific options.
    /// </summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    /// <summary>
    /// Extension data for the publication.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Catalog target that can expose canonical resources.
/// </summary>
public sealed record MetadataV2Catalog
{
    /// <summary>
    /// Catalog metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Catalog target identifier, such as ogc-records, dcat, stac, or esri-portal.
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// Catalog-specific options.
    /// </summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    /// <summary>
    /// Extension data for the catalog.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Mapping profile for projecting canonical metadata into service or catalog formats.
/// </summary>
public sealed record MetadataV2ProjectionProfile
{
    /// <summary>
    /// Projection profile metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Target format identifier.
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// Required semantic keys for this profile.
    /// </summary>
    [JsonPropertyName("requiredSemantics")]
    public IReadOnlyList<string> RequiredSemantics { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Profile-specific options.
    /// </summary>
    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    /// <summary>
    /// Extension data for the projection profile.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Metadata policy definition.
/// </summary>
public sealed record MetadataV2Policy
{
    /// <summary>
    /// Policy metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Policy engine or policy language identifier.
    /// </summary>
    [JsonPropertyName("engine")]
    public string Engine { get; init; } = string.Empty;

    /// <summary>
    /// Policy effect, such as allow, deny, mask, or audit.
    /// </summary>
    [JsonPropertyName("effect")]
    public string Effect { get; init; } = string.Empty;

    /// <summary>
    /// Policy-specific rules.
    /// </summary>
    [JsonPropertyName("rules")]
    public IReadOnlyDictionary<string, JsonElement> Rules { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    /// <summary>
    /// Extension data for the policy.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Metadata role definition.
/// </summary>
public sealed record MetadataV2Role
{
    /// <summary>
    /// Role metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Permission identifiers granted by the role.
    /// </summary>
    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Policy identifiers attached to the role.
    /// </summary>
    [JsonPropertyName("policyIds")]
    public IReadOnlyList<string> PolicyIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    /// <summary>
    /// Extension data for the role.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Runtime metadata for cache-safe graph snapshots.
/// </summary>
public sealed record MetadataV2RuntimeSnapshot
{
    /// <summary>
    /// Runtime metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    /// <summary>
    /// Cache key for the materialized runtime snapshot.
    /// </summary>
    [JsonPropertyName("cacheKey")]
    public string? CacheKey { get; init; }

    /// <summary>
    /// Source graph revision represented by this runtime snapshot.
    /// </summary>
    [JsonPropertyName("sourceRevision")]
    public long? SourceRevision { get; init; }

    /// <summary>
    /// Optional cache entity tag for snapshot consumers.
    /// </summary>
    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    /// <summary>
    /// Optional cache expiry for derived runtime snapshots.
    /// </summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    /// <summary>
    /// Extension data for the runtime snapshot.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

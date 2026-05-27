// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Scene.Domain;
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
    private string? _primaryStorageBindingId;
    private IReadOnlyList<string>? _storageBindingIds;
    private IReadOnlyList<MetadataV2Field>? _schemaFields;
    private IReadOnlyList<string>? _policyIds;
    private IReadOnlyList<MetadataV2Relationship>? _relationships;
    private IReadOnlyList<string>? _styleResourceIds;
    private IReadOnlyDictionary<string, JsonElement>? _extensions;

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
    public IReadOnlyList<string> StorageBindingIds
    {
        get => _storageBindingIds ?? Array.Empty<string>();
        init => _storageBindingIds = value;
    }

    /// <summary>
    /// The primary storage binding id for this resource — i.e.
    /// <c>StorageBindingIds[0]</c> when present, or <c>null</c> for resources
    /// with no physical storage.
    /// </summary>
    [JsonPropertyName("primaryStorageBindingId")]
    public string? PrimaryStorageBindingId
    {
        get => _primaryStorageBindingId ?? (StorageBindingIds.Count == 0 ? null : StorageBindingIds[0]);
        init => _primaryStorageBindingId = value;
    }

    /// <summary>
    /// Canonical schema fields and field-level semantic roles. The single source
    /// of truth for the resource's field set.
    /// </summary>
    [JsonPropertyName("schemaFields")]
    public IReadOnlyList<MetadataV2Field> SchemaFields
    {
        get => _schemaFields ?? Array.Empty<MetadataV2Field>();
        init => _schemaFields = value;
    }

    /// <summary>
    /// Policy identifiers attached to this resource.
    /// </summary>
    [JsonPropertyName("policyIds")]
    public IReadOnlyList<string> PolicyIds
    {
        get => _policyIds ?? Array.Empty<string>();
        init => _policyIds = value;
    }

    /// <summary>
    /// Resource-to-resource relationships exposed by this resource.
    /// </summary>
    [JsonPropertyName("relationships")]
    public IReadOnlyList<MetadataV2Relationship> Relationships
    {
        get => _relationships ?? Array.Empty<MetadataV2Relationship>();
        init => _relationships = value;
    }

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
    /// Storage backends honour this by ANDing it with the per-request filter
    /// before SQL translation.
    /// </summary>
    [JsonPropertyName("permanentFilter")]
    public MetadataV2PermanentFilter? PermanentFilter { get; init; }

    /// <summary>
    /// Optional extrusion metadata used by FeatureServer layer metadata and 3D Tiles generation.
    /// </summary>
    [JsonPropertyName("extrusion")]
    public MetadataV2ExtrusionInfo? Extrusion { get; init; }

    /// <summary>
    /// Optional attribute-driven 3D symbology for scene / 3D Tiles generation.
    /// Null for resources with no thematic 3D styling; the generator then
    /// bakes a plain opaque-white material. When present, the generator bakes
    /// the resolved per-feature color into the tile content and emits the
    /// equivalent <c>3d-tiles-styling</c> style-metadata contract alongside the
    /// tileset.
    /// </summary>
    [JsonPropertyName("symbology3D")]
    public Symbology3D? Symbology3D { get; init; }

    /// <summary>
    /// Style resources that render this resource. <c>[0]</c> is the primary
    /// style by convention. Each entry MUST reference a declared resource of
    /// <see cref="MetadataV2ResourceType.Style"/>.
    /// </summary>
    [JsonPropertyName("styleResourceIds")]
    public IReadOnlyList<string> StyleResourceIds
    {
        get => _styleResourceIds ?? Array.Empty<string>();
        init => _styleResourceIds = value;
    }

    /// <summary>
    /// Style payload — populated on resources whose
    /// <see cref="Type"/> is <see cref="MetadataV2ResourceType.Style"/>.
    /// </summary>
    [JsonPropertyName("style")]
    public MetadataV2ResourceStyle? Style { get; init; }

    /// <summary>
    /// Optional rendering / display hints used by map and feature services
    /// (min/max scale, default visibility, display field, queryable, Z/M).
    /// </summary>
    [JsonPropertyName("display")]
    public MetadataV2ResourceDisplay? Display { get; init; }

    /// <summary>
    /// Optional editor-tracking and edit-capability hints used by
    /// FeatureServer-style edit endpoints.
    /// </summary>
    [JsonPropertyName("editing")]
    public MetadataV2ResourceEditing? Editing { get; init; }

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    /// <summary>
    /// Extension data for the resource.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions
    {
        get => _extensions ?? EmptyJsonMap;
        init => _extensions = value;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyJsonMap =
        new Dictionary<string, JsonElement>();
}

/// <summary>
/// Display / rendering hints for a <see cref="MetadataV2Resource"/>.
/// </summary>
public sealed record MetadataV2ResourceDisplay
{
    /// <summary>Minimum scale denominator at which the resource is drawn.</summary>
    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    /// <summary>Maximum scale denominator at which the resource is drawn.</summary>
    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    /// <summary>Whether the layer is visible by default in clients. Defaults to true.</summary>
    [JsonPropertyName("defaultVisibility")]
    public bool DefaultVisibility { get; init; } = true;

    /// <summary>
    /// Name of the field used as the display / label field. Must reference a
    /// declared <see cref="MetadataV2Resource.SchemaFields"/> entry when set.
    /// </summary>
    [JsonPropertyName("displayField")]
    public string? DisplayField { get; init; }

    /// <summary>True when the resource supports attribute queries. Defaults to true.</summary>
    [JsonPropertyName("queryable")]
    public bool Queryable { get; init; } = true;

    /// <summary>True when the resource carries Z values in its geometries.</summary>
    [JsonPropertyName("hasZ")]
    public bool HasZ { get; init; }

    /// <summary>True when the resource carries M values in its geometries.</summary>
    [JsonPropertyName("hasM")]
    public bool HasM { get; init; }
}

/// <summary>
/// Editing capability and editor-tracking field hints for a
/// <see cref="MetadataV2Resource"/>.
/// </summary>
public sealed record MetadataV2ResourceEditing
{
    /// <summary>
    /// Name of the global-id field (typically a UUID) used as an
    /// edit-stable identifier. References a declared
    /// <see cref="MetadataV2Resource.SchemaFields"/> entry when set.
    /// </summary>
    [JsonPropertyName("globalIdField")]
    public string? GlobalIdField { get; init; }

    /// <summary>Name of the field tracking the creating user.</summary>
    [JsonPropertyName("creatorField")]
    public string? CreatorField { get; init; }

    /// <summary>Name of the field tracking the creation timestamp.</summary>
    [JsonPropertyName("createdAtField")]
    public string? CreatedAtField { get; init; }

    /// <summary>Name of the field tracking the last editing user.</summary>
    [JsonPropertyName("editorField")]
    public string? EditorField { get; init; }

    /// <summary>Name of the field tracking the last update timestamp.</summary>
    [JsonPropertyName("updatedAtField")]
    public string? UpdatedAtField { get; init; }

    /// <summary>True when records on this resource may be modified. Defaults to true.</summary>
    [JsonPropertyName("canModify")]
    public bool CanModify { get; init; } = true;

    /// <summary>True when the resource supports attachments.</summary>
    [JsonPropertyName("supportsAttachments")]
    public bool SupportsAttachments { get; init; }

    /// <summary>True when the resource supports related-record edits. Defaults to true.</summary>
    [JsonPropertyName("supportsRelatedRecords")]
    public bool SupportsRelatedRecords { get; init; } = true;
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
    private IReadOnlyDictionary<string, JsonElement>? _options;
    private IReadOnlyDictionary<string, JsonElement>? _extensions;

    /// <summary>
    /// Service metadata and identity.
    /// </summary>
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    private IReadOnlyList<string>? _protocols;

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
    public IReadOnlyList<string> Protocols
    {
        get => _protocols ?? EnabledProtocols ?? Array.Empty<string>();
        init => _protocols = value;
    }

    /// <summary>
    /// Legacy v2 metadata-release protocol slot retained as an alias during cutover.
    /// Runtime protocol gating reads <see cref="Protocols"/>, which falls back to this
    /// list when <c>protocols</c> is not explicitly set.
    /// </summary>
    [JsonPropertyName("enabledProtocols")]
    public IReadOnlyList<string>? EnabledProtocols { get; init; }

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
    public IReadOnlyDictionary<string, JsonElement> Options
    {
        get => _options ?? EmptyJsonMap;
        init => _options = value;
    }

    /// <summary>
    /// Optional service-level settings (record/feature/image limits, formats,
    /// timeouts, attachment caps). Consumed by every protocol cluster.
    /// </summary>
    [JsonPropertyName("settings")]
    public MetadataV2ServiceSettings? Settings { get; init; }

    /// <summary>
    /// Lifecycle and observed status.
    /// </summary>
    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    /// <summary>
    /// Extension data for the service.
    /// </summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions
    {
        get => _extensions ?? EmptyJsonMap;
        init => _extensions = value;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyJsonMap =
        new Dictionary<string, JsonElement>();
}

/// <summary>
/// Service-level operational settings shared across protocols. Slots are
/// nullable to allow protocol-specific defaults when unset.
/// </summary>
public sealed record MetadataV2ServiceSettings
{
    /// <summary>Maximum records per query (paging cap).</summary>
    [JsonPropertyName("maxRecordCount")]
    public int? MaxRecordCount { get; init; }

    /// <summary>Default records per query when the client does not request one.</summary>
    [JsonPropertyName("defaultRecordCount")]
    public int? DefaultRecordCount { get; init; }

    /// <summary>Maximum rendered image width in pixels.</summary>
    [JsonPropertyName("maxImageWidth")]
    public int? MaxImageWidth { get; init; }

    /// <summary>Maximum rendered image height in pixels.</summary>
    [JsonPropertyName("maxImageHeight")]
    public int? MaxImageHeight { get; init; }

    /// <summary>Default rendering DPI.</summary>
    [JsonPropertyName("defaultDpi")]
    public int? DefaultDpi { get; init; }

    /// <summary>Maximum features returned per layer per request.</summary>
    [JsonPropertyName("maxFeaturesPerLayer")]
    public int? MaxFeaturesPerLayer { get; init; }

    /// <summary>Default response format token (e.g. <c>json</c>, <c>geojson</c>).</summary>
    [JsonPropertyName("defaultFormat")]
    public string? DefaultFormat { get; init; }

    /// <summary>Response formats this service is allowed to emit.</summary>
    [JsonPropertyName("supportedFormats")]
    public IReadOnlyList<string> SupportedFormats { get; init; } = Array.Empty<string>();

    /// <summary>Default tile matrix set identifier for tile services.</summary>
    [JsonPropertyName("defaultTileMatrixSet")]
    public string? DefaultTileMatrixSet { get; init; }

    /// <summary>True when this service supports attachments.</summary>
    [JsonPropertyName("supportsAttachments")]
    public bool SupportsAttachments { get; init; }

    /// <summary>Maximum attachment size in bytes.</summary>
    [JsonPropertyName("maxAttachmentSizeBytes")]
    public long? MaxAttachmentSizeBytes { get; init; }

    /// <summary>Per-query timeout in milliseconds.</summary>
    [JsonPropertyName("queryTimeoutMs")]
    public int? QueryTimeoutMs { get; init; }

    /// <summary>Maximum edit operations per applyEdits transaction.</summary>
    [JsonPropertyName("maxEditsPerTransaction")]
    public int? MaxEditsPerTransaction { get; init; }

    /// <summary>Maximum request payload size in bytes.</summary>
    [JsonPropertyName("maxPayloadBytes")]
    public long? MaxPayloadBytes { get; init; }
}

/// <summary>
/// Resource-first publication link from a canonical resource to a service.
/// </summary>
public sealed record MetadataV2Publication
{
    private int? _layerIndex;
    private string? _path;
    private string? _serviceLocalId;

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
    public int? LayerIndex
    {
        get => _layerIndex ?? (Identifier.IsNumeric
            && int.TryParse(Identifier.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null);
        init => _layerIndex = value;
    }

    /// <summary>
    /// Computed full URL path override read from <see cref="Identifier"/>.
    /// Kept as a derived property so existing call sites that read
    /// <c>publication.Path</c> compile unchanged.
    /// </summary>
    [JsonIgnore]
    public string? Path
    {
        get => _path ?? Identifier.PathOverride;
        init => _path = value;
    }

    /// <summary>
    /// Computed service-local id read from <see cref="Identifier"/>'s value.
    /// Kept as a derived property so existing call sites that read
    /// <c>publication.ServiceLocalId</c> compile unchanged.
    /// </summary>
    [JsonIgnore]
    public string? ServiceLocalId
    {
        get => _serviceLocalId ?? (string.IsNullOrEmpty(Identifier.Value) ? null : Identifier.Value);
        init => _serviceLocalId = value;
    }

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

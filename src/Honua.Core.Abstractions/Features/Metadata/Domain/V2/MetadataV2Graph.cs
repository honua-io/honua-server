// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Security.Domain;

using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2Graph : ExtensibleJsonRecord
{
    private IReadOnlyList<MetadataV2Folder>? _folders;
    private IReadOnlyList<MetadataV2ServicePathAlias>? _servicePathAliases;

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = MetadataV2Constants.SchemaVersion;

    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = MetadataV2Constants.ApiVersion;

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UnixEpoch;

    [JsonPropertyName("namespaces")]
    public IReadOnlyList<string> Namespaces { get; init; } = Array.Empty<string>();

    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("catalogs")]
    public IReadOnlyList<MetadataV2Catalog> Catalogs { get; init; } = Array.Empty<MetadataV2Catalog>();

    [JsonPropertyName("resources")]
    public IReadOnlyList<MetadataV2Resource> Resources { get; init; } = Array.Empty<MetadataV2Resource>();

    [JsonPropertyName("connections")]
    public IReadOnlyList<MetadataV2Connection> Connections { get; init; } = Array.Empty<MetadataV2Connection>();

    [JsonPropertyName("storageBindings")]
    public IReadOnlyList<MetadataV2StorageBinding> StorageBindings { get; init; } = Array.Empty<MetadataV2StorageBinding>();

    [JsonPropertyName("folders")]
    public IReadOnlyList<MetadataV2Folder> Folders
    {
        get => _folders ?? [];
        init => _folders = value;
    }

    [JsonPropertyName("servicePathAliases")]
    public IReadOnlyList<MetadataV2ServicePathAlias> ServicePathAliases
    {
        get => _servicePathAliases ?? [];
        init => _servicePathAliases = value;
    }

    [JsonPropertyName("services")]
    public IReadOnlyList<MetadataV2Service> Services { get; init; } = Array.Empty<MetadataV2Service>();

    [JsonPropertyName("publications")]
    public IReadOnlyList<MetadataV2Publication> Publications { get; init; } = Array.Empty<MetadataV2Publication>();

    [JsonPropertyName("projectionProfiles")]
    public IReadOnlyList<MetadataV2ProjectionProfile> ProjectionProfiles { get; init; } =
        Array.Empty<MetadataV2ProjectionProfile>();

    [JsonPropertyName("policies")]
    public IReadOnlyList<MetadataV2Policy> Policies { get; init; } = Array.Empty<MetadataV2Policy>();

    [JsonPropertyName("roles")]
    public IReadOnlyList<MetadataV2Role> Roles { get; init; } = Array.Empty<MetadataV2Role>();

    [JsonPropertyName("runtime")]
    public MetadataV2RuntimeSnapshot Runtime { get; init; } = new();

    [JsonPropertyName("extensionPoints")]
    public IReadOnlyList<MetadataV2ExtensionPoint> ExtensionPoints { get; init; } = Array.Empty<MetadataV2ExtensionPoint>();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2Resource : ExtensibleJsonRecord
{
    private string? _primaryStorageBindingId;
    private IReadOnlyList<string>? _storageBindingIds;
    private IReadOnlyList<MetadataV2Field>? _schemaFields;
    private IReadOnlyList<string>? _policyIds;
    private IReadOnlyList<MetadataV2Relationship>? _relationships;
    private IReadOnlyList<string>? _styleResourceIds;
    private IReadOnlyList<MetadataV2AttributeRule>? _attributeRules;
    private IReadOnlyList<MetadataV2ContingentValueGroup>? _contingentValueGroups;
    private IReadOnlyDictionary<string, JsonElement>? _extensions;

    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonIgnore]
    public MetadataV2ResourceType Type
    {
        get => TypeValue.Value;
        init => TypeValue = value;
    }

    [JsonPropertyName("type")]
    [JsonConverter(typeof(PreservedEnumJsonConverter<MetadataV2ResourceType>))]
    public PreservedEnumValue<MetadataV2ResourceType> TypeValue { get; init; } = MetadataV2ResourceType.FeatureDataset;

    [JsonPropertyName("storageBindingIds")]
    public IReadOnlyList<string> StorageBindingIds
    {
        get => _storageBindingIds ?? Array.Empty<string>();
        init => _storageBindingIds = value;
    }

    [JsonPropertyName("primaryStorageBindingId")]
    public string? PrimaryStorageBindingId
    {
        get => _primaryStorageBindingId ?? (StorageBindingIds.Count == 0 ? null : StorageBindingIds[0]);
        init => _primaryStorageBindingId = value;
    }

    [JsonPropertyName("schemaFields")]
    public IReadOnlyList<MetadataV2Field> SchemaFields
    {
        get => _schemaFields ?? Array.Empty<MetadataV2Field>();
        init => _schemaFields = value;
    }

    [JsonPropertyName("policyIds")]
    public IReadOnlyList<string> PolicyIds
    {
        get => _policyIds ?? Array.Empty<string>();
        init => _policyIds = value;
    }

    [JsonPropertyName("relationships")]
    public IReadOnlyList<MetadataV2Relationship> Relationships
    {
        get => _relationships ?? Array.Empty<MetadataV2Relationship>();
        init => _relationships = value;
    }

    // Resource and service access policies combine deny-wins, and when both are unset authentication is required unless AllowAnonymous is set.
    [JsonPropertyName("accessPolicy")]
    public AccessPolicy? AccessPolicy { get; init; }

    [JsonPropertyName("spatial")]
    public MetadataV2ResourceSpatial? Spatial { get; init; }

    [JsonPropertyName("temporal")]
    public MetadataV2ResourceTemporal? Temporal { get; init; }

    [JsonPropertyName("permanentFilter")]
    public MetadataV2PermanentFilter? PermanentFilter { get; init; }

    [JsonPropertyName("subtypes")]
    public MetadataV2Subtypes? Subtypes { get; init; }

    [JsonPropertyName("attributeRules")]
    public IReadOnlyList<MetadataV2AttributeRule> AttributeRules
    {
        get => _attributeRules ?? Array.Empty<MetadataV2AttributeRule>();
        init => _attributeRules = value;
    }

    [JsonPropertyName("contingentValueGroups")]
    public IReadOnlyList<MetadataV2ContingentValueGroup> ContingentValueGroups
    {
        get => _contingentValueGroups ?? Array.Empty<MetadataV2ContingentValueGroup>();
        init => _contingentValueGroups = value;
    }

    [JsonPropertyName("ownerEditPolicy")]
    public MetadataV2OwnerEditPolicy? OwnerEditPolicy { get; init; }

    [JsonPropertyName("extrusion")]
    public MetadataV2ExtrusionInfo? Extrusion { get; init; }

    [JsonPropertyName("symbology3D")]
    public Symbology3D? Symbology3D { get; init; }

    // Each style reference must name a declared style resource, and the first entry is the primary style.
    [JsonPropertyName("styleResourceIds")]
    public IReadOnlyList<string> StyleResourceIds
    {
        get => _styleResourceIds ?? Array.Empty<string>();
        init => _styleResourceIds = value;
    }

    [JsonPropertyName("style")]
    public MetadataV2ResourceStyle? Style { get; init; }

    [JsonPropertyName("display")]
    public MetadataV2ResourceDisplay? Display { get; init; }

    [JsonPropertyName("editing")]
    public MetadataV2ResourceEditing? Editing { get; init; }

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions
    {
        get => _extensions ?? EmptyJsonMap;
        init => _extensions = value;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyJsonMap =
        new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2ResourceDisplay : ExtensibleJsonRecord
{
    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    [JsonPropertyName("defaultVisibility")]
    public bool DefaultVisibility { get; init; } = true;

    [JsonPropertyName("displayField")]
    public string? DisplayField { get; init; }

    [JsonPropertyName("queryable")]
    public bool Queryable { get; init; } = true;

    [JsonPropertyName("hasZ")]
    public bool HasZ { get; init; }

    [JsonPropertyName("hasM")]
    public bool HasM { get; init; }
}

public sealed record MetadataV2ResourceEditing : ExtensibleJsonRecord
{
    [JsonPropertyName("globalIdField")]
    public string? GlobalIdField { get; init; }

    [JsonPropertyName("creatorField")]
    public string? CreatorField { get; init; }

    [JsonPropertyName("createdAtField")]
    public string? CreatedAtField { get; init; }

    [JsonPropertyName("editorField")]
    public string? EditorField { get; init; }

    [JsonPropertyName("updatedAtField")]
    public string? UpdatedAtField { get; init; }

    [JsonPropertyName("canModify")]
    public bool CanModify { get; init; } = true;

    [JsonPropertyName("supportsAttachments")]
    public bool SupportsAttachments { get; init; }

    [JsonPropertyName("supportsRelatedRecords")]
    public bool SupportsRelatedRecords { get; init; } = true;
}

public sealed record MetadataV2Connection : ExtensibleJsonRecord
{
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonIgnore]
    public MetadataV2ConnectionType Type
    {
        get => TypeValue.Value;
        init => TypeValue = value;
    }

    [JsonPropertyName("type")]
    [JsonConverter(typeof(PreservedEnumJsonConverter<MetadataV2ConnectionType>))]
    public PreservedEnumValue<MetadataV2ConnectionType> TypeValue { get; init; } = MetadataV2ConnectionType.Managed;

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("endpoint")]
    public Uri? Endpoint { get; init; }

    [JsonPropertyName("secretRef")]
    public string? SecretRef { get; init; }

    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2StorageBinding : ExtensibleJsonRecord
{
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("resourceId")]
    public string ResourceId { get; init; } = string.Empty;

    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; init; }

    [JsonIgnore]
    public MetadataV2StorageType StorageType
    {
        get => StorageTypeValue.Value;
        init => StorageTypeValue = value;
    }

    [JsonPropertyName("storageType")]
    [JsonConverter(typeof(PreservedEnumJsonConverter<MetadataV2StorageType>))]
    public PreservedEnumValue<MetadataV2StorageType> StorageTypeValue { get; init; } = MetadataV2StorageType.RelationalTable;

    [JsonPropertyName("locator")]
    public string Locator { get; init; } = string.Empty;

    // This integer is the store's layer id, distinct from the service-local index, and feature, raster, and table resources require it.
    [JsonPropertyName("storageLayerId")]
    public int? StorageLayerId { get; init; }

    [JsonIgnore]
    public IReadOnlyList<MetadataV2StorageBindingCapability> Capabilities
    {
        get => CapabilityValues.Select(value => value.Value).ToArray();
        init => CapabilityValues = value.Select(capability => new PreservedEnumValue<MetadataV2StorageBindingCapability>(capability)).ToArray();
    }

    [JsonPropertyName("capabilities")]
    [JsonConverter(typeof(PreservedEnumListJsonConverter<MetadataV2StorageBindingCapability>))]
    public IReadOnlyList<PreservedEnumValue<MetadataV2StorageBindingCapability>> CapabilityValues { get; init; } = [];

    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();

    public bool Supports(MetadataV2StorageBindingCapability capability)
    {
        return CapabilityValues.Any(value => value.Value == capability);
    }
}

public sealed record MetadataV2Service : ExtensibleJsonRecord
{
    [JsonPropertyName("folderId")]
    public string? FolderId { get; init; }

    [JsonPropertyName("publicName")]
    public string? PublicName { get; init; }

    [JsonIgnore]
    public string EffectivePublicName => PublicName ?? Metadata.Name;

    private IReadOnlyDictionary<string, JsonElement>? _options;
    private IReadOnlyDictionary<string, JsonElement>? _extensions;

    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    private IReadOnlyList<string>? _protocols;

    [JsonIgnore]
    public MetadataV2ServiceType ServiceType
    {
        get => ServiceTypeValue.Value;
        init => ServiceTypeValue = value;
    }

    [JsonPropertyName("serviceType")]
    [JsonConverter(typeof(PreservedEnumJsonConverter<MetadataV2ServiceType>))]
    public PreservedEnumValue<MetadataV2ServiceType> ServiceTypeValue { get; init; } = MetadataV2ServiceType.OgcApiFeatures;

    [JsonPropertyName("route")]
    public string? Route { get; init; }

    [JsonPropertyName("publicationIds")]
    public IReadOnlyList<string> PublicationIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("accessPolicy")]
    public AccessPolicy? AccessPolicy { get; init; }

    // The service output CRS is independent of the resource CRS because a service can reproject on the fly.
    [JsonPropertyName("spatialReference")]
    public MetadataV2SpatialReference? SpatialReference { get; init; }

    [JsonPropertyName("protocols")]
    public IReadOnlyList<string> Protocols
    {
        get => _protocols ?? EnabledProtocols ?? Array.Empty<string>();
        init => _protocols = value;
    }

    [JsonPropertyName("enabledProtocols")]
    public IReadOnlyList<string>? EnabledProtocols { get; init; }

    [JsonIgnore]
    public string? PrimaryProtocol => Protocols.Count == 0 ? null : Protocols[0];

    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options
    {
        get => _options ?? EmptyJsonMap;
        init => _options = value;
    }

    [JsonPropertyName("settings")]
    public MetadataV2ServiceSettings? Settings { get; init; }

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions
    {
        get => _extensions ?? EmptyJsonMap;
        init => _extensions = value;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyJsonMap =
        new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2ServiceSettings : ExtensibleJsonRecord
{
    [JsonPropertyName("maxRecordCount")]
    public int? MaxRecordCount { get; init; }

    [JsonPropertyName("defaultRecordCount")]
    public int? DefaultRecordCount { get; init; }

    [JsonPropertyName("maxImageWidth")]
    public int? MaxImageWidth { get; init; }

    [JsonPropertyName("maxImageHeight")]
    public int? MaxImageHeight { get; init; }

    [JsonPropertyName("defaultDpi")]
    public int? DefaultDpi { get; init; }

    [JsonPropertyName("maxFeaturesPerLayer")]
    public int? MaxFeaturesPerLayer { get; init; }

    [JsonPropertyName("defaultFormat")]
    public string? DefaultFormat { get; init; }

    [JsonPropertyName("supportedFormats")]
    public IReadOnlyList<string> SupportedFormats { get; init; } = Array.Empty<string>();

    [JsonPropertyName("defaultTileMatrixSet")]
    public string? DefaultTileMatrixSet { get; init; }

    [JsonPropertyName("supportsAttachments")]
    public bool SupportsAttachments { get; init; }

    [JsonPropertyName("maxAttachmentSizeBytes")]
    public long? MaxAttachmentSizeBytes { get; init; }

    [JsonPropertyName("queryTimeoutMs")]
    public int? QueryTimeoutMs { get; init; }

    [JsonPropertyName("maxEditsPerTransaction")]
    public int? MaxEditsPerTransaction { get; init; }

    [JsonPropertyName("maxPayloadBytes")]
    public long? MaxPayloadBytes { get; init; }
}

public sealed record MetadataV2Publication : ExtensibleJsonRecord
{
    [JsonPropertyName("advertised")]
    public bool Advertised { get; init; } = true;

    private int? _layerIndex;
    private string? _path;
    private string? _serviceLocalId;

    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("resourceId")]
    public string ResourceId { get; init; } = string.Empty;

    [JsonPropertyName("serviceId")]
    public string ServiceId { get; init; } = string.Empty;

    [JsonPropertyName("storageBindingId")]
    public string? StorageBindingId { get; init; }

    [JsonIgnore]
    public MetadataV2PublicationType PublicationType
    {
        get => PublicationTypeValue.Value;
        init => PublicationTypeValue = value;
    }

    [JsonPropertyName("publicationType")]
    [JsonConverter(typeof(PreservedEnumJsonConverter<MetadataV2PublicationType>))]
    public PreservedEnumValue<MetadataV2PublicationType> PublicationTypeValue { get; init; } = MetadataV2PublicationType.OgcCollection;

    [JsonPropertyName("titleOverride")]
    public string? TitleOverride { get; init; }

    // One protocol-facing identifier replaces the separate layer index, service-local id, and path so the three cannot diverge.
    [JsonPropertyName("identifier")]
    public MetadataV2PublicationIdentifier Identifier { get; init; } = new();

    [JsonPropertyName("layerIndex")]
    public int? LayerIndex
    {
        get => _layerIndex ?? (Identifier?.IsNumeric == true
            && int.TryParse(Identifier.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null);
        init => _layerIndex = value;
    }

    [JsonPropertyName("path")]
    public string? Path
    {
        get => _path ?? Identifier?.PathOverride;
        init => _path = value;
    }

    [JsonPropertyName("serviceLocalId")]
    public string? ServiceLocalId
    {
        get => _serviceLocalId ?? (string.IsNullOrEmpty(Identifier?.Value) ? null : Identifier.Value);
        init => _serviceLocalId = value;
    }

    // At most one publication of a resource on a service is primary, and resolvers use it when the resource is published more than once.
    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; init; }

    [JsonPropertyName("supportedFormats")]
    public IReadOnlyList<string> SupportedFormats { get; init; } = Array.Empty<string>();

    [JsonPropertyName("fieldAliases")]
    public IReadOnlyDictionary<string, string> FieldAliases { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2Catalog : ExtensibleJsonRecord
{
    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetadataV2CatalogConfiguration? Configuration { get; init; }

    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2ProjectionProfile : ExtensibleJsonRecord
{
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    [JsonPropertyName("requiredSemantics")]
    public IReadOnlyList<string> RequiredSemantics { get; init; } = Array.Empty<string>();

    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2Policy : ExtensibleJsonRecord
{
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("engine")]
    public string Engine { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string Effect { get; init; } = string.Empty;

    [JsonPropertyName("rules")]
    public IReadOnlyDictionary<string, JsonElement> Rules { get; init; } = new Dictionary<string, JsonElement>();

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2Role : ExtensibleJsonRecord
{
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    [JsonPropertyName("policyIds")]
    public IReadOnlyList<string> PolicyIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record MetadataV2RuntimeSnapshot : ExtensibleJsonRecord
{
    [JsonPropertyName("metadata")]
    public MetadataV2ObjectMetadata Metadata { get; init; } = new();

    [JsonPropertyName("cacheKey")]
    public string? CacheKey { get; init; }

    [JsonPropertyName("sourceRevision")]
    public long? SourceRevision { get; init; }

    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("status")]
    public MetadataV2Status Status { get; init; } = new();

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Protocols.GeoServices.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed class LayerResponse
{
    // No ArcGIS Server version (currentVersion/fullVersion) is advertised. Honua is an
    // independent GeoServices REST server and must not impersonate a specific ArcGIS Server
    // release. Do NOT add a currentVersion/fullVersion field (guarded by NoGeoServicesServerVersionTests).

    public required int Id { get; init; }

    public required string Name { get; init; }

    public string Type { get; init; } = "Feature Layer";

    public string? Description { get; init; }

    public string? CopyrightText { get; init; }

    public string? License { get; init; }

    public string? Publisher { get; init; }

    public GeoServicesGovernanceLink[]? Links { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required string? GeometryType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required SpatialReferenceInfo? SpatialReference { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasZ")]
    public bool HasZ { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyName("hasM")]
    public bool HasM { get; init; }

    public required GeoServicesFieldInfo[] Fields { get; init; }

    public ExtentInfo? Extent { get; init; }

    public FeatureServerTimeInfo? TimeInfo { get; init; }

    public FeatureServerExtrusionInfo? ExtrusionInfo { get; init; }

    public double? MinScale { get; init; }

    public double? MaxScale { get; init; }

    public bool DefaultVisibility { get; init; } = true;

    public string Capabilities { get; init; } = "Query,Extract";

    public int MaxRecordCount { get; init; } = 1000;

    public bool SupportsAdvancedQueries { get; init; } = true;

    public bool SupportsStatistics { get; init; } = true;

    public bool SupportsCountDistinct { get; init; } = true;

    public bool SupportsOrderBy { get; init; } = true;

    public bool SupportsDistinct { get; init; } = true;

    public bool SupportsPagination { get; init; } = true;

    public bool SupportsTrueCurve { get; init; }

    public required string ObjectIdField { get; init; }

    public string? GlobalIdField { get; init; }

    public string? DisplayField { get; init; }

    public UniqueIdFieldInfo? UniqueIdField { get; init; }

    public string? TypeIdField { get; init; }

    public GeoServicesLayerType[]? Types { get; init; }

    public LayerRelationshipInfo[] Relationships { get; init; } = [];

    public bool IsDataVersioned { get; init; }

    public bool IsDataBranchVersioned { get; init; }

    public bool? SupportsRollbackOnFailureParameter { get; init; }

    public object? ArchivingInfo { get; init; }

    public bool SupportsApplyEditsWithGlobalIds { get; init; }

    public object? DrawingInfo { get; init; }

    public bool HasAttachments { get; init; }

    // The layer root emits supportsQueryAttachments as true when the layer has attachments, because the ArcGIS Maps SDK for JavaScript refuses the operation when the root flag is absent or false.
    public bool SupportsQueryAttachments { get; init; }

    public bool SupportsAttachmentKeywords { get; init; }

    public bool SupportsAttachmentsByUploadId { get; init; }

    public object? PopupInfo { get; init; }

    public bool SupportsQueryRelated { get; init; }

    // Supported query formats are a comma-delimited string because the ArcGIS Maps SDK for JavaScript calls split on the value.
    [System.Text.Json.Serialization.JsonConverter(typeof(CommaDelimitedStringArrayConverter))]
    public string[] SupportedQueryFormats { get; init; } = ["JSON", "GeoJSON"];

    public object? OwnershipBasedAccessControlForFeatures { get; init; }

    public bool UseStandardizedQueries { get; init; } = true;

    public bool SupportsSqlFormat { get; init; } = true;

    // Supported SQL formats are a comma-delimited string because clients split the value and the GeoServices contract defines it that way.
    [System.Text.Json.Serialization.JsonConverter(typeof(CommaDelimitedStringArrayConverter))]
    public string[] SupportedSqlFormats { get; init; } = ["none", "standard", "native"];

    // supportsReturningQueryExtent is also emitted on the layer root because the ArcGIS Maps SDK for JavaScript reads it from the layer object, not only from the nested capabilities.
    public bool SupportsReturningQueryExtent { get; init; }

    public bool SupportsCoordinatesQuantization { get; init; } = true;

    public bool AllowGeometryUpdates { get; init; } = true;

    public EditFieldsInfo? EditFieldsInfo { get; init; }

    public EditingInfo? EditingInfo { get; init; }

    public FeatureTemplate[] Templates { get; init; } = [];

    public AdvancedQueryCapabilities? AdvancedQueryCapabilities { get; init; }

    public string? SubtypeField { get; init; }

    public System.Text.Json.JsonElement? DefaultSubtypeCode { get; init; }

    public GeoServicesSubtypeInfo[]? Subtypes { get; init; }

    [JsonPropertyName("contingentValuesDefinition")]
    public ContingentValuesDefinition? ContingentValuesDefinition { get; init; }
}

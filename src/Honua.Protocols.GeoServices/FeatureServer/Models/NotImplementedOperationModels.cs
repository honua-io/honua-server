// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Response model for the service-level <c>queryContingentValues</c> operation.
/// Honua models contingent attribute values through the Metadata v2 graph
/// (<c>contingentValueGroups</c> on each resource); the response carries one
/// <see cref="ContingentValuesDefinition"/> per layer that declares contingent values
/// and an empty collection for services with none, so SDK clients parse it without
/// error (#1878).
/// </summary>
public sealed class QueryContingentValuesResponse
{
    /// <summary>
    /// Ordered value-type identifiers used by compact contingent value rows
    /// (<c>Unknown</c>, <c>Any</c>, <c>Null</c>, <c>Code</c>, <c>Range</c>).
    /// </summary>
    [JsonPropertyName("typeCodes")]
    public string[] TypeCodes { get; set; } =
    [
        "Unknown",
        "Any",
        "Null",
        "Code",
        "Range"
    ];

    /// <summary>
    /// Domain string dictionaries referenced by contingent value rows. Empty; Honua emits
    /// coded/range values inline rather than via a shared string-dictionary table.
    /// </summary>
    [JsonPropertyName("stringDicts")]
    public object[] StringDicts { get; set; } = [];

    /// <summary>
    /// Per-layer contingent value definitions. One entry per layer that declares one or more
    /// contingent value groups; empty when the service declares none.
    /// </summary>
    [JsonPropertyName("contingentValuesDefinitions")]
    public ContingentValuesDefinition[] ContingentValuesDefinitions { get; set; } = [];
}

/// <summary>
/// Per-layer contingent value definition: the field groups and their enumerated allowed
/// value combinations for one FeatureServer layer.
/// </summary>
public sealed class ContingentValuesDefinition
{
    /// <summary>Layer id the contingent values apply to.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Field groups (cross-field constraints) declared for the layer.</summary>
    [JsonPropertyName("fieldGroups")]
    public ContingentValueFieldGroup[] FieldGroups { get; set; } = [];
}

/// <summary>
/// A field group within a <see cref="ContingentValuesDefinition"/>: the constrained fields
/// and the enumerated allowed value combinations across them.
/// </summary>
public sealed class ContingentValueFieldGroup
{
    /// <summary>Field group name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// When <see langword="true"/> only the enumerated <see cref="ContingentValues"/>
    /// combinations are valid; when <see langword="false"/> the group is advisory.
    /// </summary>
    [JsonPropertyName("restrictive")]
    public bool Restrictive { get; set; } = true;

    /// <summary>Ordered names of the fields the group constrains.</summary>
    [JsonPropertyName("fields")]
    public string[] Fields { get; set; } = [];

    /// <summary>Enumerated allowed value combinations across the group's fields.</summary>
    [JsonPropertyName("contingentValues")]
    public ContingentValueRow[] ContingentValues { get; set; } = [];
}

/// <summary>
/// A single allowed value combination within a <see cref="ContingentValueFieldGroup"/>.
/// </summary>
public sealed class ContingentValueRow
{
    /// <summary>Stable id of the combination within its group.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Subtype code the combination applies to, or <c>null</c> when it applies to every subtype.
    /// </summary>
    [JsonPropertyName("subtypeCode")]
    public System.Text.Json.JsonElement? SubtypeCode { get; set; }

    /// <summary>Allowed value for each constrained field, keyed by field name.</summary>
    [JsonPropertyName("values")]
    public Dictionary<string, ContingentFieldValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The value a single field may take within a <see cref="ContingentValueRow"/> — a discrete
/// coded value, a numeric range, any value, or null.
/// </summary>
public sealed class ContingentFieldValue
{
    /// <summary>Value kind: <c>code</c>, <c>range</c>, <c>any</c>, or <c>null</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "any";

    /// <summary>Discrete coded value when <see cref="Type"/> is <c>code</c>.</summary>
    [JsonPropertyName("code")]
    public System.Text.Json.JsonElement? Code { get; set; }

    /// <summary>Two-element [min, max] range when <see cref="Type"/> is <c>range</c>.</summary>
    [JsonPropertyName("range")]
    public System.Text.Json.JsonElement[]? Range { get; set; }
}

/// <summary>
/// Response model for the service-level <c>sharedTemplates</c> resource and its
/// <c>query</c> child operation. Honua does not persist shared editing templates;
/// the response is shaped per the Esri specification with an empty template list.
/// </summary>
public sealed class SharedTemplatesResponse
{
    /// <summary>
    /// Shared editing templates configured for the service. Empty until shared
    /// template storage is implemented.
    /// </summary>
    [JsonPropertyName("sharedTemplates")]
    public object[] SharedTemplates { get; set; } = [];
}

/// <summary>
/// Response model for the service-level <c>htmlPopup</c> resource. Honua does not
/// serve author-defined HTML pop-ups, so the type is always
/// <c>esriServerHTMLPopupTypeNone</c> per the Esri specification.
/// </summary>
public sealed class HtmlPopupResponse
{
    /// <summary>
    /// HTML pop-up type. Always <c>esriServerHTMLPopupTypeNone</c> until authored
    /// pop-up content is supported.
    /// </summary>
    [JsonPropertyName("htmlPopupType")]
    public string HtmlPopupType { get; set; } = "esriServerHTMLPopupTypeNone";
}

/// <summary>
/// Response model for the layer-level <c>hasAssets</c> operation. Honua does not yet
/// store layer assets; the flag is always <see langword="false"/>.
/// </summary>
public sealed class HasAssetsResponse
{
    /// <summary>
    /// Indicates whether the layer has any stored assets. Always
    /// <see langword="false"/> until asset storage is implemented.
    /// </summary>
    [JsonPropertyName("hasAssets")]
    public bool HasAssets { get; set; }
}

/// <summary>
/// Response model for the layer-level <c>queryAssets</c> operation. Honua does not
/// yet store layer assets; the asset collection is always empty.
/// </summary>
public sealed class QueryAssetsResponse
{
    /// <summary>
    /// Assets matching the query. Empty until asset storage is implemented.
    /// </summary>
    [JsonPropertyName("assets")]
    public object[] Assets { get; set; } = [];

    /// <summary>
    /// Indicates whether more assets exist than were returned. Always
    /// <see langword="false"/> until asset storage is implemented.
    /// </summary>
    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; set; }
}

/// <summary>
/// Response model for the layer-level <c>cleanupAssets</c> operation. Honua does not
/// yet store layer assets, so no assets are ever removed.
/// </summary>
public sealed class CleanupAssetsResponse
{
    /// <summary>
    /// Indicates whether the cleanup completed. Always <see langword="true"/> as a
    /// no-op until asset storage is implemented.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    /// <summary>
    /// Number of orphaned assets removed. Always zero until asset storage is
    /// implemented.
    /// </summary>
    [JsonPropertyName("cleanedAssetCount")]
    public int CleanedAssetCount { get; set; }
}

/// <summary>
/// Response model for the layer-level <c>metadata/update</c> operation acknowledgement.
/// </summary>
public sealed class UpdateMetadataResponse
{
    /// <summary>
    /// Indicates whether the metadata update was accepted.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

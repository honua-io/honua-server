// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Admin.Models;

// ---- Subtypes (MetadataV2Subtypes) -----------------------------------------------------------------------

/// <summary>
/// Per-field override (default value and/or value domain) for a single subtype, keyed by
/// field name in the parent <see cref="SubtypePayload.FieldOverrides"/> map. The
/// <c>defaultValue</c> and <c>domain</c> are carried through verbatim as JSON so the full
/// Esri domain shape round-trips without a hand-maintained mirror DTO.
/// </summary>
public sealed class SubtypeFieldOverridePayload
{
    /// <summary>Default value for the field on rows of this subtype. Null when no default.</summary>
    public JsonElement? DefaultValue { get; init; }

    /// <summary>Per-subtype value domain for the field (carried verbatim). Null when no override.</summary>
    public JsonElement? Domain { get; init; }
}

/// <summary>One subtype definition: integer code, display name, and per-field overrides.</summary>
public sealed class SubtypePayload
{
    /// <summary>Integer subtype code (JSON-typed to match the subtype field's value type).</summary>
    public JsonElement Code { get; init; }

    /// <summary>Human-readable display name for the subtype.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Per-field overrides keyed by field name. Null/empty when the subtype has none.</summary>
    public IReadOnlyDictionary<string, SubtypeFieldOverridePayload>? FieldOverrides { get; init; }
}

/// <summary>
/// Request payload for updating a layer's Esri-style subtype set
/// (<c>MetadataV2Subtypes</c>). Send <c>subtypeField = null</c> (or an empty value with
/// <c>clear = true</c>) to remove the subtype set entirely.
/// </summary>
public sealed class LayerSubtypesUpdateRequest
{
    /// <summary>When true, clears the entire subtype set regardless of the other fields.</summary>
    public bool Clear { get; init; }

    /// <summary>Name of the integer field selecting the subtype. Must reference a declared schema field.</summary>
    public string? SubtypeField { get; init; }

    /// <summary>Subtype code applied to new rows when none is supplied. Null when no default.</summary>
    public JsonElement? DefaultSubtypeCode { get; init; }

    /// <summary>The subtype definitions. Null leaves the existing list unchanged; empty array clears it.</summary>
    public IReadOnlyList<SubtypePayload>? Subtypes { get; init; }
}

/// <summary>Response payload echoing a layer's persisted subtype set (null when none).</summary>
public sealed class LayerSubtypesResponse
{
    public int LayerId { get; init; }

    public string? SubtypeField { get; init; }

    public JsonElement? DefaultSubtypeCode { get; init; }

    public IReadOnlyList<SubtypePayload> Subtypes { get; init; } = Array.Empty<SubtypePayload>();
}

// ---- Attribute rules (MetadataV2AttributeRule[]) ---------------------------------------------------------

/// <summary>One Esri-style attribute rule (calculation / constraint / validation).</summary>
public sealed class AttributeRulePayload
{
    /// <summary>Stable rule name (unique within the resource).</summary>
    public required string Name { get; init; }

    /// <summary>Rule kind: <c>calculation</c>, <c>constraint</c>, or <c>validation</c>.</summary>
    public string Type { get; init; } = "calculation";

    /// <summary>Target field a calculation rule writes into. References a declared schema field.</summary>
    public string? FieldName { get; init; }

    /// <summary>Arcade expression text.</summary>
    public string ScriptExpression { get; init; } = string.Empty;

    /// <summary>Edit events that trigger the rule: <c>insert</c>, <c>update</c>, <c>delete</c>.</summary>
    public IReadOnlyList<string>? TriggeringEvents { get; init; }

    /// <summary>Operator-facing message surfaced on a violation.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Whether the rule is active. Defaults to true.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Request payload that fully replaces a layer's attribute-rule set
/// (<c>MetadataV2AttributeRule[]</c>). Send an empty array to clear all rules.
/// </summary>
public sealed class LayerAttributeRulesUpdateRequest
{
    /// <summary>The complete attribute-rule set. Required (may be empty to clear).</summary>
    public IReadOnlyList<AttributeRulePayload> Rules { get; init; } = Array.Empty<AttributeRulePayload>();
}

/// <summary>Response payload echoing a layer's persisted attribute-rule set.</summary>
public sealed class LayerAttributeRulesResponse
{
    public int LayerId { get; init; }

    public IReadOnlyList<AttributeRulePayload> Rules { get; init; } = Array.Empty<AttributeRulePayload>();
}

// ---- 3D extrusion & symbology (MetadataV2ExtrusionInfo + Symbology3D) -------------------------------------

/// <summary>Extrusion configuration payload (<c>MetadataV2ExtrusionInfo</c>).</summary>
public sealed class ExtrusionInfoPayload
{
    /// <summary>Numeric field driving extrusion height. References a declared schema field.</summary>
    public required string HeightField { get; init; }

    /// <summary>Optional field for base elevation. References a declared schema field when set.</summary>
    public string? BaseHeightField { get; init; }

    /// <summary>Vertical unit (<c>meters</c>, <c>feet</c>, <c>usSurveyFeet</c>). Null defaults to meters.</summary>
    public string? Unit { get; init; }

    /// <summary>Fallback height when the height field value is null. Must be &gt;= 0.</summary>
    public double? DefaultHeight { get; init; }

    /// <summary>Optional material/style hint passed through to 3D Tiles generation.</summary>
    public string? MaterialHint { get; init; }
}

/// <summary>sRGB color with 8-bit channels for 3D symbology.</summary>
public sealed class Symbology3DColorPayload
{
    public byte Red { get; init; }

    public byte Green { get; init; }

    public byte Blue { get; init; }
}

/// <summary>One attribute-driven 3D symbology rule.</summary>
public sealed class Symbology3DRulePayload
{
    /// <summary>Source attribute (field) name the condition is evaluated against.</summary>
    public required string Attribute { get; init; }

    /// <summary>Comparison operator: equals, notEquals, greaterThan, greaterThanOrEqual, lessThan, lessThanOrEqual.</summary>
    public string Comparison { get; init; } = "equals";

    /// <summary>Right-hand comparison operand.</summary>
    public string? Value { get; init; }

    /// <summary>Color applied when the rule matches. Null leaves the color unchanged.</summary>
    public Symbology3DColorPayload? Color { get; init; }

    /// <summary>Opacity in [0, 1] applied when the rule matches. Null inherits the default.</summary>
    public double? Opacity { get; init; }

    /// <summary>Visibility applied when the rule matches. Null means visible.</summary>
    public bool? Visible { get; init; }
}

/// <summary>Attribute-driven 3D symbology payload (<c>Symbology3D</c>).</summary>
public sealed class Symbology3DPayload
{
    /// <summary>Default base color for features matching no rule. Null falls back to opaque white.</summary>
    public Symbology3DColorPayload? DefaultColor { get; init; }

    /// <summary>Default opacity in [0, 1] for features matching no rule. Null is fully opaque.</summary>
    public double? DefaultOpacity { get; init; }

    /// <summary>Ordered attribute-driven rules; the first matching rule wins.</summary>
    public IReadOnlyList<Symbology3DRulePayload> Rules { get; init; } = Array.Empty<Symbology3DRulePayload>();
}

/// <summary>
/// Request payload for updating a layer's 3D extrusion config and attribute-driven 3D
/// symbology. A null section leaves the corresponding stored value unchanged; set the
/// matching clear flag to remove a section.
/// </summary>
public sealed class LayerExtrusionUpdateRequest
{
    /// <summary>Extrusion config. Null leaves unchanged.</summary>
    public ExtrusionInfoPayload? Extrusion { get; init; }

    /// <summary>When true, removes the extrusion config.</summary>
    public bool ClearExtrusion { get; init; }

    /// <summary>3D symbology. Null leaves unchanged.</summary>
    public Symbology3DPayload? Symbology3D { get; init; }

    /// <summary>When true, removes the 3D symbology.</summary>
    public bool ClearSymbology3D { get; init; }
}

/// <summary>Response payload echoing a layer's persisted extrusion and 3D symbology.</summary>
public sealed class LayerExtrusionResponse
{
    public int LayerId { get; init; }

    public ExtrusionInfoPayload? Extrusion { get; init; }

    public Symbology3DPayload? Symbology3D { get; init; }
}

// ---- Publication overrides (MetadataV2Publication) -------------------------------------------------------

/// <summary>
/// Request payload for updating a publication's presentation overrides. Null fields leave
/// the corresponding stored value unchanged; an empty array/map clears a list/map field;
/// an empty <c>titleOverride</c> string clears the title override.
/// </summary>
public sealed class PublicationOverridesUpdateRequest
{
    /// <summary>Title override for this publication. Null leaves unchanged; empty string clears.</summary>
    public string? TitleOverride { get; init; }

    /// <summary>Service-specific field aliases (field name to alias). Null leaves unchanged; empty map clears.</summary>
    public IReadOnlyDictionary<string, string>? FieldAliases { get; init; }

    /// <summary>Publication capabilities. Null leaves unchanged; empty array clears.</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Supported format identifiers. Null leaves unchanged; empty array clears.</summary>
    public IReadOnlyList<string>? SupportedFormats { get; init; }

    /// <summary>Whether this publication is the primary publication of its resource on its service. Null leaves unchanged.</summary>
    public bool? IsPrimary { get; init; }
}

/// <summary>Response payload echoing a publication's persisted presentation overrides.</summary>
public sealed class PublicationOverridesResponse
{
    /// <summary>Publication identity id (<c>MetadataV2Publication.Metadata.Id</c>).</summary>
    public string PublicationId { get; init; } = string.Empty;

    public string ResourceId { get; init; } = string.Empty;

    public string ServiceId { get; init; } = string.Empty;

    public string? TitleOverride { get; init; }

    public IReadOnlyDictionary<string, string> FieldAliases { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SupportedFormats { get; init; } = Array.Empty<string>();

    public bool IsPrimary { get; init; }
}

// ---- Lifecycle status (MetadataV2Status) -----------------------------------------------------------------

/// <summary>
/// Request payload for setting a layer resource's lifecycle status
/// (<c>MetadataV2Status</c>). At least one of the fields must be supplied; null fields
/// leave the corresponding stored value unchanged.
/// </summary>
public sealed class LayerStatusUpdateRequest
{
    /// <summary>Desired lifecycle state: <c>draft</c>, <c>active</c>, <c>deprecated</c>, <c>retired</c>, <c>archived</c>. Null leaves unchanged.</summary>
    public string? Lifecycle { get; init; }

    /// <summary>Observed operational state: <c>unknown</c>, <c>ready</c>, <c>pending</c>, <c>degraded</c>, <c>failed</c>. Null leaves unchanged.</summary>
    public string? State { get; init; }
}

/// <summary>Response payload echoing a layer resource's persisted lifecycle status.</summary>
public sealed class LayerStatusResponse
{
    public int LayerId { get; init; }

    public string Lifecycle { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public DateTimeOffset? ObservedAt { get; init; }
}

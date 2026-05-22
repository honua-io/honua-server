// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Unified, protocol-facing identifier for a <see cref="MetadataV2Publication"/>.
/// Collapses the previously separate <c>LayerIndex</c>, <c>ServiceLocalId</c>,
/// and <c>Path</c> slots into one typed record so the three cannot desync.
/// </summary>
/// <remarks>
/// Encoding conventions:
/// <list type="bullet">
///   <item>For GeoServices-style numeric routing (FeatureServer/MapServer layer id),
///         set <see cref="Value"/> to the stringified integer (e.g. <c>"0"</c>) and
///         <see cref="IsNumeric"/> to <c>true</c>. Callers that need the <c>int</c>
///         read <see cref="MetadataV2Publication.LayerIndex"/>.</item>
///   <item>For name-based routing (OGC API Features collection id, STAC collection id),
///         set <see cref="Value"/> to the collection id and leave <see cref="IsNumeric"/>
///         <c>false</c>.</item>
///   <item>Set <see cref="PathOverride"/> only when the publication is exposed at a
///         non-default URL path that differs from the convention derived from
///         <see cref="Value"/>.</item>
/// </list>
/// </remarks>
public sealed record MetadataV2PublicationIdentifier
{
    /// <summary>Protocol-facing identifier (collection id, layer name, or stringified layer index).</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    /// <summary>True when <see cref="Value"/> is a stringified non-negative integer (GeoServices-style numeric routing).</summary>
    [JsonPropertyName("isNumeric")]
    public bool IsNumeric { get; init; }

    /// <summary>Optional full URL path override (only set when the publication is at a non-default URL path).</summary>
    [JsonPropertyName("pathOverride")]
    public string? PathOverride { get; init; }
}

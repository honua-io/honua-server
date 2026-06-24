// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// A single Esri-style layer <c>type</c> entry served on the FeatureServer layer
/// metadata <c>types</c> array. Esri derives a layer's editing <c>types</c> from its
/// subtypes: each type carries the subtype code as its <see cref="Id"/>, the subtype
/// label as its <see cref="Name"/>, the per-subtype field value <see cref="Domains"/>,
/// and one or more editing <see cref="Templates"/> whose prototype attributes pre-seed
/// the subtype's field default values. Honua surfaces this shape directly from the
/// canonical Metadata v2 subtype set (no separate type/template authoring exists),
/// matching how ArcGIS authors layer types from subtypes (#1878).
/// </summary>
public sealed class GeoServicesLayerType
{
    /// <summary>
    /// Type identifier. Mirrors the subtype code so Esri clients can correlate the
    /// type with the layer's <c>subtypes</c> array and <c>typeIdField</c>.
    /// </summary>
    [JsonPropertyName("id")]
    public required JsonElement Id { get; init; }

    /// <summary>
    /// Human-readable type label (the subtype name).
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Per-field value domains that apply to features of this type, keyed by field name.
    /// Absent when the subtype declares no field domain overrides.
    /// </summary>
    [JsonPropertyName("domains")]
    public IReadOnlyDictionary<string, GeoServicesFieldDomainInfo>? Domains { get; init; }

    /// <summary>
    /// Editing templates for creating features of this type. Each template's prototype
    /// pre-seeds the subtype's field default values plus the subtype code.
    /// </summary>
    [JsonPropertyName("templates")]
    public FeatureTemplate[] Templates { get; init; } = [];
}

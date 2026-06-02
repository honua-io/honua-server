// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// A single Esri subtype entry served on the FeatureServer layer metadata
/// <c>subtypes</c> array. Carries the subtype code, label, and per-field
/// default values and value domains that apply to rows of this subtype.
/// </summary>
public sealed class GeoServicesSubtypeInfo
{
    /// <summary>
    /// Integer subtype code identifying the subtype.
    /// </summary>
    [JsonPropertyName("code")]
    public required JsonElement Code { get; init; }

    /// <summary>
    /// Human-readable subtype label.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Per-field default values for rows of this subtype, keyed by field name.
    /// Absent when the subtype declares no field defaults.
    /// </summary>
    [JsonPropertyName("defaultValues")]
    public IReadOnlyDictionary<string, JsonElement>? DefaultValues { get; init; }

    /// <summary>
    /// Per-field value domains for rows of this subtype, keyed by field name.
    /// Absent when the subtype declares no field domain overrides.
    /// </summary>
    [JsonPropertyName("domains")]
    public IReadOnlyDictionary<string, GeoServicesFieldDomainInfo>? Domains { get; init; }
}

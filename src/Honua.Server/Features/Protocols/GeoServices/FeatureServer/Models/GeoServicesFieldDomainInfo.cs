// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// GeoServices field domain metadata for layer and query field descriptions.
/// </summary>
public sealed class GeoServicesFieldDomainInfo
{
    /// <summary>
    /// Domain type, such as <c>codedValue</c> or <c>range</c>.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Stable domain name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Coded values for coded-value domains.
    /// </summary>
    [JsonPropertyName("codedValues")]
    public GeoServicesFieldDomainCodedValueInfo[]? CodedValues { get; init; }

    /// <summary>
    /// Range values for range domains.
    /// </summary>
    [JsonPropertyName("range")]
    public JsonElement[]? Range { get; init; }

    /// <summary>
    /// Optional merge policy metadata.
    /// </summary>
    [JsonPropertyName("mergePolicy")]
    public string? MergePolicy { get; init; }

    /// <summary>
    /// Optional split policy metadata.
    /// </summary>
    [JsonPropertyName("splitPolicy")]
    public string? SplitPolicy { get; init; }
}

/// <summary>
/// GeoServices coded-value domain entry.
/// </summary>
public sealed class GeoServicesFieldDomainCodedValueInfo
{
    /// <summary>
    /// Stored coded value.
    /// </summary>
    [JsonPropertyName("code")]
    public required JsonElement Code { get; init; }

    /// <summary>
    /// Human-readable coded value label.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

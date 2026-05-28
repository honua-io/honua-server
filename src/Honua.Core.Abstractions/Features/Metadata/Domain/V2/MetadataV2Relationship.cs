// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Metadata v2 representation of a layer-to-layer relationship. Mirrors the v1
/// <c>Relationship</c> record but keys both sides on <see cref="MetadataV2Resource"/>
/// identifiers (not integer layer ids) so the canonical graph can express
/// relationships independently of any service-local numbering.
/// </summary>
/// <remarks>
/// V2 protocol handlers (OData $expand, GeoServices FeatureServer related-records,
/// WFS join queries) read this off <see cref="MetadataV2Resource.Relationships"/>.
/// The publication's <see cref="MetadataV2Publication.LayerIndex"/> is used by
/// callers that need the integer layer id of the related resource (looked up via
/// the resource → publication index on the snapshot).
/// </remarks>
public sealed record MetadataV2Relationship
{
    /// <summary>
    /// Stable identifier for this relationship within the resource. Esri client
    /// compatibility: when callers need a numeric relationship id, pass
    /// <see cref="EsriRelationshipId"/>.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable relationship name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Identifier of the canonical resource on the other side of this relationship.
    /// </summary>
    [JsonPropertyName("relatedResourceId")]
    public string RelatedResourceId { get; init; } = string.Empty;

    /// <summary>
    /// Role of this side of the relationship — typically <c>origin</c> or
    /// <c>destination</c> (Esri-style). Free-form to allow other vocabularies.
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>
    /// Cardinality (<c>one-to-one</c> / <c>one-to-many</c> / <c>many-to-many</c>).
    /// </summary>
    [JsonPropertyName("cardinality")]
    public string Cardinality { get; init; } = "one-to-many";

    /// <summary>
    /// Field on this resource whose value identifies the foreign key.
    /// </summary>
    [JsonPropertyName("originField")]
    public string OriginField { get; init; } = string.Empty;

    /// <summary>
    /// Field on the related resource that the foreign key points to.
    /// </summary>
    [JsonPropertyName("destinationField")]
    public string DestinationField { get; init; } = string.Empty;

    /// <summary>
    /// Optional integer relationship id used by Esri-style protocols
    /// (GeoServices FeatureServer query/relatedRecords). When unset, callers may
    /// derive a stable integer from a hash of <see cref="Id"/>.
    /// </summary>
    [JsonPropertyName("esriRelationshipId")]
    public int? EsriRelationshipId { get; init; }
}

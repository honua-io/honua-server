// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// A directed provenance edge linking one Console content item to another related
/// artifact (catalog resource, published service, Studio artifact, generated app).
/// </summary>
public sealed record ConsoleProvenanceRef
{
    /// <summary>
    /// Canonical artifact kind on the far side of the edge.
    /// </summary>
    /// <remarks>
    /// Well-known values: <c>catalog-resource</c>, <c>published-service</c>,
    /// <c>studio-artifact</c>, <c>generated-app</c>. Free-form strings are
    /// permitted so adapters can model proprietary lineage.
    /// </remarks>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>
    /// Identifier of the linked artifact.
    /// </summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>
    /// Relationship verb describing how this item relates to <see cref="ItemId"/>.
    /// </summary>
    /// <remarks>
    /// Well-known values: <c>derived-from</c>, <c>publishes</c>, <c>generated-by</c>,
    /// <c>input-of</c>.
    /// </remarks>
    [JsonPropertyName("rel")]
    public required string Rel { get; init; }

    /// <summary>
    /// Optional namespace qualifier for <see cref="ItemId"/>.
    /// </summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }
}

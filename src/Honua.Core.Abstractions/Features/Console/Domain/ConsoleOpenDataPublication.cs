// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Lifecycle status of an item's STAC publication. STAC publication is the
/// catalog-facing projection of an open-data dataset and is controlled
/// independently of the editable open-data page.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleStacPublicationStatus>))]
public enum ConsoleStacPublicationStatus
{
    /// <summary>The item has never been published to the STAC catalog.</summary>
    [JsonStringEnumMemberName("unpublished")]
    Unpublished,

    /// <summary>The item is published and discoverable in the STAC catalog.</summary>
    [JsonStringEnumMemberName("published")]
    Published,
}

/// <summary>
/// Server-owned STAC publication state for a content item. Persisted; projected
/// to the wire via <see cref="ConsoleStacPublicationState"/>-shaped responses and
/// the anonymous STAC collection/item projections.
/// </summary>
public sealed record ConsoleStacPublicationState
{
    /// <summary>Content item id this publication state belongs to.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>Current publication status.</summary>
    [JsonPropertyName("status")]
    public ConsoleStacPublicationStatus Status { get; init; } = ConsoleStacPublicationStatus.Unpublished;

    /// <summary>
    /// Stable STAC collection id assigned at first publish. Null until first
    /// published; retained after unpublish so re-publish is stable.
    /// </summary>
    [JsonPropertyName("collectionId")]
    public string? CollectionId { get; init; }

    /// <summary>Monotonic publication revision, incremented on each publish/update.</summary>
    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    /// <summary>Timestamp the item was first published, when published at least once.</summary>
    [JsonPropertyName("firstPublishedAt")]
    public DateTimeOffset? FirstPublishedAt { get; init; }

    /// <summary>Timestamp the publication state was last changed.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Principal that last changed the publication state (audit).</summary>
    [JsonPropertyName("updatedById")]
    public string? UpdatedById { get; init; }
}

/// <summary>
/// Severity of an open-data / DCAT validation finding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleOpenDataValidationSeverity>))]
public enum ConsoleOpenDataValidationSeverity
{
    /// <summary>Blocks a compliant export (a required field is missing/invalid).</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>Recommended-but-not-required field is missing; export still valid.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,
}

/// <summary>
/// A single open-data / DCAT validation finding, addressable by field.
/// </summary>
public sealed record ConsoleOpenDataValidationIssue
{
    /// <summary>Dotted field path the finding applies to (e.g. <c>publisher.name</c>).</summary>
    [JsonPropertyName("field")]
    public required string Field { get; init; }

    /// <summary>Finding severity.</summary>
    [JsonPropertyName("severity")]
    public required ConsoleOpenDataValidationSeverity Severity { get; init; }

    /// <summary>Stable, human-readable explanation of the finding.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// Aggregate validation result for an item's DCAT/data.json export readiness.
/// </summary>
public sealed record ConsoleOpenDataValidationResult
{
    /// <summary>True when there are no <see cref="ConsoleOpenDataValidationSeverity.Error"/> findings.</summary>
    [JsonPropertyName("isValid")]
    public required bool IsValid { get; init; }

    /// <summary>All validation findings (errors and warnings).</summary>
    [JsonPropertyName("issues")]
    public IReadOnlyList<ConsoleOpenDataValidationIssue> Issues { get; init; } = Array.Empty<ConsoleOpenDataValidationIssue>();
}

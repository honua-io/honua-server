// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Closed time range carried on Metadata v2 resources. Both endpoints are nullable —
/// an open-ended interval (e.g. "data from 2020-01-01 onward") is expressed as
/// <c>End == null</c>, and "any" is expressed as both ends null.
/// </summary>
public sealed record MetadataV2TimeRange
{
    /// <summary>Inclusive start of the temporal extent, or <c>null</c> for open-start.</summary>
    [JsonPropertyName("start")]
    public DateTimeOffset? Start { get; init; }

    /// <summary>Inclusive end of the temporal extent, or <c>null</c> for open-end.</summary>
    [JsonPropertyName("end")]
    public DateTimeOffset? End { get; init; }
}

/// <summary>
/// Typed temporal metadata for a Metadata v2 resource. Replaces the prior
/// <c>JsonElement?</c> slot — every consumer needed the same three facts (start
/// field name, optional end field name, optional track id field) plus an optional
/// declared extent, so they belong on the typed model.
/// </summary>
/// <remarks>
/// Field naming conventions used by the runtime resolvers:
/// <list type="bullet">
/// <item><see cref="StartTimeField"/> is the schema field carrying the start
///   instant for each row. It must resolve to a
///   <see cref="MetadataV2FieldType.Date"/> /
///   <see cref="MetadataV2FieldType.DateTime"/> /
///   <see cref="MetadataV2FieldType.Time"/> schema field; otherwise the resource is
///   treated as non-temporal.</item>
/// <item><see cref="EndTimeField"/> is the end-instant field when the row models
///   a time interval. When unset, rows are treated as instantaneous.</item>
/// <item><see cref="TrackIdField"/> groups rows into trajectories (Esri-style
///   time tracks). Used by FeatureServer / MapServer time-track responses.</item>
/// </list>
/// </remarks>
public sealed record MetadataV2ResourceTemporal
{
    /// <summary>Name of the start-time schema field.</summary>
    [JsonPropertyName("startTimeField")]
    public string? StartTimeField { get; init; }

    /// <summary>Name of the end-time schema field; unset for instantaneous rows.</summary>
    [JsonPropertyName("endTimeField")]
    public string? EndTimeField { get; init; }

    /// <summary>Name of the schema field grouping rows into trajectories.</summary>
    [JsonPropertyName("trackIdField")]
    public string? TrackIdField { get; init; }

    /// <summary>
    /// Optional declared temporal extent. Capability documents prefer this static
    /// value when present; runtime resolvers compute the live extent against the
    /// underlying store when it is not.
    /// </summary>
    [JsonPropertyName("extent")]
    public MetadataV2TimeRange? Extent { get; init; }
}

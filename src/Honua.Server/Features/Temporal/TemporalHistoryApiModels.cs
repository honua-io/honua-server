// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Temporal;

/// <summary>
/// Capability descriptor returned by the temporal capability-discovery endpoint (slice 1 of #1166).
/// </summary>
public sealed class TemporalCapabilityResponse
{
    /// <summary>Owning service id.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer index.</summary>
    public required int LayerId { get; init; }

    /// <summary>Human-readable layer name when available.</summary>
    public string? LayerName { get; init; }

    /// <summary>True when the layer can answer temporal history reads.</summary>
    public required bool SupportsHistory { get; init; }

    /// <summary>True when an as-of read can be served (slice 1).</summary>
    public required bool SupportsAsOf { get; init; }

    /// <summary>The temporal column on the storage mapping, when present (diagnostics only).</summary>
    public string? TemporalColumn { get; init; }

    /// <summary>The cursor kind clients should use for as-of reads.</summary>
    public required string CursorKind { get; init; }

    /// <summary>Current change-tracker generation when history is supported.</summary>
    public long? CurrentGeneration { get; init; }

    /// <summary>Capabilities recognized by the contract but deferred to later slices.</summary>
    public required TemporalDeferredCapabilitiesResponse Deferred { get; init; }
}

/// <summary>
/// Capabilities deferred to later slices of #1166. All values are <c>false</c> in slice 1.
/// </summary>
public sealed class TemporalDeferredCapabilitiesResponse
{
    /// <summary>Diff between two checkpoints (deferred).</summary>
    public required bool SupportsDiff { get; init; }

    /// <summary>Per-feature timeline reads (deferred).</summary>
    public required bool SupportsTimeline { get; init; }

    /// <summary>Actor/source/operation attribution (deferred).</summary>
    public required bool SupportsAttribution { get; init; }

    /// <summary>Governed rollback via the job runner (deferred).</summary>
    public required bool SupportsRollback { get; init; }
}

/// <summary>
/// As-of read result returned by the temporal as-of endpoint (slice 1 of #1166).
/// </summary>
public sealed class TemporalAsOfResponse
{
    /// <summary>Owning service id.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer index.</summary>
    public required int LayerId { get; init; }

    /// <summary>Cursor kind the client requested.</summary>
    public required string RequestedCursorKind { get; init; }

    /// <summary>Generation the cursor resolved to.</summary>
    public required long ResolvedGeneration { get; init; }

    /// <summary>Current change-tracker generation at read time.</summary>
    public required long CurrentGeneration { get; init; }

    /// <summary>Collapsed net feature changes since the resolved cursor.</summary>
    public required IReadOnlyList<TemporalFeatureStateResponse> Features { get; init; }
}

/// <summary>
/// A single feature's net state in an as-of read.
/// </summary>
public sealed class TemporalFeatureStateResponse
{
    /// <summary>Stable object id.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Net operation since the cursor: insert, update, or delete.</summary>
    public required string Operation { get; init; }

    /// <summary>Timestamp of the most recent change, in UTC ISO-8601.</summary>
    public required string ChangedAt { get; init; }

    /// <summary>Current attribute snapshot for surviving features; null for deletes.</summary>
    public IReadOnlyDictionary<string, object?>? Attributes { get; init; }
}

/// <summary>
/// Source-generated JSON context for the temporal history admin API (AOT).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TemporalCapabilityResponse))]
[JsonSerializable(typeof(TemporalDeferredCapabilitiesResponse))]
[JsonSerializable(typeof(TemporalAsOfResponse))]
[JsonSerializable(typeof(TemporalFeatureStateResponse))]
[JsonSerializable(typeof(ProblemDetailsResponse))]
// The feature attribute bag flows through as IReadOnlyDictionary<string, object?>; register the
// concrete dictionary plus the primitive value types so the polymorphic serializer can emit them
// under AOT (mirrors SpatialAnalyticsJsonContext).
[JsonSerializable(typeof(IReadOnlyDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class TemporalHistoryApiJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

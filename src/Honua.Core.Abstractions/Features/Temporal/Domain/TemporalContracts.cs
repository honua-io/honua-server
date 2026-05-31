// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Temporal.Domain;

/// <summary>
/// Describes whether a published layer/resource supports temporal history reads and which
/// temporal primitives back that support. This is the slice-1 capability contract: it reports
/// discovery only (as-of read availability) and explicitly defers diff/timeline/attribution/rollback
/// to later slices so clients can negotiate capability without depending on those endpoints existing.
/// </summary>
/// <param name="ServiceId">Owning Metadata v2 service id.</param>
/// <param name="LayerId">Service-local layer index addressed by the request.</param>
/// <param name="LayerName">Human-readable resource name when available.</param>
/// <param name="SupportsHistory">
/// True when the layer can answer history reads. Requires both a configured temporal column on the
/// storage mapping and a non-no-op change tracker for the backing provider.
/// </param>
/// <param name="SupportsAsOf">True when an as-of read can be served for this layer (slice 1).</param>
/// <param name="TemporalColumn">
/// The temporal column declared on the storage mapping, when present. Surfaced for diagnostics only;
/// the physical column name is not load-bearing for clients.
/// </param>
/// <param name="CursorKind">The temporal cursor kind clients may use for as-of reads (slice 1: generation).</param>
/// <param name="CurrentGeneration">Current change-tracker generation, when history is supported.</param>
/// <param name="DeferredCapabilities">
/// Capabilities recognized by the contract but not yet implemented in this slice (diff, timeline,
/// attribution, rollback). Reported as <c>false</c> so clients negotiate forward-compatibly.
/// </param>
public sealed record TemporalCapability(
    string ServiceId,
    int LayerId,
    string? LayerName,
    bool SupportsHistory,
    bool SupportsAsOf,
    string? TemporalColumn,
    TemporalCursorKind CursorKind,
    long? CurrentGeneration,
    TemporalDeferredCapabilities DeferredCapabilities);

/// <summary>
/// Capabilities recognized by the temporal contract but deferred to later slices of honua-server#1166.
/// All values are <c>false</c> in slice 1; clients must not assume these endpoints exist yet.
/// </summary>
/// <param name="SupportsDiff">Diff between two checkpoints (deferred).</param>
/// <param name="SupportsTimeline">Per-feature timeline reads (deferred).</param>
/// <param name="SupportsAttribution">Actor/source/operation attribution (deferred).</param>
/// <param name="SupportsRollback">Governed rollback via the job runner (deferred).</param>
public sealed record TemporalDeferredCapabilities(
    bool SupportsDiff = false,
    bool SupportsTimeline = false,
    bool SupportsAttribution = false,
    bool SupportsRollback = false);

/// <summary>
/// Identifies the kind of temporal cursor a client supplied or that the server resolved.
/// Slice 1 supports <see cref="Generation"/>; <see cref="Timestamp"/> is resolved to a generation.
/// </summary>
public enum TemporalCursorKind
{
    /// <summary>Monotonic change-tracker generation (the slice-1 canonical cursor).</summary>
    Generation = 0,

    /// <summary>Wall-clock timestamp, resolved against the temporal column / change log.</summary>
    Timestamp = 1
}

/// <summary>
/// A single feature's state in an as-of read, expressed as the net change since the requested cursor.
/// Geometry is intentionally omitted in slice 1: the as-of read reports identity, net operation, and
/// the feature's current attribute snapshot for surviving features, built on the existing change tracker.
/// </summary>
/// <param name="ObjectId">Stable object id of the feature.</param>
/// <param name="NetOperation">Collapsed net operation since the requested cursor (insert/update/delete).</param>
/// <param name="ChangedAtUtc">Timestamp of the most recent change, in UTC.</param>
/// <param name="Attributes">
/// Current attribute snapshot for surviving features (insert/update). Null for deleted features, since
/// the change tracker does not retain pre-delete attribute state in this slice.
/// </param>
public sealed record TemporalFeatureState(
    long ObjectId,
    TemporalChangeKind NetOperation,
    DateTimeOffset ChangedAtUtc,
    IReadOnlyDictionary<string, object?>? Attributes);

/// <summary>
/// Net change kind reported by an as-of read. Mirrors the change tracker's collapsed operation set.
/// </summary>
public enum TemporalChangeKind
{
    /// <summary>Feature was inserted (net) since the cursor.</summary>
    Insert = 1,

    /// <summary>Feature was updated (net) since the cursor.</summary>
    Update = 2,

    /// <summary>Feature was deleted (net) since the cursor.</summary>
    Delete = 3
}

/// <summary>
/// Result of an as-of read: the resolved cursor, the current generation, and the collapsed set of
/// features changed since the requested cursor. An empty <see cref="Features"/> set with a resolved
/// cursor means the layer is unchanged as-of that point (a deterministic, valid result).
/// </summary>
/// <param name="ServiceId">Owning service id.</param>
/// <param name="LayerId">Service-local layer index.</param>
/// <param name="RequestedCursorKind">Cursor kind the client requested.</param>
/// <param name="ResolvedGeneration">Generation the cursor resolved to.</param>
/// <param name="CurrentGeneration">Current change-tracker generation at read time.</param>
/// <param name="Features">Collapsed net changes since the resolved cursor.</param>
public sealed record TemporalAsOfResult(
    string ServiceId,
    int LayerId,
    TemporalCursorKind RequestedCursorKind,
    long ResolvedGeneration,
    long CurrentGeneration,
    ImmutableArray<TemporalFeatureState> Features);

/// <summary>
/// A normalized as-of read request. Exactly one of <see cref="Generation"/> or <see cref="Timestamp"/>
/// should be supplied by the client; the service resolves either to a generation cursor.
/// </summary>
/// <param name="Generation">Generation cursor (changes since this generation, exclusive).</param>
/// <param name="Timestamp">Timestamp cursor, resolved to a generation by the service.</param>
/// <param name="Limit">Optional cap on returned feature states.</param>
public sealed record TemporalAsOfRequest(
    long? Generation = null,
    DateTimeOffset? Timestamp = null,
    int? Limit = null);

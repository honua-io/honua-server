// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Temporal.Domain;

/// <summary>
/// A single revision of a feature in its history timeline (slice 3 of honua-server#1166). Each revision
/// pairs the change-tracker generation and timestamp with the operation that produced it and the
/// attribution for that change (slice 4). Attribute snapshots are subject to the masking policy.
/// </summary>
/// <param name="Generation">The change-tracker generation at which this revision was recorded.</param>
/// <param name="Operation">The operation that produced the revision (insert/update/delete).</param>
/// <param name="ChangedAtUtc">When the revision was recorded, in UTC.</param>
/// <param name="Attribution">Attribution for the revision when recorded (slice 4); null when unavailable.</param>
public sealed record TemporalRevision(
    long Generation,
    TemporalChangeKind Operation,
    DateTimeOffset ChangedAtUtc,
    TemporalAttribution? Attribution);

/// <summary>
/// A feature's revision timeline across history (slice 3 of honua-server#1166): the ordered set of
/// revisions for a single object id, oldest first. An empty <see cref="Revisions"/> set means the feature
/// has no recorded history (it predates change tracking or never changed).
/// </summary>
/// <param name="ServiceId">Owning service id.</param>
/// <param name="LayerId">Service-local layer index.</param>
/// <param name="ObjectId">Stable object id of the feature.</param>
/// <param name="CurrentGeneration">Current change-tracker generation at read time.</param>
/// <param name="Revisions">Ordered revisions (oldest first), subject to the masking policy.</param>
/// <param name="NextCursor">Opaque cursor for the next page, or null when the page is the last one.</param>
public sealed record TemporalTimeline(
    string ServiceId,
    int LayerId,
    long ObjectId,
    long CurrentGeneration,
    ImmutableArray<TemporalRevision> Revisions,
    string? NextCursor);

/// <summary>
/// A normalized feature-timeline request (slice 3 of honua-server#1166).
/// </summary>
/// <param name="ObjectId">Stable object id whose timeline is requested.</param>
/// <param name="Limit">Optional page size cap for returned revisions.</param>
/// <param name="Cursor">Opaque page cursor returned by a prior timeline call.</param>
public sealed record TemporalTimelineRequest(
    long ObjectId,
    int? Limit = null,
    string? Cursor = null);

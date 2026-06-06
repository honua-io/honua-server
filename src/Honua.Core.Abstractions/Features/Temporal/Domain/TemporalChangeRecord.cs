// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Domain;

/// <summary>
/// A single uncollapsed change-log row for a layer, enriched with attribution (slice 4). Unlike the
/// collapsed <see cref="FeatureStore.Domain.FeatureChange"/> used by replication, this preserves every
/// individual revision so the diff and timeline surfaces can report ordered per-feature history. Attribute
/// and geometry snapshots are not stored on the change row; diff value detail is resolved separately when
/// the provider supports it.
/// </summary>
/// <param name="Generation">The change-tracker generation at which the change was recorded.</param>
/// <param name="ObjectId">Stable object id of the changed feature.</param>
/// <param name="Operation">The operation that produced the change.</param>
/// <param name="ChangedAtUtc">When the change was recorded, in UTC.</param>
/// <param name="Attribution">Attribution recorded alongside the change (slice 4); null when unavailable.</param>
public sealed record TemporalChangeRecord(
    long Generation,
    long ObjectId,
    TemporalChangeKind Operation,
    DateTimeOffset ChangedAtUtc,
    TemporalAttribution? Attribution);

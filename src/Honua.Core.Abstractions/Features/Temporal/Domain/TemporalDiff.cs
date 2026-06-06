// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Temporal.Domain;

/// <summary>
/// Classification of a feature-level change between two checkpoints (slice 2 of honua-server#1166).
/// </summary>
public enum TemporalDiffChangeClass
{
    /// <summary>The feature exists at the target checkpoint but not at the base checkpoint.</summary>
    Added = 1,

    /// <summary>The feature exists at the base checkpoint but not at the target checkpoint.</summary>
    Removed = 2,

    /// <summary>The feature exists at both checkpoints and at least one non-geometry attribute changed.</summary>
    AttributeChanged = 3,

    /// <summary>The feature exists at both checkpoints and its geometry changed.</summary>
    GeometryChanged = 4
}

/// <summary>
/// A single field-level change detail within a feature diff. Values are masked when the field is
/// redacted by the temporal masking policy.
/// </summary>
/// <param name="Field">The attribute name that changed.</param>
/// <param name="OldValue">The value at the base checkpoint (null when added or when masked).</param>
/// <param name="NewValue">The value at the target checkpoint (null when removed or when masked).</param>
/// <param name="Masked">True when the field is redacted by the masking policy and values are withheld.</param>
public sealed record TemporalFieldChange(
    string Field,
    object? OldValue,
    object? NewValue,
    bool Masked = false);

/// <summary>
/// A single feature's classified change between two checkpoints, with field-level detail and geometry
/// change indicators. Multiple change classes can apply (for example both attribute and geometry
/// changed); <see cref="Classes"/> reports every applicable class while <see cref="PrimaryClass"/> gives
/// the dominant one for summary grouping.
/// </summary>
/// <param name="ObjectId">Stable object id of the feature.</param>
/// <param name="PrimaryClass">The dominant change class used for summary counts.</param>
/// <param name="Classes">All change classes that apply to this feature.</param>
/// <param name="GeometryChanged">True when the feature's geometry changed between the checkpoints.</param>
/// <param name="FieldChanges">Field-level attribute changes (empty for pure geometry/add/remove changes).</param>
/// <param name="Attribution">Attribution for the change that produced the target state, when recorded (slice 4).</param>
public sealed record TemporalFeatureDiff(
    long ObjectId,
    TemporalDiffChangeClass PrimaryClass,
    ImmutableArray<TemporalDiffChangeClass> Classes,
    bool GeometryChanged,
    ImmutableArray<TemporalFieldChange> FieldChanges,
    TemporalAttribution? Attribution = null);

/// <summary>
/// Summary counts for a temporal diff, grouped by change class. The counts cover the full diff, not just
/// the returned page.
/// </summary>
/// <param name="Added">Number of features added between the checkpoints.</param>
/// <param name="Removed">Number of features removed between the checkpoints.</param>
/// <param name="AttributeChanged">Number of features whose attributes changed.</param>
/// <param name="GeometryChanged">Number of features whose geometry changed.</param>
/// <param name="Total">Total number of changed features (distinct object ids).</param>
public sealed record TemporalDiffSummary(
    int Added,
    int Removed,
    int AttributeChanged,
    int GeometryChanged,
    int Total);

/// <summary>
/// Result of a temporal diff between two checkpoints (slice 2 of honua-server#1166): summary counts plus
/// a page of classified feature changes. An empty <see cref="Changes"/> set with a zeroed summary means
/// the layer is identical between the two checkpoints.
/// </summary>
/// <param name="ServiceId">Owning service id.</param>
/// <param name="LayerId">Service-local layer index.</param>
/// <param name="FromCheckpoint">The resolved base checkpoint.</param>
/// <param name="ToCheckpoint">The resolved target checkpoint.</param>
/// <param name="Summary">Summary counts across the full diff.</param>
/// <param name="Changes">A page of classified feature changes ordered by object id.</param>
/// <param name="NextCursor">Opaque cursor for the next page, or null when the page is the last one.</param>
public sealed record TemporalDiffResult(
    string ServiceId,
    int LayerId,
    ResolvedTemporalCheckpoint FromCheckpoint,
    ResolvedTemporalCheckpoint ToCheckpoint,
    TemporalDiffSummary Summary,
    ImmutableArray<TemporalFeatureDiff> Changes,
    string? NextCursor);

/// <summary>
/// A normalized temporal diff request between two checkpoints. Exactly one base and one target
/// checkpoint must be supplied; the target defaults to the current generation when omitted.
/// </summary>
/// <param name="From">The base checkpoint to diff from.</param>
/// <param name="To">The target checkpoint to diff to; null means the current generation.</param>
/// <param name="Limit">Optional page size cap for returned feature changes.</param>
/// <param name="Cursor">Opaque page cursor returned by a prior diff call.</param>
public sealed record TemporalDiffRequest(
    TemporalCheckpoint From,
    TemporalCheckpoint? To = null,
    int? Limit = null,
    string? Cursor = null);

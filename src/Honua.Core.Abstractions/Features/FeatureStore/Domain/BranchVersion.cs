// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// A named branch version registered for branch-versioned editing. A branch version
/// isolates edits and reads for a single base storage layer from the implicit
/// <c>DEFAULT</c> version by routing feature rows to a distinct synthetic storage
/// layer id within the shared feature store. Because the canonical query, edit, and
/// change-tracking pipelines are all keyed on the storage layer id, branch versions
/// participate in incremental replication and change tracking without any additional
/// branch-specific plumbing.
/// </summary>
public readonly record struct BranchVersion
{
    /// <summary>
    /// Feature service the branch version belongs to.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Case-insensitive branch version name as supplied by clients in the
    /// <c>gdbVersion</c> parameter (for example <c>field-edits</c> or
    /// <c>owner.field-edits</c>). The reserved name <c>DEFAULT</c> (and the
    /// <c>sde.DEFAULT</c> alias) is never persisted; it always resolves to the
    /// base storage layer id.
    /// </summary>
    public required string VersionName { get; init; }

    /// <summary>
    /// Base (DEFAULT) storage layer id the branch version was forked from. Reads and
    /// edits against DEFAULT continue to target this layer id.
    /// </summary>
    public required int BaseLayerId { get; init; }

    /// <summary>
    /// Synthetic storage layer id that isolates this branch version's feature rows from
    /// DEFAULT within the shared feature store. Reads and edits against the named version
    /// target this layer id.
    /// </summary>
    public required int BranchLayerId { get; init; }

    /// <summary>
    /// Timestamp when the branch version was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

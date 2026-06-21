// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Full persisted record of an addressable network dataset, including the editing /
/// audit metadata not needed by the read-only solve path (#1882 editing surface).
/// <see cref="NetworkDataset"/> is the lean projection the routing provider resolves
/// table names from; this record is what the admin editing surface lists, registers,
/// and updates.
/// </summary>
/// <param name="Id">Stable identifier (lowercase slug, e.g. <c>"default"</c>).</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="EdgeTable">Schema-qualified pgRouting edge table name.</param>
/// <param name="VertexTable">Schema-qualified pgRouting vertex table name.</param>
/// <param name="Srid">Spatial reference (SRID) of the topology geometries.</param>
/// <param name="Status">
/// Lifecycle status: <c>active</c>, <c>building</c>, or <c>disabled</c>.
/// </param>
/// <param name="TopologyVersion">Monotonic topology version.</param>
/// <param name="NeedsRebuild">
/// Whether the topology needs a rebuild after edits. The registry editing surface does
/// not rebuild topology (deferred); this flag is surfaced read-only for callers.
/// </param>
/// <param name="Description">Optional operator note.</param>
/// <param name="CreatedAt">Registration timestamp.</param>
/// <param name="UpdatedAt">Last-update timestamp.</param>
/// <param name="CreatedBy">Admin identity that registered the dataset.</param>
/// <param name="UpdatedBy">Admin identity that last updated the dataset.</param>
public sealed record NetworkDatasetRecord(
    string Id,
    string Name,
    string EdgeTable,
    string VertexTable,
    int Srid,
    string Status,
    int TopologyVersion,
    bool NeedsRebuild,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy);

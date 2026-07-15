// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Provider-neutral mutation vocabulary for a routing edge. This contract identifies
/// topology content without exposing provider table names or proprietary network types.
/// </summary>
/// <param name="EdgeId">Stable edge identifier within the dataset.</param>
/// <param name="SourceVertexId">Stable source-vertex identifier.</param>
/// <param name="TargetVertexId">Stable target-vertex identifier.</param>
/// <param name="GeometryGeoJson">GeoJSON geometry in the declared <paramref name="Srid"/>.</param>
/// <param name="Srid">Spatial reference of the geometry.</param>
/// <param name="Attributes">Allowlisted, protocol-neutral routing attributes.</param>
public sealed record NetworkEdgeEdit(
    string EdgeId,
    string SourceVertexId,
    string TargetVertexId,
    string GeometryGeoJson,
    int Srid,
    IReadOnlyDictionary<string, string?> Attributes);

/// <summary>
/// Type of a provider-neutral turn restriction.
/// </summary>
public enum NetworkTurnRestrictionKind
{
    /// <summary>The described turn is prohibited.</summary>
    Prohibited,

    /// <summary>Only the described turn is permitted through the via vertex.</summary>
    Required,

    /// <summary>The described turn incurs an additional non-negative impedance.</summary>
    Penalty,
}

/// <summary>
/// Provider-neutral mutation vocabulary for a turn restriction.
/// </summary>
/// <param name="RestrictionId">Stable restriction identifier within the dataset.</param>
/// <param name="FromEdgeId">Stable incoming edge identifier.</param>
/// <param name="ViaVertexId">Stable connecting vertex identifier.</param>
/// <param name="ToEdgeId">Stable outgoing edge identifier.</param>
/// <param name="Kind">Restriction behavior.</param>
/// <param name="Penalty">Optional non-negative impedance used only for penalty restrictions.</param>
/// <param name="Attributes">Allowlisted, protocol-neutral restriction attributes.</param>
public sealed record NetworkTurnRestrictionEdit(
    string RestrictionId,
    string FromEdgeId,
    string ViaVertexId,
    string ToEdgeId,
    NetworkTurnRestrictionKind Kind,
    double? Penalty,
    IReadOnlyDictionary<string, string?> Attributes);

/// <summary>
/// A batched, all-or-nothing edge and turn-restriction mutation request targeting one
/// non-active (<c>draft</c> or <c>dirty</c>) topology generation (#2716). Every list is
/// optional; an empty batch (every list empty) is rejected by
/// <see cref="NetworkTopologyEditValidation"/> before it reaches storage.
/// </summary>
/// <param name="AddEdges">Edges to insert. Fails the whole batch if an id already exists.</param>
/// <param name="UpdateEdges">Edges to replace in place. Fails the whole batch if an id does not exist.</param>
/// <param name="DeleteEdgeIds">Edge ids to remove. Fails the whole batch if an id does not exist or is still referenced by a turn restriction outside this batch.</param>
/// <param name="AddRestrictions">Turn restrictions to insert. Fails the whole batch if an id already exists or references an unknown edge.</param>
/// <param name="UpdateRestrictions">Turn restrictions to replace in place. Fails the whole batch if an id does not exist or references an unknown edge.</param>
/// <param name="DeleteRestrictionIds">Turn restriction ids to remove. Fails the whole batch if an id does not exist.</param>
public sealed record NetworkTopologyEditBatch(
    IReadOnlyList<NetworkEdgeEdit> AddEdges,
    IReadOnlyList<NetworkEdgeEdit> UpdateEdges,
    IReadOnlyList<string> DeleteEdgeIds,
    IReadOnlyList<NetworkTurnRestrictionEdit> AddRestrictions,
    IReadOnlyList<NetworkTurnRestrictionEdit> UpdateRestrictions,
    IReadOnlyList<string> DeleteRestrictionIds)
{
    /// <summary>Returns an empty batch (no edits in any list).</summary>
    public static NetworkTopologyEditBatch Empty { get; } = new([], [], [], [], [], []);

    /// <summary>Total number of edge mutation items across add/update/delete.</summary>
    public int EdgeItemCount => AddEdges.Count + UpdateEdges.Count + DeleteEdgeIds.Count;

    /// <summary>Total number of turn-restriction mutation items across add/update/delete.</summary>
    public int RestrictionItemCount => AddRestrictions.Count + UpdateRestrictions.Count + DeleteRestrictionIds.Count;

    /// <summary>Returns whether every list in the batch is empty.</summary>
    public bool IsEmpty => EdgeItemCount == 0 && RestrictionItemCount == 0;
}

/// <summary>
/// Outcome of a successful <see cref="NetworkTopologyEditBatch"/> mutation: the resulting
/// generation metadata plus per-list counts. Never carries geometry, attributes, or other
/// edited content — only counts and identifiers safe for audit/telemetry (#2716).
/// </summary>
/// <param name="DatasetId">Stable network-dataset identifier.</param>
/// <param name="Generation">Generation number the batch was applied to.</param>
/// <param name="SourceRevision">Content revision after the mutation.</param>
/// <param name="RowVersion">Compare-and-swap version after the mutation.</param>
/// <param name="State">Lifecycle state after the mutation (always <c>dirty</c> on success).</param>
/// <param name="EdgesAdded">Number of edges inserted.</param>
/// <param name="EdgesUpdated">Number of edges replaced.</param>
/// <param name="EdgesDeleted">Number of edges removed.</param>
/// <param name="RestrictionsAdded">Number of turn restrictions inserted.</param>
/// <param name="RestrictionsUpdated">Number of turn restrictions replaced.</param>
/// <param name="RestrictionsDeleted">Number of turn restrictions removed.</param>
/// <param name="WasIdempotentReplay">
/// <see langword="true"/> when this result was replayed from a prior request that used the
/// same idempotency key and an identical payload, rather than freshly applied.
/// </param>
public sealed record NetworkTopologyEditResult(
    string DatasetId,
    long Generation,
    long SourceRevision,
    long RowVersion,
    NetworkTopologyGenerationState State,
    int EdgesAdded,
    int EdgesUpdated,
    int EdgesDeleted,
    int RestrictionsAdded,
    int RestrictionsUpdated,
    int RestrictionsDeleted,
    bool WasIdempotentReplay);

/// <summary>
/// Stable reason a batched topology content edit was rejected before any content mutated
/// (#2716). Mirrors <see cref="NetworkTopologyTransitionFailure"/> for lifecycle
/// transitions, but covers the content-edit compare-and-swap instead.
/// </summary>
public enum NetworkTopologyEditRejection
{
    /// <summary>The edit succeeded.</summary>
    None,

    /// <summary>The caller's expected row version no longer matches persisted state.</summary>
    StaleRowVersion,

    /// <summary>
    /// The generation is not in <c>draft</c> or <c>dirty</c> state, so it cannot accept
    /// content edits (active/ready/building/failed/retired generations reject edits).
    /// </summary>
    GenerationNotEditable,
}

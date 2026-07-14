// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Wire shape for one topology generation returned by the admin editing surface (#2716).
/// </summary>
public sealed class NetworkTopologyGenerationDto
{
    /// <summary>Stable network-dataset id the generation belongs to.</summary>
    public string DatasetId { get; set; } = string.Empty;

    /// <summary>Monotonically increasing generation number within the dataset.</summary>
    public long Generation { get; set; }

    /// <summary>Monotonic content revision the generation was built from (or has accumulated edits toward).</summary>
    public long SourceRevision { get; set; }

    /// <summary>Lifecycle state: <c>draft</c>, <c>dirty</c>, <c>building</c>, <c>ready</c>, <c>active</c>, <c>failed</c>, or <c>retired</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Compare-and-swap version. Send back as <c>If-Match</c> when submitting an edit batch.</summary>
    public long RowVersion { get; set; }

    /// <summary>Spatial reference edge geometry in this generation must be expressed in.</summary>
    public int Srid { get; set; }

    /// <summary>Generation creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last lifecycle or content mutation timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Timestamp the generation became active, if applicable.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>Sanitized stable failure code when <see cref="State"/> is <c>failed</c>.</summary>
    public string? FailureCode { get; set; }
}

/// <summary>
/// Request body for one edge create/update item in a batched topology edit (#2716).
/// </summary>
public sealed class NetworkEdgeEditDto
{
    /// <summary>Stable edge identifier within the dataset.</summary>
    public string? EdgeId { get; set; }

    /// <summary>Stable source-vertex identifier.</summary>
    public string? SourceVertexId { get; set; }

    /// <summary>Stable target-vertex identifier.</summary>
    public string? TargetVertexId { get; set; }

    /// <summary>GeoJSON <c>LineString</c>/<c>MultiLineString</c> geometry in <see cref="Srid"/>.</summary>
    public string? GeometryGeoJson { get; set; }

    /// <summary>Spatial reference of <see cref="GeometryGeoJson"/>. Must match the target generation's SRID.</summary>
    public int? Srid { get; set; }

    /// <summary>
    /// Allowlisted, dataset-backed travel-profile cost attributes (#2655 forward/reverse
    /// cost columns). Any key outside the dataset's declared travel profiles is rejected.
    /// </summary>
    public Dictionary<string, string?>? Attributes { get; set; }
}

/// <summary>
/// Request body for one turn-restriction create/update item in a batched topology edit (#2716).
/// </summary>
public sealed class NetworkTurnRestrictionEditDto
{
    /// <summary>Stable turn-restriction identifier within the dataset.</summary>
    public string? RestrictionId { get; set; }

    /// <summary>Stable incoming edge identifier. Must reference an edge staged in the same generation.</summary>
    public string? FromEdgeId { get; set; }

    /// <summary>Stable connecting vertex identifier.</summary>
    public string? ViaVertexId { get; set; }

    /// <summary>Stable outgoing edge identifier. Must reference an edge staged in the same generation.</summary>
    public string? ToEdgeId { get; set; }

    /// <summary>Restriction behavior: <c>prohibited</c>, <c>required</c>, or <c>penalty</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>Non-negative impedance penalty. Required when <see cref="Kind"/> is <c>penalty</c>; otherwise must be omitted.</summary>
    public double? Penalty { get; set; }

    /// <summary>Allowlisted, protocol-neutral restriction attributes.</summary>
    public Dictionary<string, string?>? Attributes { get; set; }
}

/// <summary>
/// Request body for a batched, all-or-nothing edge and turn-restriction content edit
/// against one non-active topology generation (#2716). Requires an <c>Idempotency-Key</c>
/// header and an <c>If-Match</c> header carrying the generation's current row version.
/// </summary>
public sealed class NetworkTopologyEditBatchRequest
{
    /// <summary>Edges to insert. Fails the whole batch if an id already exists in this generation.</summary>
    public NetworkEdgeEditDto[]? AddEdges { get; set; }

    /// <summary>Edges to replace in place. Fails the whole batch if an id does not exist in this generation.</summary>
    public NetworkEdgeEditDto[]? UpdateEdges { get; set; }

    /// <summary>Edge ids to remove. Fails the whole batch if an id does not exist, or is still referenced by a turn restriction.</summary>
    public string[]? DeleteEdgeIds { get; set; }

    /// <summary>Turn restrictions to insert. Fails the whole batch if an id already exists or references an unknown edge.</summary>
    public NetworkTurnRestrictionEditDto[]? AddRestrictions { get; set; }

    /// <summary>Turn restrictions to replace in place. Fails the whole batch if an id does not exist or references an unknown edge.</summary>
    public NetworkTurnRestrictionEditDto[]? UpdateRestrictions { get; set; }

    /// <summary>Turn restriction ids to remove. Fails the whole batch if an id does not exist.</summary>
    public string[]? DeleteRestrictionIds { get; set; }
}

/// <summary>
/// Response body for a successfully applied (or idempotently replayed) batched topology
/// edit (#2716). Never carries geometry, attribute values, or profile costs.
/// </summary>
public sealed class NetworkTopologyEditResultDto
{
    /// <summary>Stable network-dataset id the batch was applied to.</summary>
    public string DatasetId { get; set; } = string.Empty;

    /// <summary>Generation number the batch was applied to.</summary>
    public long Generation { get; set; }

    /// <summary>Content revision after the mutation.</summary>
    public long SourceRevision { get; set; }

    /// <summary>Compare-and-swap version after the mutation. Use for the next <c>If-Match</c>.</summary>
    public long RowVersion { get; set; }

    /// <summary>Lifecycle state after the mutation (always <c>dirty</c> on success).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Number of edges inserted.</summary>
    public int EdgesAdded { get; set; }

    /// <summary>Number of edges replaced.</summary>
    public int EdgesUpdated { get; set; }

    /// <summary>Number of edges removed.</summary>
    public int EdgesDeleted { get; set; }

    /// <summary>Number of turn restrictions inserted.</summary>
    public int RestrictionsAdded { get; set; }

    /// <summary>Number of turn restrictions replaced.</summary>
    public int RestrictionsUpdated { get; set; }

    /// <summary>Number of turn restrictions removed.</summary>
    public int RestrictionsDeleted { get; set; }

    /// <summary>
    /// <see langword="true"/> when this result was replayed from a prior request that used
    /// the same <c>Idempotency-Key</c> and an identical payload, rather than freshly applied.
    /// </summary>
    public bool WasIdempotentReplay { get; set; }
}

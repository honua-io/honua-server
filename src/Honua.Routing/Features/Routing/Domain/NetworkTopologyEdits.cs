// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public sealed record NetworkEdgeEdit(
    string EdgeId,
    string SourceVertexId,
    string TargetVertexId,
    string GeometryGeoJson,
    int Srid,
    IReadOnlyDictionary<string, string?> Attributes);

public enum NetworkTurnRestrictionKind
{
    Prohibited,

    Required,

    Penalty,
}

public sealed record NetworkTurnRestrictionEdit(
    string RestrictionId,
    string FromEdgeId,
    string ViaVertexId,
    string ToEdgeId,
    NetworkTurnRestrictionKind Kind,
    double? Penalty,
    IReadOnlyDictionary<string, string?> Attributes);

public sealed record NetworkTopologyEditBatch(
    IReadOnlyList<NetworkEdgeEdit> AddEdges,
    IReadOnlyList<NetworkEdgeEdit> UpdateEdges,
    IReadOnlyList<string> DeleteEdgeIds,
    IReadOnlyList<NetworkTurnRestrictionEdit> AddRestrictions,
    IReadOnlyList<NetworkTurnRestrictionEdit> UpdateRestrictions,
    IReadOnlyList<string> DeleteRestrictionIds)
{
    public static NetworkTopologyEditBatch Empty { get; } = new([], [], [], [], [], []);

    public int EdgeItemCount => AddEdges.Count + UpdateEdges.Count + DeleteEdgeIds.Count;

    public int RestrictionItemCount => AddRestrictions.Count + UpdateRestrictions.Count + DeleteRestrictionIds.Count;

    public bool IsEmpty => EdgeItemCount == 0 && RestrictionItemCount == 0;
}

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

public enum NetworkTopologyEditRejection
{
    None,

    StaleRowVersion,

    GenerationNotEditable,
}

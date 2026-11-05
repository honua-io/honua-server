// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class NetworkTopologyGenerationDto
{
    public string DatasetId { get; set; } = string.Empty;

    public long Generation { get; set; }

    public long SourceRevision { get; set; }

    public string State { get; set; } = string.Empty;

    public long RowVersion { get; set; }

    public int Srid { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }

    public string? FailureCode { get; set; }
}

public sealed class NetworkEdgeEditDto
{
    public string? EdgeId { get; set; }

    public string? SourceVertexId { get; set; }

    public string? TargetVertexId { get; set; }

    public string? GeometryGeoJson { get; set; }

    public int? Srid { get; set; }

    public Dictionary<string, string?>? Attributes { get; set; }
}

public sealed class NetworkTurnRestrictionEditDto
{
    public string? RestrictionId { get; set; }

    public string? FromEdgeId { get; set; }

    public string? ViaVertexId { get; set; }

    public string? ToEdgeId { get; set; }

    public string? Kind { get; set; }

    // The penalty is required when the restriction is penalty and must be omitted otherwise.
    public double? Penalty { get; set; }

    public Dictionary<string, string?>? Attributes { get; set; }
}

public sealed class NetworkTopologyEditBatchRequest
{
    public NetworkEdgeEditDto[]? AddEdges { get; set; }

    public NetworkEdgeEditDto[]? UpdateEdges { get; set; }

    public string[]? DeleteEdgeIds { get; set; }

    public NetworkTurnRestrictionEditDto[]? AddRestrictions { get; set; }

    public NetworkTurnRestrictionEditDto[]? UpdateRestrictions { get; set; }

    public string[]? DeleteRestrictionIds { get; set; }
}

// A topology edit response never carries geometry, attribute values, or profile costs.
public sealed class NetworkTopologyEditResultDto
{
    public string DatasetId { get; set; } = string.Empty;

    public long Generation { get; set; }

    public long SourceRevision { get; set; }

    public long RowVersion { get; set; }

    public string State { get; set; } = string.Empty;

    public int EdgesAdded { get; set; }

    public int EdgesUpdated { get; set; }

    public int EdgesDeleted { get; set; }

    public int RestrictionsAdded { get; set; }

    public int RestrictionsUpdated { get; set; }

    public int RestrictionsDeleted { get; set; }

    public bool WasIdempotentReplay { get; set; }
}

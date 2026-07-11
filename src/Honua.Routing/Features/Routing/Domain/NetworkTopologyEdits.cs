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

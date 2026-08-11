// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Serializes the GeoServices client side of a conflicting uploaded edit into the opaque state
/// envelope the durable conflict record carries, so the record is complete the moment it is inserted.
/// </summary>
/// <remarks>
/// The envelope is the same shape the conflict-review surface renders and the resolution planner
/// writes back, produced by <c>FeatureServerEndpoints.SerializeStateEnvelope</c> so the client and
/// server sides of a conflict stay directly comparable (#1287, #2430).
/// </remarks>
internal sealed class FeatureServerReplicaClientStateSerializer : IReplicaClientStateSerializer
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly FeatureServerReplicaClientStateSerializer Instance = new();

    private FeatureServerReplicaClientStateSerializer()
    {
    }

    /// <inheritdoc />
    public string? Serialize(ReplicaUploadEdit edit)
        // Updates only: a delete carries just an object id, and a create has no server identity yet, so
        // neither has a client state a resolution could write back.
        => edit.Kind == FeatureEditOperationKind.Update && edit.Payload is GeoServicesFeature feature
            ? FeatureServerEndpoints.SerializeStateEnvelope(
                feature.Attributes,
                feature.Geometry,
                feature.IncludeGeometry)
            : null;
}

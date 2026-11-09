// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal sealed class FeatureServerReplicaClientStateSerializer : IReplicaClientStateSerializer
{
    public static readonly FeatureServerReplicaClientStateSerializer Instance = new();

    private FeatureServerReplicaClientStateSerializer()
    {
    }

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

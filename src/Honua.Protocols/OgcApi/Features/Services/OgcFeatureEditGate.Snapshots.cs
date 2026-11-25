// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.Ogc.Api.Features.Services;

internal static partial class OgcFeatureEditGate
{
    private static Dictionary<long, Feature>? StoredRows(OgcFeaturesEditRequest request)
    {
        var stored = new Dictionary<long, Feature>();
        if (request is { StoredFeature: { } single, ObjectId: { } objectId })
        {
            stored[objectId] = single;
        }

        foreach (var operation in request.BatchOperations.IsDefault ? [] : request.BatchOperations)
        {
            if (operation is { StoredFeature: { } row, ObjectId: { } id })
            {
                stored[id] = row;
            }
        }

        return stored.Count == 0 ? null : stored;
    }
}

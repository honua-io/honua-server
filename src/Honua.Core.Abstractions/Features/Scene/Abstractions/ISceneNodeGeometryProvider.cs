// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

public interface ISceneNodeGeometryProvider
{
    Task<I3sTranscodedGeometry?> GetNodeGeometryAsync(
        SceneDataset scene,
        int nodeId,
        CancellationToken cancellationToken = default);
}

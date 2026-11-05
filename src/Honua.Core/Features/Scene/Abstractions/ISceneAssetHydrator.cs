// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

// A record with no object-store prefix is served from local disk and is not hydrated.
public interface ISceneAssetHydrator
{
    Task EnsureLocalAsync(
        SceneDatasetRecord record,
        string localAssetRoot,
        CancellationToken cancellationToken = default);
}

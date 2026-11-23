// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

public interface ISceneObjectOriginReader
{
    Task<SceneObjectAssetMetadata?> GetMetadataAsync(
        SceneDataset scene,
        string relativeAssetPath,
        CancellationToken cancellationToken = default);

    Task<SceneObjectAssetReadResult> OpenReadAsync(
        SceneDataset scene,
        string relativeAssetPath,
        SceneByteRange? range = null,
        CancellationToken cancellationToken = default);
}

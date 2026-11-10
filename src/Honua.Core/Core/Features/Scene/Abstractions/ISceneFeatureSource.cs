// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

public interface ISceneFeatureSource
{
    IAsyncEnumerable<SceneFeature> StreamAsync(
        MetadataV2Resource resource,
        int storageLayerId,
        IReadOnlyList<string> includeAttributes,
        CancellationToken cancellationToken = default);
}

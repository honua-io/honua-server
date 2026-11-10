// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

// A retry resumes from the last completed metatile block and regenerates only the failed or not-yet-attempted units.
public interface ITileCacheGenerationCheckpointStore
{
    ValueTask SaveAsync(TileCacheGenerationCheckpoint checkpoint, CancellationToken cancellationToken = default);

    ValueTask<TileCacheGenerationCheckpoint?> LoadAsync(string generationId, CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(string generationId, CancellationToken cancellationToken = default);
}
